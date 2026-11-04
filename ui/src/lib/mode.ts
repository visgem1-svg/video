import type { CameraConfig, CameraHealth, ExtractMode } from './types'

/** Absent field (older backend) means full mode. */
export const modeOf = (x: { mode?: ExtractMode } | undefined): ExtractMode => x?.mode ?? 'full'

/** Live health mode wins over the (polled) camera config; both default to full. */
export function effectiveMode(
  health: Pick<CameraHealth, 'mode'> | undefined,
  cam: Pick<CameraConfig, 'extract'> | undefined,
): ExtractMode {
  return health?.mode ?? cam?.extract?.mode ?? 'full'
}

/** Contract 9.8 item 5 may also surface idle as the health state itself: fold it into mode. */
export function normalizeHealth(h: CameraHealth): CameraHealth {
  return (h.state as string) === 'idle' ? { ...h, state: 'online', mode: 'idle' } : h
}

/**
 * Frames arrive at full rate when the newest two are < 2.5 s apart and the newest is fresh
 * (idle is one frame / 5 s). `tsList` is newest first.
 */
export function framesAtFullRate(tsList: number[], now: number): boolean {
  if (tsList.length < 2) return false
  return now - tsList[0] < 5000 && tsList[0] - tsList[1] < 2500
}
