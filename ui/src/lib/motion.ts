import type { CameraConfig, CameraHealth, MotionConfig, MotionSensitivity } from './types'

/** Contract 9.9: missing = disabled / medium / 5 s. */
export const DEFAULT_MOTION: MotionConfig = {
  enabled: false,
  sensitivity: 'medium',
  heartbeat_s: 5,
}

export const SENSITIVITIES: Array<{ value: MotionSensitivity; label: string; hint: string }> = [
  { value: 'low', label: 'Low', hint: 'Ignores small changes. Good for noisy night cameras.' },
  { value: 'medium', label: 'Medium', hint: 'Balanced default for most scenes.' },
  { value: 'high', label: 'High', hint: 'Catches small objects. Best for quiet scenes.' },
]

const isSensitivity = (v: unknown): v is MotionSensitivity =>
  v === 'low' || v === 'medium' || v === 'high'

/** Tolerant read of camera.motion: any missing or malformed field falls back to the default. */
export function motionOf(cam: Pick<CameraConfig, 'motion'> | undefined): MotionConfig {
  const m = cam?.motion
  const hb = Number(m?.heartbeat_s)
  return {
    enabled: m?.enabled === true,
    sensitivity: isSensitivity(m?.sensitivity) ? m.sensitivity : DEFAULT_MOTION.sensitivity,
    heartbeat_s: Number.isFinite(hb) && hb >= 1 && hb <= 60 ? hb : DEFAULT_MOTION.heartbeat_s,
  }
}

/** Rounded 0-100, or null when the backend did not report a figure. */
export function skippedPct(h: Pick<CameraHealth, 'motion_skipped_pct'> | undefined): number | null {
  const v = h?.motion_skipped_pct
  return typeof v === 'number' && Number.isFinite(v)
    ? Math.round(Math.min(100, Math.max(0, v)))
    : null
}

export const skippedLabel = (pct: number) => `Skipped ${pct} % (unchanged frames)`
