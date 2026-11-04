namespace Executor;

/// <summary>
/// Kafka-agnostic core of a consumer loop: bounded parallelism, stale-message skipping (back-pressure for a live
/// system) and contiguous-offset bookkeeping. The Kafka loop calls <see cref="SubmitAsync"/> for each message IN ORDER;
/// the call blocks (async) while MaxInflight handlers are running, which in turn stops polling -> natural back-pressure.
/// </summary>
public sealed class WorkDispatcher<T>
{
    private readonly int _maxInflight;
    private readonly SemaphoreSlim _slots;
    private readonly long _maxAgeMs;
    private readonly Func<T, long> _tsOf;
    private readonly Func<T, CancellationToken, Task> _handler;
    private readonly OffsetTracker _tracker;
    private readonly Action<int, long> _commit;
    private readonly Action<string> _onSkip;
    private readonly Action<Exception> _onError;
    private readonly Action<int> _onInflight;
    private readonly TimeProvider _time;
    private readonly CancellationToken _handlerCt;
    private int _inflight;
    private readonly Action<T> _onDiscard;   // item dropped without running the handler (superseded / stale): release its buffer

    // ---- per-key conflation (live latency): only the newest message per key (camera) is ever processed -------------
    private sealed record Pend(int P, long O, T Item);
    private readonly Func<T, string?>? _keyOf;
    private readonly object _cl = new();
    private readonly Dictionary<string, Pend> _mail = new();   // newest waiting message per key
    // Two-tier ready queue (contract 9.8): tier 1 = watched keys, tier 2 = the rest. Each ready key sits in exactly one
    // queue. The tier is (re)evaluated whenever a key is enqueued AND when it reaches the head at dispatch time, so a
    // camera that became watched while queued is promoted the moment it surfaces (O(1) per operation, no scans).
    private readonly Queue<string> _ready1 = new();
    private readonly Queue<string> _ready2 = new();
    private readonly Func<string, bool> _isWatched;
    private int _dispatchSeq;
    private readonly Action<T, long> _onDequeue;   // (item, age ms at dispatch time) for latency metrics
    public const int StarvationEvery = 4;
    private readonly HashSet<string> _busy = new();            // keys with a run in flight (kept serial per camera)

    public WorkDispatcher(int maxInflight, long maxAgeMs, Func<T, long> tsOf, Func<T, CancellationToken, Task> handler,
        OffsetTracker tracker, Action<int, long> commit, TimeProvider? time = null, Action<string>? onSkip = null,
        Action<Exception>? onError = null, Action<int>? onInflight = null, CancellationToken handlerCt = default, Func<T, string?>? keyOf = null, Action<T>? onDiscard = null,
        Func<string, bool>? isWatched = null, Action<T, long>? onDequeue = null)
    {
        _onDequeue = onDequeue ?? ((_, _) => { });
        _isWatched = isWatched ?? (_ => false);
        _maxInflight = maxInflight;
        _slots = new SemaphoreSlim(maxInflight, maxInflight);
        _maxAgeMs = maxAgeMs;
        _tsOf = tsOf;
        _handler = handler;
        _tracker = tracker;
        _commit = commit;
        _time = time ?? TimeProvider.System;
        _onSkip = onSkip ?? (_ => { });
        _onError = onError ?? (_ => { });
        _onInflight = onInflight ?? (_ => { });
        _handlerCt = handlerCt;
        _keyOf = keyOf;
        _onDiscard = onDiscard ?? (_ => { });
    }

    public int Inflight => Volatile.Read(ref _inflight);

    /// <summary>Message could not be parsed: count it and let the offset advance.</summary>
    public void Skip(int partition, long offset, string reason)
    {
        _tracker.Register(partition, offset);
        _onSkip(reason);
        Finish(partition, offset);
    }

    public async ValueTask SubmitAsync(int partition, long offset, T item, CancellationToken ct)
    {
        if (_keyOf is not null) { SubmitConflating(partition, offset, item); return; }
        _tracker.Register(partition, offset);
        await _slots.WaitAsync(ct).ConfigureAwait(false);

        // Age is checked AFTER the slot is acquired: time spent waiting for capacity counts as staleness.
        if (_maxAgeMs > 0 && _time.GetUtcNow().ToUnixTimeMilliseconds() - _tsOf(item) > _maxAgeMs)
        {
            _slots.Release();
            _onSkip("stale");
            _onDiscard(item);
            Finish(partition, offset);
            return;
        }

        _onInflight(Interlocked.Increment(ref _inflight));
        _ = RunAsync(partition, offset, item);
    }

