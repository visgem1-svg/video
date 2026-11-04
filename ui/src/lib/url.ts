const DEFAULT_PORTS: Record<string, string> = {
  rtsp: '554',
  rtsps: '322',
  rtmp: '1935',
  http: '80',
  https: '443',
}

/**
 * Mirrors the registry's duplicate detection (contract 9.4): lowercase scheme/host, default
 * port removed, credentials and trailing "/" ignored. "publisher" (and junk) -> null.
 */
export function normalizeStreamUrl(raw: string): string | null {
  const s = raw.trim()
  const m = /^([a-z][a-z0-9+.-]*):\/\/(?:[^@/?#]*@)?([^/:?#]+)(?::(\d+))?([^?#]*)(\?[^#]*)?/i.exec(
    s,
  )
  if (!m) return null
  const scheme = m[1].toLowerCase()
  const host = m[2].toLowerCase()
  const port = m[3] && m[3] !== DEFAULT_PORTS[scheme] ? `:${m[3]}` : ''
  const path = m[4].replace(/\/+$/, '')
  return `${scheme}://${host}${port}${path}${m[5] ?? ''}`
}

/** Camera ids (other than `selfId`) that pull the same normalized stream URL. */
export function findDuplicates(
  streamUrl: string,
  cameras: Array<{ camera_id: string; stream_url: string }>,
  selfId?: string,
): string[] {
  const n = normalizeStreamUrl(streamUrl)
  if (!n) return []
  return cameras
    .filter((c) => c.camera_id !== selfId && normalizeStreamUrl(c.stream_url) === n)
    .map((c) => c.camera_id)
}
