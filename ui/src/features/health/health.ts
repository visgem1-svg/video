import { countByErrorClass } from '@/lib/errorClass'
import { modeOf } from '@/lib/mode'
import { skippedPct } from '@/lib/motion'
import type { CameraHealth, HealthState, HealthSummary } from '@/lib/types'

const RANK: Record<HealthState, number> = { offline: 0, degraded: 1, unassigned: 2, online: 3 }

/** Worst first: offline, degraded, unassigned, online; then by fps shortfall, then name. */
export function sortWorstFirst(list: CameraHealth[]): CameraHealth[] {
  const shortfall = (h: CameraHealth) => (h.fps_target > 0 ? h.fps_actual / h.fps_target : 1)
  return [...list].sort(
    (a, b) =>
      RANK[a.state] - RANK[b.state] ||
      shortfall(a) - shortfall(b) ||
      a.name.localeCompare(b.name, undefined, { numeric: true }),
  )
}

export function summarize(list: Iterable<CameraHealth>): HealthSummary {
  const s: HealthSummary = { total: 0, online: 0, degraded: 0, offline: 0, unassigned: 0, idle: 0 }
  const all = [...list]
  for (const h of all) {
    s.total++
    s[h.state]++
    if (modeOf(h) === 'idle') s.idle++
  }
  const by = countByErrorClass(all)
  if (Object.keys(by).length) s.by_error_class = by
  return s
}

/** The Motion skipped column only exists when some camera reports the figure. */
export const anyMotionSkipped = (list: Iterable<CameraHealth>): boolean => {
  for (const h of list) if (skippedPct(h) !== null) return true
  return false
}

export type Tone = 'ok' | 'warn' | 'err'
/** Uptime 1h: >=99 ok, 95-99 warn, <95 err. */
export const uptimeTone = (pct: number): Tone => (pct >= 99 ? 'ok' : pct >= 95 ? 'warn' : 'err')
/** Reconnects in the last hour: more than 5 is a warning. */
export const reconnectTone = (n: number): Tone => (n > 5 ? 'warn' : 'ok')
