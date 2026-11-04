import { memo, useMemo, useState } from 'react'

import { Copy, Search, Activity } from 'lucide-react'

import { ErrorClassBadge } from '@/components/ErrorClassBadge'

import { ModeBadge } from '@/components/ModeBadge'

import { StateBadge, StatusDot } from '@/components/StatusDot'

import {
  Badge,
  Card,
  EmptyState,
  ErrorState,
  Input,
  PageHeader,
  Pagination,
} from '@/components/ui/primitives'

import { useCameras } from '@/lib/api/queries'

import { errorHintOf, errorLabel } from '@/lib/errorClass'

import { modeOf } from '@/lib/mode'

import { skippedPct } from '@/lib/motion'

import { ageTone } from '@/lib/overlay'

import { useHealthMap } from '@/lib/realtime/stores'

import { useNow } from '@/lib/realtime/useNow'

import type { CameraHealth, HealthState } from '@/lib/types'

import { cn, formatAge } from '@/lib/utils'

import { anyMotionSkipped, reconnectTone, sortWorstFirst, summarize, uptimeTone } from './health'

const PAGE = 25

type Filter = 'all' | HealthState | 'idle'

const TILES: Array<{ key: Filter; label: string; dot?: HealthState }> = [
  { key: 'all', label: 'Total' },
  { key: 'online', label: 'Online', dot: 'online' },
  { key: 'degraded', label: 'Degraded', dot: 'degraded' },
  { key: 'offline', label: 'Offline', dot: 'offline' },
  { key: 'unassigned', label: 'Unassigned', dot: 'unassigned' },
  { key: 'idle', label: 'Idle' },
]

const VALUE_TONE: Partial<Record<HealthState, string>> = {
  degraded: 'text-warn',
  offline: 'text-err',
}

const TEXT = { ok: 'text-foreground', warn: 'text-warn', err: 'text-err' } as const
const BAR = { ok: 'bg-ok', warn: 'bg-warn', err: 'bg-err' } as const
const RAIL: Partial<Record<HealthState, string>> = {
  degraded: 'shadow-[inset_2px_0_0_var(--warn)]',
  offline: 'shadow-[inset_2px_0_0_var(--err)]',
  unassigned: 'shadow-[inset_2px_0_0_var(--unas)]',
}

/** Ticks every second via the shared clock; the only cell that re-renders per second. */
const AgeCell = memo(function AgeCell({ ts, state }: { ts: number | null; state: HealthState }) {
  const now = useNow()
  if (!ts) return <span className="text-faint">–</span>
  const age = now - ts
  const tone = state === 'online' || state === 'degraded' ? ageTone(age) : 'neutral'
  return (
    <span className={cn('num', tone === 'warn' && 'text-warn', tone === 'err' && 'text-err')}>
      {formatAge(age)}
    </span>
  )
})

