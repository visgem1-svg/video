/**
 * Minimal WHEP (WebRTC-HTTP Egress Protocol) client.
 *   POST {url}  Content-Type: application/sdp  body = offer  ->  201 + answer SDP + Location
 *   DELETE {Location}                                         ->  teardown
 * Dependencies (fetch, RTCPeerConnection) are injectable so it can be unit tested.
 */
export interface WhepDeps {
  fetchImpl?: typeof fetch
  createPeerConnection?: () => RTCPeerConnection
  /** Max ms to wait for ICE gathering before sending the offer anyway. */
  iceGatherTimeoutMs?: number
}

/**
 * The gateway strips the /media prefix before MediaMTX, so MediaMTX answers with an absolute
 * Location like "/cam-002/whep/<id>". Re-add the prefix of the offer URL (".../media/<cam>/whep").
 */
export function resolveLocation(location: string, offerUrl: string): string {
  const offer = new URL(offerUrl, typeof window === 'undefined' ? undefined : window.location.href)
  if (location.startsWith('/')) {
    const prefix = offer.pathname.replace(/\/[^/]+\/whep\/?$/, '')
    if (prefix && !location.startsWith(prefix + '/')) location = prefix + location
  }
  return new URL(location, offer).toString()
}

export class WhepError extends Error {
  constructor(
    message: string,
    public status?: number,
  ) {
    super(message)
    this.name = 'WhepError'
  }
}

export class WhepClient {
  private pc: RTCPeerConnection | null = null
  private resource: string | null = null
  private closed = false
  private readonly fetchImpl: typeof fetch
  private readonly makePc: () => RTCPeerConnection
  private readonly iceTimeout: number

  constructor(
    private readonly url: string,
    private readonly onStream: (stream: MediaStream) => void,
    private readonly onState?: (state: RTCPeerConnectionState) => void,
    deps: WhepDeps = {},
  ) {
    this.fetchImpl = deps.fetchImpl ?? ((...a) => fetch(...a))
    this.makePc = deps.createPeerConnection ?? (() => new RTCPeerConnection())
    this.iceTimeout = deps.iceGatherTimeoutMs ?? 2000
  }

  async start(signal?: AbortSignal): Promise<void> {
    const pc = this.makePc()
    this.pc = pc
    pc.addTransceiver('video', { direction: 'recvonly' })
    pc.ontrack = (ev) => {
      const stream = ev.streams[0] ?? new MediaStream([ev.track])
      this.onStream(stream)
    }
    pc.onconnectionstatechange = () => this.onState?.(pc.connectionState)

    const offer = await pc.createOffer()
    await pc.setLocalDescription(offer)
    await this.waitIce(pc)

    const res = await this.fetchImpl(this.url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/sdp' },
      body: pc.localDescription?.sdp ?? offer.sdp,
      signal,
    })
    if (this.closed) {
      pc.close()
      return
    }
    if (res.status !== 201 && res.status !== 200) {
      this.close()
      throw new WhepError(`WHEP offer rejected (${res.status})`, res.status)
    }
    const loc = res.headers.get('Location')
    if (loc) this.resource = resolveLocation(loc, this.url)
    const answer = await res.text()
    await pc.setRemoteDescription({ type: 'answer', sdp: answer })
  }

  private waitIce(pc: RTCPeerConnection): Promise<void> {
    if (pc.iceGatheringState === 'complete') return Promise.resolve()
    return new Promise((resolve) => {
      const done = () => {
        clearTimeout(timer)
        pc.removeEventListener('icegatheringstatechange', check)
        resolve()
      }
      const check = () => {
        if (pc.iceGatheringState === 'complete') done()
      }
      const timer = setTimeout(done, this.iceTimeout)
      pc.addEventListener('icegatheringstatechange', check)
    })
  }

  close(): void {
    if (this.closed) return
    this.closed = true
    const resource = this.resource
    if (resource)
      void this.fetchImpl(resource, { method: 'DELETE', keepalive: true }).catch(() => {})
    this.pc?.close()
    this.pc = null
  }
}