    /// <summary>
    /// Never blocks the consume loop: the message replaces any older waiting message of the same key (the older one is
    /// completed immediately as "superseded" so offsets still advance contiguously); free slots are filled round-robin
    /// by waiting keys, newest message each. Memory is bounded by one waiting message per key.
    /// </summary>
    private void SubmitConflating(int partition, long offset, T item)
    {
        var key = _keyOf!(item) ?? "";
        _tracker.Register(partition, offset);
        Pend? old;
        lock (_cl)
        {
            _mail.TryGetValue(key, out old);
            _mail[key] = new Pend(partition, offset, item);
            if (old is null && !_busy.Contains(key)) Enqueue(key);
        }
        if (old is not null) { _onSkip("superseded"); _onDiscard(old.Item); Finish(old.P, old.O); }
        Pump();
    }

    private void Pump()
    {
        while (_slots.Wait(0))
        {
            Pend? w = null;
            string? key = null;
            lock (_cl)
            {
                key = NextReady();
                if (key is not null && _mail.Remove(key, out w)) _busy.Add(key);
            }
            if (w is null) { _slots.Release(); return; }

            var ageMs = _time.GetUtcNow().ToUnixTimeMilliseconds() - _tsOf(w.Item);
            _onDequeue(w.Item, ageMs);
            if (_maxAgeMs > 0 && ageMs > _maxAgeMs)
            {
                _onSkip("stale");
                _onDiscard(w.Item);
                EndKey(key!);
                _slots.Release();
                Finish(w.P, w.O);
                continue;
            }
            _onInflight(Interlocked.Increment(ref _inflight));
            _ = RunAsync(w.P, w.O, w.Item, key);
        }
    }

    private void Enqueue(string key) => (_isWatched(key) ? _ready1 : _ready2).Enqueue(key);

    /// <summary>Caller holds _cl. Every 4th dispatch prefers tier 2 (starvation guard), otherwise tier 1 first.</summary>
    private string? NextReady()
    {
        var preferBackground = (_dispatchSeq + 1) % StarvationEvery == 0;
        var k = preferBackground ? PopBackground() ?? PopWatched() : PopWatched() ?? PopBackground();
        if (k is not null) _dispatchSeq++;
        return k;
    }

    private string? PopWatched()
    {
        // a key queued as watched that no longer is moves down (each key moves at most once per pop)
        while (_ready1.TryDequeue(out var k))
        {
            if (!_mail.ContainsKey(k)) continue;
            if (_isWatched(k)) return k;
            _ready2.Enqueue(k);
        }
        return null;
    }

    private string? PopBackground()
    {
        while (_ready2.TryDequeue(out var k))
            if (_mail.ContainsKey(k)) return k;
        return null;
    }

    private void EndKey(string key)
    {
        lock (_cl)
        {
            _busy.Remove(key);
            if (_mail.ContainsKey(key)) Enqueue(key);
        }
    }

    private async Task RunAsync(int partition, long offset, T item, string? key = null)
    {
        var abandoned = false;
        try
        {
            await _handler(item, _handlerCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_handlerCt.IsCancellationRequested)
        {
            abandoned = true; // shutdown: leave the offset uncommitted so the message is redelivered
        }
        catch (Exception ex)
        {
            _onError(ex); // poison/failed message: logged + counted by the caller, offset still advances (live system)
        }
        finally
        {
            _onInflight(Interlocked.Decrement(ref _inflight));
            if (key is not null) EndKey(key);
            _slots.Release();
            if (!abandoned) Finish(partition, offset);
            if (key is not null) Pump();
        }
    }

    private void Finish(int partition, long offset)
    {
        if (_tracker.Complete(partition, offset) is { } c) _commit(partition, c);
    }

    /// <summary>Waits until all in-flight handlers finished (or the timeout elapses).</summary>
    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        var deadline = _time.GetUtcNow() + timeout;
        while (Inflight > 0)
        {
            if (_time.GetUtcNow() >= deadline) return false;
            await Task.Delay(20).ConfigureAwait(false);
        }
        return true;
    }
}
