import { memo } from 'react'
import { CircleCheck, CircleDashed, TriangleAlert, WifiOff } from 'lucide-react'
import type { HealthState } from '@/lib/types'
import { Badge } from '@/components/ui/primitives'
import { cn } from '@/lib/utils'

/** Single source of status semantics (DESIGN.md section 2). Never colour alone: icon + text. */
export const STATE_COLOR: Record<HealthState | 'unknown', string> = {
  online: 'bg-ok',
  degraded: 'bg-warn',
  offline: 'bg-err',
  unassigned: 'bg-unas',
  unknown: 'bg-off',
}
const STATE_TEXT: Record<HealthState | 'unknown', string> = {
  online: 'text-ok',
  degraded: 'text-warn',
  offline: 'text-err',
  unassigned: 'text-unas',
  unknown: 'text-off',
}
const STATE_ICON = {
  online: CircleCheck,
  degraded: TriangleAlert,
  offline: WifiOff,
  unassigned: CircleDashed,
} as const
const TONE = {
  online: 'success',
  degraded: 'warning',
  offline: 'danger',
  unassigned: 'unassigned',
} as const

export const stateTextClass = (s: HealthState | undefined) => STATE_TEXT[s ?? 'unknown']

export const StatusDot = memo(function StatusDot({
  state,
  className,
}: {
  state: HealthState | undefined
  className?: string
}) {
  const s = state ?? 'unknown'
  return (
    <span
      role="img"
      aria-label={`status ${s}`}
      title={s}
      className={cn('inline-block size-2 shrink-0 rounded-full', STATE_COLOR[s], className)}
    />
  )
})

/**
 * Online is deliberately quiet (dot + muted text) so the few degraded/offline rows stand out;
 * every other state is a tinted badge with an icon.
 */
export function StateBadge({ state }: { state: HealthState | undefined }) {
  if (!state) return <Badge>unknown</Badge>
  if (state === 'online')
    return (
      <span className="inline-flex items-center gap-1.5 text-xs text-muted-foreground">
        <StatusDot state="online" /> online
      </span>
    )
  const Icon = STATE_ICON[state]
  return (
    <Badge tone={TONE[state]}>
      <Icon className="size-3" aria-hidden />
      {state}
    </Badge>
  )
}