const Row = memo(function Row({
  h,
  dup,
  motion,
}: {
  h: CameraHealth
  dup?: string[]
  motion: boolean
}) {
  const ratio = h.fps_target > 0 ? h.fps_actual / h.fps_target : 1
  const unassigned = h.state === 'unassigned'
  const up = uptimeTone(h.uptime_pct_1h)
  const skipped = skippedPct(h)
  return (
    <tr className={cn('h-11 border-b last:border-0 hover:bg-muted/60', RAIL[h.state])}>
      <td className="px-4 py-1.5">
        <div className="font-medium">{h.name}</div>
        <div className="font-mono text-xs text-muted-foreground">{h.camera_id}</div>
        {dup && dup.length > 0 && (
          <Badge tone="warning" className="mt-1" title="Another camera pulls the same stream">
            <Copy className="size-3" aria-hidden />
            duplicate of {dup.join(', ')}
          </Badge>
        )}
      </td>
      <td className="px-3">
        <div className="flex items-center gap-1.5">
          <StateBadge state={h.state} />
          <ModeBadge mode={modeOf(h)} />
        </div>
      </td>
      <td className="num px-3 text-right">
        <span className={ratio < 0.7 && !unassigned ? 'text-warn' : ''}>
          {h.fps_actual.toFixed(1)}
        </span>{' '}
        <span className="text-faint">/ {h.fps_target}</span>
      </td>
      <td className="px-3 text-right">
        <AgeCell ts={h.last_frame_ts} state={h.state} />
      </td>
      <td className={cn('num px-3 text-right', TEXT[reconnectTone(h.reconnects_1h)])}>
        {h.reconnects_1h}
      </td>
      <td className="px-3">
        {unassigned ? (
          <span className="block text-right text-faint">–</span>
        ) : (
          <div className="flex items-center justify-end gap-2">
            <span className={cn('num', TEXT[up])}>{h.uptime_pct_1h}%</span>
            <span className="h-1 w-12 overflow-hidden rounded bg-muted" aria-hidden>
              <span
                className={cn('block h-full', BAR[up])}
                style={{ width: `${Math.max(0, Math.min(100, h.uptime_pct_1h))}%` }}
              />
            </span>
          </div>
        )}
      </td>
      {motion && (
        <td className="px-3 text-right">
          {skipped === null ? (
            <span className="text-faint" title="Motion detection is off for this camera">
              –
            </span>
          ) : (
            <div className="flex items-center justify-end gap-2">
              <span className="num">{skipped}%</span>
              <span className="h-1 w-10 overflow-hidden rounded bg-muted" aria-hidden>
                <span className="block h-full bg-primary" style={{ width: `${skipped}%` }} />
              </span>
            </div>
          )}
        </td>
      )}
      <td className="px-3">
        {h.worker_id ? (
          <Badge className="font-mono">{h.worker_id}</Badge>
        ) : (
          <span className="text-faint">–</span>
        )}
      </td>
      <td className="max-w-0 overflow-hidden px-3 text-xs" title={h.last_error ?? ''}>
        {h.error_class && (
          <div className="flex min-w-0 flex-col gap-0.5 py-1">
            <ErrorClassBadge
              health={h}
              cameraId={h.camera_id}
              className="flex-wrap gap-x-2 gap-y-0.5"
            />
            <span className="truncate text-muted-foreground">{errorHintOf(h)}</span>
          </div>
        )}
        {!h.error_class && (
          <div className="truncate font-mono text-muted-foreground">{h.last_error ?? ''}</div>
        )}
      </td>
    </tr>
  )
})

