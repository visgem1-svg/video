import { useEffect } from 'react'
import { hub } from '@/lib/realtime/hub'

/** Tells the gateway someone looks at this camera's extracted frames (contract 9.8). */
export function useWatchFrames(cameraId: string | null, active = true) {
  useEffect(() => (cameraId && active ? hub.watchFrames(cameraId) : undefined), [cameraId, active])
}
