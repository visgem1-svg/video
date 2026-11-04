import { useEffect, useState } from 'react'
import { ImageOff } from 'lucide-react'
import { cn } from '@/lib/utils'

export const RETRY_DELAYS_MS = [1000, 2000, 4000]

/**
 * Snapshot that may not exist yet (Results copies it shortly after the live event):
 * shows a skeleton, retries up to 3 times (1 s, 2 s, 4 s) with a cache-busting param,
 * then a "no snapshot" state.
 */
export const LIVE_FIRST_REQUEST_MS = 1500

export function SnapshotImage({
  src,
  alt,
  initialDelayMs = 0,
}: {
  src: string
  alt: string
  /** live events: Results copies the snapshot 0.3-1.2 s after the event, so wait before asking */
  initialDelayMs?: number
}) {
  const [attempt, setAttempt] = useState(0)
  const [loaded, setLoaded] = useState(false)
  const [armed, setArmed] = useState(initialDelayMs <= 0 || src.startsWith('data:'))

  useEffect(() => {
    if (armed) return
    const t = setTimeout(() => setArmed(true), initialDelayMs)
    return () => clearTimeout(t)
  }, [armed, initialDelayMs])
  const failed = attempt >= RETRY_DELAYS_MS.length + 1

  useEffect(() => {
    setAttempt(0)
    setLoaded(false)
  }, [src])

  const onError = () => {
    if (attempt >= RETRY_DELAYS_MS.length) {
      setAttempt(RETRY_DELAYS_MS.length + 1)
      return
    }
    const delay = RETRY_DELAYS_MS[attempt]
    setTimeout(() => setAttempt((a) => a + 1), delay)
  }

  if (failed) {
    return (
      <div className="grid size-full place-items-center text-muted-foreground">
        <div className="flex flex-col items-center gap-1 text-xs">
          <ImageOff className="size-6" />
          No snapshot
        </div>
      </div>
    )
  }
  const url =
    attempt === 0 || src.startsWith('data:')
      ? src
      : `${src}${src.includes('?') ? '&' : '?'}r=${attempt}`
  return (
    <>
      {!loaded && <div data-testid="snapshot-skeleton" className="absolute inset-0 skeleton" />}
      {armed && (
        <img
          key={attempt}
          src={url}
          alt={alt}
          decoding="async"
          className={cn('size-full object-cover transition-opacity', !loaded && 'opacity-0')}
          onLoad={() => setLoaded(true)}
          onError={onError}
        />
      )}
    </>
  )
}
