import { memo, useEffect, useRef } from 'react'
import { hub } from '@/lib/realtime/hub'
import { useNow } from '@/lib/realtime/useNow'
import { ageChipText, ageTone } from '@/lib/overlay'
import { cn } from '@/lib/utils'

const TONE = { neutral: 'text-white/75', warn: 'text-warn', err: 'text-err' } as const

/** "updated Xs ago" for a camera. Arrival time lives in a ref; only the 1 Hz clock re-renders. */
export const AgeChip = memo(function AgeChip({
  cameraId,
  active,
  className,
}: {
  cameraId: string
  active: boolean
  className?: string
}) {
  const last = useRef<number | null>(null)
  const now = useNow()
  useEffect(() => {
    last.current = null
    if (!active) return
    return hub.onDetections(cameraId, (d) => {
      if (!d.partial) last.current = Date.now()
    })
  }, [cameraId, active])
  const age = last.current == null ? null : now - last.current
  const text = ageChipText(age)
  if (!text || age == null) return null
  return (
    <span
      className={cn(
        'num rounded bg-black/55 px-1.5 py-0.5 text-[11px] leading-4 backdrop-blur-sm',
        TONE[ageTone(age)],
        className,
      )}
    >
      {text}
    </span>
  )
})
