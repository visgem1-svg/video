import { Link } from 'react-router-dom'
import { OctagonAlert, Pencil, TriangleAlert } from 'lucide-react'
import { Badge } from '@/components/ui/primitives'
import { errorBadgeOf, errorHintOf } from '@/lib/errorClass'
import type { CameraHealth } from '@/lib/types'
import { cn } from '@/lib/utils'

export const editSourceHref = (cameraId: string) => `/sources?edit=${encodeURIComponent(cameraId)}`

type ErrorFields = Pick<CameraHealth, 'error_class' | 'error_hint'>

/**
 * RTSP error class as a badge (icon + text, never colour alone). Terminal classes get the
 * danger tone and an edit-source link; the server hint is the tooltip. Renders nothing when the
 * backend sends no class.
 */
export function ErrorClassBadge({
  health,
  cameraId,
  className,
}: {
  health: ErrorFields | undefined
  cameraId: string
  className?: string
}) {
  const b = errorBadgeOf(health)
  if (!b || !health) return null
  const Icon = b.terminal ? OctagonAlert : TriangleAlert
  return (
    <span className={cn('inline-flex items-center gap-2', className)}>
      <Badge tone={b.tone} title={errorHintOf(health)} data-testid="error-class">
        <Icon className="size-3" aria-hidden />
        {b.label}
      </Badge>
      {b.terminal && (
        <Link
          to={editSourceHref(cameraId)}
          className="inline-flex items-center gap-1 text-xs text-primary hover:underline"
          onClick={(e) => e.stopPropagation()}
        >
          <Pencil className="size-3" aria-hidden /> Edit source
        </Link>
      )}
    </span>
  )
}
