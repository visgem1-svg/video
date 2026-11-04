/** HH:mm:ss.SSS in local time. */
export function formatTimeMs(ts: number): string {
  const d = new Date(ts)
  const p = (n: number, w = 2) => String(n).padStart(w, '0')
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${p(d.getMilliseconds(), 3)}`
}

/** "1.4 s", "2 m 05 s" style age. */
export function formatAgeMs(ms: number): string {
  if (!Number.isFinite(ms)) return '-'
  const v = Math.max(0, ms)
  if (v < 10_000) return `${(v / 1000).toFixed(1)} s`
  const s = Math.floor(v / 1000)
  if (s < 60) return `${s} s`
  return `${Math.floor(s / 60)} m ${String(s % 60).padStart(2, '0')} s`
}
