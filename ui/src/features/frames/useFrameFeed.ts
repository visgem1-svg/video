import { useCallback, useEffect, useRef, useState } from 'react'
import { framesApi, type FrameRecord } from '@/lib/api/frames'
import { mergeFrames, olderCursor, PAGE_SIZE } from './timeline'

export const TAIL_MS = 1_000

/**
 * Newest-first frame list of one camera. Live tail polls the newest page every second and merges;
 * `loadOlder` pages back with `before=<oldest ts>`.
 */
export function useFrameFeed(cameraId: string | null, live: boolean) {
  const [frames, setFrames] = useState<FrameRecord[]>([])
  const [hasMore, setHasMore] = useState(true)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [initial, setInitial] = useState(true)
  const framesRef = useRef(frames)
  framesRef.current = frames
  const loadingOlder = useRef(false)

  const fetchNewest = useCallback(
    async (limit: number, signal: { cancelled: boolean }) => {
      if (!cameraId) return
      try {
        const page = await framesApi.list({ camera_id: cameraId, limit })
        if (signal.cancelled) return
        setFrames((cur) => mergeFrames(cur, page))
        setError(null)
      } catch (e) {
        if (!signal.cancelled) setError(e instanceof Error ? e.message : 'Request failed')
      } finally {
        if (!signal.cancelled) setInitial(false)
      }
    },
    [cameraId],
  )

  // camera change: reset + first page
  useEffect(() => {
    const sig = { cancelled: false }
    setFrames([])
    setHasMore(true)
    setInitial(true)
    setError(null)
    void fetchNewest(PAGE_SIZE, sig)
    return () => {
      sig.cancelled = true
    }
  }, [fetchNewest])

  // live tail
  useEffect(() => {
    if (!live || !cameraId) return
    const sig = { cancelled: false }
    const t = setInterval(() => {
      if (document.visibilityState !== 'hidden') void fetchNewest(30, sig)
    }, TAIL_MS)
    return () => {
      sig.cancelled = true
      clearInterval(t)
    }
  }, [live, cameraId, fetchNewest])

  const loadOlder = useCallback(async () => {
    const cursor = olderCursor(framesRef.current)
    if (!cameraId || cursor == null || loadingOlder.current) return
    loadingOlder.current = true
    setLoading(true)
    try {
      const page = await framesApi.list({ camera_id: cameraId, before: cursor, limit: PAGE_SIZE })
      setFrames((cur) => mergeFrames(cur, page))
      if (page.length < PAGE_SIZE) setHasMore(false)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Request failed')
    } finally {
      loadingOlder.current = false
      setLoading(false)
    }
  }, [cameraId])

  const refresh = useCallback(() => {
    void fetchNewest(PAGE_SIZE, { cancelled: false })
  }, [fetchNewest])

  return { frames, hasMore, loading, error, initial, loadOlder, refresh }
}
