import pytest

from extractor.state import ALLOWED, Backoff, CamState, FpsMeter, InvalidTransition, StateMachine

S = CamState


def test_happy_path_and_callbacks():
    seen = []
    sm = StateMachine(lambda o, n: seen.append((o, n)))
    assert sm.state is S.STARTING
    for nxt in (S.ONLINE, S.STALLED, S.RECONNECTING, S.ONLINE, S.RECONNECTING, S.ERROR, S.ONLINE, S.OFFLINE):
        assert sm.to(nxt) is True
    assert seen[0] == (S.STARTING, S.ONLINE) and len(seen) == 8


def test_same_state_is_noop():
    seen = []
    sm = StateMachine(lambda o, n: seen.append(n))
    assert sm.to(S.STARTING) is False and seen == []


@pytest.mark.parametrize("a,b", [
    (a, b) for a in S for b in S if a is not b and b not in ALLOWED[a]
])
def test_invalid_transitions_rejected(a, b):
    sm = StateMachine()
    sm.state = a
    with pytest.raises(InvalidTransition):
        sm.to(b)


def test_offline_only_restarts_via_starting():
    assert ALLOWED[S.OFFLINE] == {S.STARTING}
    assert S.STARTING not in ALLOWED[S.ONLINE]


def test_backoff_sequence_and_cap_no_jitter():
    b = Backoff(jitter=0.0)
    assert [b.next_delay() for _ in range(8)] == [1, 2, 4, 8, 16, 30, 30, 30]
    b.reset()
    assert b.next_delay() == 1


def test_backoff_jitter_bounds():
    lo, hi = Backoff(rng=lambda: 0.0), Backoff(rng=lambda: 1.0)
    for i in range(10):
        base = min(30, 2 ** i)
        d_lo, d_hi = lo.next_delay(), hi.next_delay()
        assert d_lo == pytest.approx(base * 0.8)
        assert d_hi == min(30.0, base * 1.2)
        assert d_hi <= 30


def test_fps_meter_window():
    t = [0.0]
    m = FpsMeter(10.0, lambda: t[0])
    for _ in range(30):  # 3 fps for 10 s
        t[0] += 1 / 3
        m.record()
    assert m.fps() == pytest.approx(3.0, abs=0.35)
    t[0] += 20  # silence
    assert m.fps() == 0.0
