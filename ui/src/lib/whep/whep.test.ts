import { describe, expect, it, vi } from 'vitest'
import { SlotPool } from './slotPool'
import { WhepClient } from './whepClient'

function fakePc() {
  return {
    iceGatheringState: 'complete',
    connectionState: 'new',
    localDescription: { sdp: 'v=0 offer' },
    ontrack: null as ((e: unknown) => void) | null,
    onconnectionstatechange: null,
    addTransceiver: vi.fn(),
    createOffer: vi.fn().mockResolvedValue({ type: 'offer', sdp: 'v=0 offer' }),
    setLocalDescription: vi.fn().mockResolvedValue(undefined),
    setRemoteDescription: vi.fn().mockResolvedValue(undefined),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    close: vi.fn(),
  }
}

describe('WhepClient', () => {
  it('happy path: POSTs the offer as SDP, applies the answer, delivers the stream, DELETEs on close', async () => {
    const pc = fakePc()
    const fetchImpl = vi.fn(async (_url: RequestInfo | URL, init?: RequestInit) =>
      init?.method === 'POST'
        ? new Response('v=0 answer', {
            status: 201,
            headers: { Location: '/media/cam-001/whep/session-1' },
          })
        : new Response(null, { status: 200 }),
    )
    const onStream = vi.fn()
    const c = new WhepClient('http://gw/media/cam-001/whep', onStream, undefined, {
      fetchImpl: fetchImpl as unknown as typeof fetch,
      createPeerConnection: () => pc as unknown as RTCPeerConnection,
    })
    await c.start()

    const [url, init] = fetchImpl.mock.calls[0]
    expect(url).toBe('http://gw/media/cam-001/whep')
    expect(init?.method).toBe('POST')
    expect((init?.headers as Record<string, string>)['Content-Type']).toBe('application/sdp')
    expect(init?.body).toBe('v=0 offer')
    expect(pc.addTransceiver).toHaveBeenCalledWith('video', { direction: 'recvonly' })
    expect(pc.setRemoteDescription).toHaveBeenCalledWith({ type: 'answer', sdp: 'v=0 answer' })

    const stream = { id: 's' }
    pc.ontrack?.({ streams: [stream], track: {} })
    expect(onStream).toHaveBeenCalledWith(stream)

    c.close()
    expect(fetchImpl).toHaveBeenLastCalledWith(
      'http://gw/media/cam-001/whep/session-1',
      expect.objectContaining({ method: 'DELETE' }),
    )
    expect(pc.close).toHaveBeenCalled()
  })

  it('throws and closes the peer connection when the server rejects', async () => {
    const pc = fakePc()
    const c = new WhepClient('http://gw/x', vi.fn(), undefined, {
      fetchImpl: (async () => new Response('no', { status: 404 })) as unknown as typeof fetch,
      createPeerConnection: () => pc as unknown as RTCPeerConnection,
    })
    await expect(c.start()).rejects.toThrow(/404/)
    expect(pc.close).toHaveBeenCalled()
  })
})

describe('SlotPool', () => {
  it('caps concurrency and serves waiters FIFO on release', async () => {
    const p = new SlotPool(1)
    const r1 = await p.acquire()
    let got = false
    const w = p.acquire().then((r) => {
      got = true
      return r
    })
    await Promise.resolve()
    expect(got).toBe(false)
    r1()
    const r2 = await w
    expect(p.inUse).toBe(1)
    r2()
    expect(p.inUse).toBe(0)
  })
})
