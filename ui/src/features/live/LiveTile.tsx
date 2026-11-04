import { memo, useEffect, useRef } from 'react'
import { Link } from 'react-router-dom'
import { Maximize2, Workflow, WifiOff, CircleDashed } from 'lucide-react'
import { StatusDot } from '@/components/StatusDot'
import { hub } from '@/lib/realtime/hub'
import { useCameraHealth } from '@/lib/realtime/stores'
import { useNow } from '@/lib/realtime/useNow'
import type { CameraConfig, CameraHealth } from '@/lib/types'
import { cn, formatAge } from '@/lib/utils'
import { AgeChip } from './AgeChip'
import { DetectionOverlay } from './DetectionOverlay'
import { useInView } from './useInView'
import { VideoSurface } from './VideoSurface'

/** Offline / disabled tile body: never a bare black rectangle (DESIGN.md audit #1). */
function NoSignal({ health, disabled }: { health?: CameraHealth; disabled: boolean }) {
  const now = useNow()
  const Icon = disabled ? CircleDashed : WifiOff
  return (
    <div
      className="absolute inset-0 grid place-items-center text-center"
      style={{
        backgroundImage:
          'repeating-linear-gradient(135deg, rgba(255,255,255,.025) 0 8px, transparent 8px 16px)',
      }}
    >
      <div className="flex flex-col items-center gap-1 text-white/70">
        <Icon className="size-5 text-white/50" aria-hidden />
        <div className="text-[13px] font-medium">
          {disabled ? 'Disabled / unassigned' : 'No signal'}
        </div>
        {!disabled && health?.last_frame_ts && (
          <div className="num text-xs text-white/50">
            Last frame {formatAge(now - health.last_frame_ts)} ago
          </div>
        )}
      </div>
    </div>
  )
}

/**
 * One grid cell. When it is not in the viewport (or the tab is hidden) it closes its WebRTC
 * session, unsubscribes the camera on the hub and stops its canvas loop.
 */
export const LiveTile = memo(function LiveTile({
  camera,
  onOpen,
}: {
  camera: CameraConfig
  onOpen: (id: string) => void
}) {
  const ref = useRef<HTMLDivElement>(null)
  const visible = useInView(ref)
  const health = useCameraHealth(camera.camera_id)
  const disabled = health?.state === 'unassigned' || !camera.enabled
  const offline = health?.state === 'offline' || disabled
  const live = visible && !offline

  useEffect(() => {
    if (!live) return
    return hub.subscribeCamera(camera.camera_id)
  }, [live, camera.camera_id])

  return (
    <div
      ref={ref}
      role="button"
      tabIndex={0}
      aria-label={`Open ${camera.name}`}
      onClick={() => onOpen(camera.camera_id)}
      onKeyDown={(e) => e.key === 'Enter' && onOpen(camera.camera_id)}
      className={cn(
        'group relative min-h-0 cursor-pointer overflow-hidden rounded-[10px] border bg-[var(--video-bg)] transition-shadow duration-[120ms] hover:ring-1 hover:ring-border-strong',
        health?.state === 'degraded' && 'border-warn/60',
        health?.state === 'offline' && 'border-err/60',
      )}
    >
      {offline ? (
        <NoSignal health={health} disabled={disabled} />
      ) : live ? (
        <>
          <VideoSurface cameraId={camera.camera_id} label={camera.name} active={live} />
          <DetectionOverlay cameraId={camera.camera_id} active={live} />
        </>
      ) : null}

      <div className="pointer-events-none absolute inset-x-0 top-0 flex h-10 items-start gap-2 bg-gradient-to-b from-black/65 to-transparent px-2.5 pt-2 text-white">
        <StatusDot state={health?.state} className="mt-[5px]" />
        <span className="truncate text-[13px] font-semibold">{camera.name}</span>
        <span className="ml-auto font-mono text-[11px] text-white/70">{camera.camera_id}</span>
      </div>

      <div
        className={cn(
          'absolute inset-x-0 bottom-0 flex items-end justify-between gap-2 bg-gradient-to-t from-black/60 to-transparent px-2.5 pt-6 pb-2 transition-opacity duration-[120ms]',
          health?.state === 'degraded' || offline
            ? 'opacity-100'
            : 'opacity-0 group-focus-within:opacity-100 group-hover:opacity-100',
        )}
      >
        <div className="flex items-center gap-1.5 text-[11px] text-white/80">
          {health?.state === 'degraded' && <span className="text-warn">degraded</span>}
          <AgeChip cameraId={camera.camera_id} active={live} />
        </div>
        <div className="flex gap-1">
          <Link
            to={`/live/${camera.camera_id}`}
            onClick={(e) => e.stopPropagation()}
            title="Open focus view"
            aria-label={`Focus ${camera.name}`}
            className="rounded bg-black/50 p-1 text-white/85 hover:bg-black/70"
          >
            <Maximize2 className="size-3.5" />
          </Link>
          <Link
            to={`/live/${camera.camera_id}#pipeline`}
            onClick={(e) => e.stopPropagation()}
            title="Watch pipeline"
            aria-label={`Pipeline of ${camera.name}`}
            className="rounded bg-black/50 p-1 text-white/85 hover:bg-black/70"
          >
            <Workflow className="size-3.5" />
          </Link>
        </div>
      </div>
    </div>
  )
})
