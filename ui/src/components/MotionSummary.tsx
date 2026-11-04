import { Link } from 'react-router-dom'
import { Pencil } from 'lucide-react'
import { editSourceHref } from '@/components/ErrorClassBadge'
import { Badge } from '@/components/ui/primitives'
import { motionOf, skippedLabel, skippedPct } from '@/lib/motion'
import type { CameraConfig, CameraHealth } from '@/lib/types'
import { cn } from '@/lib/utils'

/** Skipped-frames figure with a thin bar. Higher is better (more decode work avoided). */
export function SkippedBar({ pct, className }: { pct: number; className?: string }) {
  return (
    <div className={cn('space-y-1', className)} data-testid="motion-skipped">
      <div className="num text-xs">{skippedLabel(pct)}</div>
      <div
        className="h-1 overflow-hidden rounded bg-muted"
        role="meter"
        aria-label="Frames skipped"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={pct}
      >
        <div className="h-full bg-primary" style={{ width: `${pct}%` }} />
      </div>
    </div>
  )
}

/** Read-only motion settings + live skipped %, with an edit link to Sources. */
export function MotionSummary({
  camera,
  health,
  className,
}: {
  camera: Pick<CameraConfig, 'camera_id' | 'motion'> | undefined
  health: Pick<CameraHealth, 'motion_skipped_pct'> | undefined
  className?: string
}) {
  if (!camera) return null
  const m = motionOf(camera)
  const pct = skippedPct(health)
  return (
    <div className={cn('space-y-2', className)}>
      <div className="flex flex-wrap items-center gap-2 text-xs">
        <Badge tone={m.enabled ? 'success' : 'neutral'}>{m.enabled ? 'On' : 'Off'}</Badge>
        {m.enabled && (
          <span className="text-muted-foreground">
            {m.sensitivity} sensitivity, heartbeat {m.heartbeat_s} s
          </span>
        )}
        <Link
          to={editSourceHref(camera.camera_id)}
          className="ml-auto inline-flex items-center gap-1 text-primary hover:underline"
        >
          <Pencil className="size-3" aria-hidden /> Edit
        </Link>
      </div>
      {m.enabled &&
        (pct !== null ? (
          <SkippedBar pct={pct} />
        ) : (
          <div className="text-xs text-muted-foreground">Skipped frames: measuring...</div>
        ))}
    </div>
  )
}
