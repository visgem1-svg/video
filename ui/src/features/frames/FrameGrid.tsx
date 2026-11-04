import { memo, useMemo, useRef } from 'react'
import { ImageOff, Maximize2 } from 'lucide-react'
import { frameImageUrl, useLatestFrame, type FrameStat } from '@/lib/api/frames'
import { ModeBadge } from '@/components/ModeBadge'
import { useInView } from '@/features/live/useInView'
import { effectiveMode } from '@/lib/mode'
import { useCameraHealth } from '@/lib/realtime/stores'
import { useNow } from '@/lib/realtime/useNow'
import { EmptyState } from '@/components/ui/primitives'
import type { CameraConfig } from '@/lib/types'
import { cn } from '@/lib/utils'
import { useWatchFrames } from './useWatchFrames'
import { formatAgeMs, formatTimeMs } from './format'

const FrameTile = memo(function FrameTile({
  camera,
  stat,
  onOpen,
}: {
  camera: CameraConfig
  stat: FrameStat | undefined
  onOpen: (id: string) => void
}) {
  const ref = useRef<HTMLButtonElement>(null)
  const inView = useInView(ref, 0.05)
  useWatchFrames(camera.camera_id, inView)
  const mode = effectiveMode(useCameraHealth(camera.camera_id), camera)
  const { data: frame } = useLatestFrame(camera.camera_id, inView)
  const now = useNow()
  const age = frame ? now - frame.ts : null
  const stale = age != null && age > 15_000
  return (
    <button
      ref={ref}
      type="button"
      onClick={() => onOpen(camera.camera_id)}
      aria-label={`Open ${camera.name} frames`}
      className="group relative aspect-video overflow-hidden rounded-[10px] border bg-[var(--video-bg)] text-left"
    >
      {frame ? (
        <img
          src={frameImageUrl(frame)}
          alt=""
          decoding="async"
          className={cn('absolute inset-0 size-full object-cover', stale && 'opacity-50 grayscale')}
        />
      ) : (
        <div className="absolute inset-0 grid place-items-center text-faint">
          <span className="flex flex-col items-center gap-1 text-xs">
            <ImageOff className="size-5" aria-hidden />
            {inView ? 'No frames yet' : 'Paused off-screen'}
          </span>
        </div>
      )}
      <div className="absolute inset-x-0 top-0 flex items-center justify-between gap-2 bg-gradient-to-b from-black/65 to-transparent p-2">
        <span className="truncate text-[13px] font-semibold text-white">{camera.name}</span>
        <span className="flex items-center gap-1.5">
          <ModeBadge mode={mode} />
          <span className="font-mono text-[11px] text-white/70">{camera.camera_id}</span>
        </span>
      </div>
      <div className="num absolute inset-x-0 bottom-0 flex items-center justify-between gap-2 bg-gradient-to-t from-black/70 to-transparent p-2 text-[11px] text-white/85">
        <span className="font-mono">{frame ? formatTimeMs(frame.ts) : '--:--:--.---'}</span>
        <span className="flex items-center gap-2">
          {age != null && <span className={cn(stale && 'text-warn')}>{formatAgeMs(age)} ago</span>}
          <span>{stat ? `${stat.fps.toFixed(1)} fps` : '- fps'}</span>
        </span>
      </div>
      <Maximize2
        className="absolute right-2 bottom-8 size-4 text-white/0 transition-colors group-hover:text-white/80"
        aria-hidden
      />
    </button>
  )
})

/** One tile per source: latest extracted frame, extraction time and fps. Off-screen tiles pause. */
export function FrameGrid({
  cameras,
  stats,
  onOpen,
}: {
  cameras: CameraConfig[]
  stats: FrameStat[] | undefined
  onOpen: (id: string) => void
}) {
  const byCam = useMemo(() => new Map((stats ?? []).map((s) => [s.camera_id, s])), [stats])
  if (cameras.length === 0)
    return <EmptyState title="No cameras match" hint="Clear the search to see all sources." />
  return (
    <div className="grid grid-cols-[repeat(auto-fill,minmax(260px,1fr))] gap-2">
      {cameras.map((c) => (
        <FrameTile key={c.camera_id} camera={c} stat={byCam.get(c.camera_id)} onOpen={onOpen} />
      ))}
    </div>
  )
}
