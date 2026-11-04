import type { CameraHealth } from './types'

/** Terminal-until-changed classes (9.9 item 5): retrying does not help, the source must be edited. */
const TERMINAL = new Set(['auth_failed', 'not_found', 'codec_unsupported'])

/** Used when the server sends error_class without error_hint. */
const FALLBACK: Record<string, string> = {
  auth_failed: 'Wrong credentials',
  not_found: 'Stream path not found',
  codec_unsupported: 'Codec not supported',
  unreachable: 'Camera unreachable',
  stalled: 'No frames',
  decode_error: 'Decode errors',
  eof: 'Stream ended',
}

export interface ErrorBadge {
  cls: string
  label: string
  terminal: boolean
  tone: 'danger' | 'warning'
}

/** Short label for a class; unknown future classes are humanised ("foo_bar" -> "foo bar"). */
export const errorLabel = (cls: string) => FALLBACK[cls] ?? cls.replace(/_/g, ' ')

/** null when the camera has no error class (older backend or healthy). */
export function errorBadgeOf(
  h: Pick<CameraHealth, 'error_class' | 'error_hint'> | undefined,
): ErrorBadge | null {
  const cls = h?.error_class
  if (!cls) return null
  const terminal = TERMINAL.has(cls)
  return {
    cls,
    label: errorLabel(cls),
    terminal,
    tone: terminal ? 'danger' : 'warning',
  }
}

/** Server hint wins; otherwise the fallback label. */
export const errorHintOf = (h: Pick<CameraHealth, 'error_class' | 'error_hint'>): string =>
  h.error_hint?.trim() || errorLabel(h.error_class ?? '')

/** Cameras per class, omitted entirely when there is none. */
export function countByErrorClass(list: Iterable<Pick<CameraHealth, 'error_class'>>) {
  const out: Record<string, number> = {}
  for (const h of list) if (h.error_class) out[h.error_class] = (out[h.error_class] ?? 0) + 1
  return out
}
