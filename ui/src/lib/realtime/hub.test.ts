import { afterEach, describe, expect, it, vi } from 'vitest'
import { Hub } from './hub'
import type { ConnState, HubEvent, HubMethod, HubTransport } from './types'

class FakeTransport implements HubTransport {
  calls: Array<[HubMethod, string[]]> = []
  failFrames = false
  private st: (s: ConnState) => void = () => {}
  async start() {
    this.st('connected')
  }
  async stop() {}
  async invoke(m: HubMethod, ids: string[]) {
    if (this.failFrames && (m === 'WatchFrames' || m === 'UnwatchFrames')) throw new Error('nope')
    this.calls.push([m, ids])
  }
  onEvent(_h: (e: HubEvent) => void) {}
  onState(h: (s: ConnState) => void) {
    this.st = h
  }
  reconnect() {
    this.st('connected')
  }
}

async function setup() {
  const t = new FakeTransport()
  const hub = new Hub(100)
  hub.attach(t)
  await hub.start()
  return { t, hub }
}

describe('hub.watchFrames', () => {
  afterEach(() => vi.useRealTimers())

  it('ref-counts and batches into one WatchFrames call per 100 ms', async () => {
    vi.useFakeTimers()
    const { t, hub } = await setup()
    t.calls.length = 0
    const a = hub.watchFrames('cam-1')
    const b = hub.watchFrames('cam-1')
    hub.watchFrames('cam-2')
    await vi.advanceTimersByTimeAsync(100)
    expect(t.calls).toEqual([['WatchFrames', ['cam-1', 'cam-2']]])

    a()
    a() // idempotent
    await vi.advanceTimersByTimeAsync(100)
    expect(t.calls).toHaveLength(1) // cam-1 still held by b
    b()
    await vi.advanceTimersByTimeAsync(100)
    expect(t.calls[1]).toEqual(['UnwatchFrames', ['cam-1']])
  })

  it('re-sends watched cameras after a reconnect', async () => {
    vi.useFakeTimers()
    const { t, hub } = await setup()
    hub.watchFrames('cam-1')
    await vi.advanceTimersByTimeAsync(100)
    t.calls.length = 0
    t.reconnect()
    await vi.advanceTimersByTimeAsync(10)
    expect(t.calls).toEqual([['WatchFrames', ['cam-1']]])
  })

  it('tolerates a gateway without WatchFrames: logs once, camera subscriptions still work', async () => {
    vi.useFakeTimers()
    const dbg = vi.spyOn(console, 'debug').mockImplementation(() => {})
    const { t, hub } = await setup()
    t.failFrames = true
    t.calls.length = 0
    hub.subscribeCamera('cam-1')
    hub.watchFrames('cam-1')
    await vi.advanceTimersByTimeAsync(100)
    hub.watchFrames('cam-2')
    await vi.advanceTimersByTimeAsync(100)
    expect(t.calls).toEqual([['SubscribeCameras', ['cam-1']]])
    expect(dbg).toHaveBeenCalledTimes(1)
    dbg.mockRestore()
  })
})
