import { useEffect, useState, type RefObject } from 'react'
import { MAX_WEBRTC_SESSIONS, whepUrl } from '../config'
import { WhepClient } from './whepClient'
import { SlotPool } from './slotPool'

export type WhepStatus = 'idle' | 'connecting' | 'playing' | 'failed'

const pool = new SlotPool(MAX_WEBRTC_SESSIONS)

/**
 * Plays `cameraId` into `videoRef` while `active`. Tears the peer connection down
 * the moment `active` turns false (tile scrolled/paged away) and retries with backoff.
 */
export function useWhep(
  cameraId: string,
  active: boolean,
  videoRef: RefObject<HTMLVideoElement | null>,
): WhepStatus {
  const [status, setStatus] = useState<WhepStatus>('idle')

  useEffect(() => {
    if (!active) {
      setStatus('idle')
      return
    }
    const ctrl = new AbortController()
    let client: WhepClient | null = null
    let release: (() => void) | null = null
    let retryTimer: ReturnType<typeof setTimeout> | null = null
    let attempt = 0

    const teardown = () => {
      client?.close()
      client = null
      release?.()
      release = null
      const v = videoRef.current
      if (v) v.srcObject = null
    }

    const connect = async () => {
      setStatus('connecting')
      try {
        release = await pool.acquire(ctrl.signal)
        client = new WhepClient(
          whepUrl(cameraId),
          (stream) => {
            const v = videoRef.current
            if (v && v.srcObject !== stream) {
              v.srcObject = stream
              void v.play().catch(() => {})
            }
            attempt = 0
            setStatus('playing')
          },
          (state) => {
            if (state === 'failed' || state === 'disconnected' || state === 'closed') fail()
          },
        )
        await client.start(ctrl.signal)
      } catch (e) {
        if ((e as Error).name !== 'AbortError') fail()
      }
    }

    const fail = () => {
      if (ctrl.signal.aborted) return
      teardown()
      setStatus('failed')
      const delay = Math.min(30_000, 1000 * 2 ** attempt++) + Math.random() * 500
      retryTimer = setTimeout(() => void connect(), delay)
    }

    void connect()
    return () => {
      ctrl.abort()
      if (retryTimer) clearTimeout(retryTimer)
      teardown()
    }
  }, [cameraId, active, videoRef])

  return active ? status : 'idle'
}
