const SECRET_PARAMS = /^(pass(word)?|pwd|token|key|secret|auth)$/i

/** Source label for the flow: credentials masked, "publisher" shown as "sim publisher". */
export function maskStreamUrl(url: string | null | undefined): string {
  if (!url || url === 'publisher') return 'sim publisher'
  try {
    const u = new URL(url)
    if (u.username || u.password) {
      u.username = '***'
      u.password = ''
    }
    for (const k of [...u.searchParams.keys()]) if (SECRET_PARAMS.test(k)) u.searchParams.set(k, '***')
    return decodeURIComponent(u.toString()).replace('***@', '***@')
  } catch {
    return url.replace(/\/\/[^/@\s]+@/, '//***@')
  }
}

export interface SourceInfo {
  kind: 'publisher' | 'stream'
  /** "publisher" or the host[:port] of the RTSP/HTTP source (no credentials) */
  label: string
}

/** Compact source description for camera chips: publisher vs. stream host. */
export function sourceInfo(streamUrl: string | null | undefined): SourceInfo {
  if (!streamUrl || streamUrl === 'publisher') return { kind: 'publisher', label: 'publisher' }
  try {
    return { kind: 'stream', label: new URL(streamUrl).host || streamUrl }
  } catch {
    return { kind: 'stream', label: streamUrl.replace(/^[a-z]+:\/\/([^/@\s]+@)?/i, '').split('/')[0] }
  }
}