export default function HealthPage() {
  const map = useHealthMap()
  const { data: cameras, error, refetch } = useCameras()
  const dupOf = useMemo(
    () => new Map((cameras ?? []).map((c) => [c.camera_id, c.duplicate_of])),
    [cameras],
  )
  const [filter, setFilter] = useState<Filter>('all')
  const [q, setQ] = useState('')
  const [page, setPage] = useState(0)
  const all = useMemo(() => [...map.values()], [map])
  const summary = useMemo(() => summarize(all), [all])
  const sorted = useMemo(() => sortWorstFirst(all), [all])
  const rows = useMemo(() => {
    const n = q.trim().toLowerCase()
    return sorted.filter(
      (h) =>
        (filter === 'all' || (filter === 'idle' ? modeOf(h) === 'idle' : h.state === filter)) &&
        (!n || h.name.toLowerCase().includes(n) || h.camera_id.includes(n)),
    )
  }, [sorted, filter, q])
  const motion = useMemo(() => anyMotionSkipped(all), [all])
  const errorClasses = Object.entries(summary.by_error_class ?? {}).sort((a, b) => b[1] - a[1])
  const pages = Math.ceil(rows.length / PAGE)
  const cur = Math.min(page, Math.max(0, pages - 1))
  return (
    <div className="space-y-4">
      <PageHeader
        title="Health"
        description="Stream status from the extractors, worst first."
        actions={
          <div className="relative">
            <Search className="absolute top-2.5 left-2.5 size-4 text-faint" aria-hidden />
            <Input
              className="w-52 pl-8"
              placeholder="Search camera"
              value={q}
              onChange={(e) => {
                setQ(e.target.value)
                setPage(0)
              }}
              aria-label="Search cameras"
            />
          </div>
        }
      />
      {error && (
        <ErrorState
          message={`Could not load cameras: ${String(error)}`}
          onRetry={() => void refetch()}
        />
      )}
      <div className="grid grid-cols-2 gap-3 md:grid-cols-6">
        {TILES.map((t) => {
          const value = t.key === 'all' ? summary.total : summary[t.key]
          return (
            <button
              key={t.key}
              onClick={() => {
                setFilter(t.key)
                setPage(0)
              }}
              aria-pressed={filter === t.key}
              className={cn(
                'h-[88px] rounded-xl border bg-card px-4 py-3 text-left transition-colors duration-[120ms] hover:bg-muted/60',
                filter === t.key && 'border-primary ring-1 ring-primary',
              )}
            >
              <div className="flex items-center gap-2 text-xs text-muted-foreground">
                {t.dot && <StatusDot state={t.dot} />}
                {t.label}
              </div>
              <div
                className={cn(
                  'num mt-1 text-[28px] leading-8 font-semibold tracking-[-0.02em]',
                  value === 0 && t.key !== 'all'
                    ? 'text-faint'
                    : ((t.dot && VALUE_TONE[t.dot]) ?? ''),
                )}
                data-testid={`tile-${t.key}`}
              >
                {value}
              </div>
            </button>
          )
        })}
      </div>
      {errorClasses.length > 0 && (
        <div
          className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground"
          data-testid="by-error-class"
        >
          <span>Stream errors</span>
          {errorClasses.map(([cls, n]) => (
            <Badge key={cls} tone="warning" title={cls}>
              {errorLabel(cls)} <span className="num font-semibold">{n}</span>
            </Badge>
          ))}
        </div>
      )}
      <Card className="overflow-hidden">
        <div className="overflow-auto">
          <table
            className={cn(
              'w-full table-fixed text-[13px]',
              motion ? 'min-w-[1080px]' : 'min-w-[960px]',
            )}
          >
            <thead className="sticky top-0 z-10 bg-card text-left text-xs text-muted-foreground">
              <tr className="border-b">
                <th className={cn('px-4 py-2 font-medium', motion ? 'w-[15%]' : 'w-[17%]')}>
                  Camera
                </th>
                <th className={cn('px-3 py-2 font-medium', motion ? 'w-[11%]' : 'w-[13%]')}>
                  State
                </th>
                <th
                  className={cn('px-3 py-2 text-right font-medium', motion ? 'w-[10%]' : 'w-[11%]')}
                >
                  FPS (act / target)
                </th>
                <th
                  className={cn('px-3 py-2 text-right font-medium', motion ? 'w-[8%]' : 'w-[9%]')}
                >
                  Last frame
                </th>
                <th
                  className={cn('px-3 py-2 text-right font-medium', motion ? 'w-[8%]' : 'w-[9%]')}
                >
                  Reconnects 1h
                </th>
                <th
                  className={cn('px-3 py-2 text-right font-medium', motion ? 'w-[11%]' : 'w-[12%]')}
                >
                  Uptime 1h
                </th>
                {motion && (
                  <th
                    className="w-[10%] px-3 py-2 text-right font-medium"
                    title="Share of frames dropped because nothing moved (last 60 s)"
                  >
                    Motion skipped
                  </th>
                )}
                <th className="w-[8%] px-3 py-2 font-medium">Worker</th>
                <th className="px-3 py-2 font-medium">Last error</th>
              </tr>
            </thead>
            <tbody>
              {rows.slice(cur * PAGE, cur * PAGE + PAGE).map((h) => (
                <Row key={h.camera_id} h={h} dup={dupOf.get(h.camera_id)} motion={motion} />
              ))}
            </tbody>
          </table>
        </div>
        {rows.length === 0 && (
          <EmptyState
            icon={<Activity />}
            title={map.size === 0 ? 'Waiting for health data' : 'No cameras in this state'}
            hint={
              map.size === 0
                ? 'Extractors report every 5 s.'
                : 'Pick another state tile to see more.'
            }
          />
        )}
        <div className="flex justify-end border-t px-4 py-2">
          <Pagination
            page={cur}
            pages={pages}
            onChange={setPage}
            total={rows.length}
            pageSize={PAGE}
          />
        </div>
      </Card>
    </div>
  )
}
