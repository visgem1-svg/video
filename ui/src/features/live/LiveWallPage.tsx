import { useCallback, useEffect, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Search, VideoOff } from 'lucide-react'
import { Dropdown } from '@/components/ui/dropdown'
import {
  EmptyState,
  ErrorState,
  Input,
  PageHeader,
  Pagination,
  Segmented,
  Skeleton,
} from '@/components/ui/primitives'
import { summarize } from '@/features/health/health'
import { useCameras } from '@/lib/api/queries'
import { useHealthMap } from '@/lib/realtime/stores'
import type { HealthState } from '@/lib/types'
import { LiveTile } from './LiveTile'

const LAYOUTS = [1, 4, 9, 16] as const
type Layout = (typeof LAYOUTS)[number]
const SIDE: Record<Layout, number> = { 1: 1, 4: 2, 9: 3, 16: 4 }
const LABEL: Record<Layout, string> = { 1: '1', 4: '2×2', 9: '3×3', 16: '4×4' }

const STATE_OPTIONS = [
  { value: '', label: 'All states' },
  { value: 'online', label: 'Online' },
  { value: 'degraded', label: 'Degraded' },
  { value: 'offline', label: 'Offline' },
  { value: 'unassigned', label: 'Unassigned' },
]

function loadLayout(): Layout {
  try {
    const v = Number(localStorage.getItem('vp-wall-layout'))
    if (LAYOUTS.includes(v as Layout)) return v as Layout
  } catch {
    /* ignore */
  }
  return 9
}

export default function LiveWallPage() {
  const nav = useNavigate()
  const { data: cameras, isLoading, error, refetch } = useCameras()
  const health = useHealthMap()
  const [layout, setLayout] = useState<Layout>(loadLayout)
  const [page, setPage] = useState(0)
  const [q, setQ] = useState('')
  const [state, setState] = useState<'' | HealthState>('')

  useEffect(() => {
    try {
      localStorage.setItem('vp-wall-layout', String(layout))
    } catch {
      /* ignore */
    }
  }, [layout])

  const filtered = useMemo(() => {
    const needle = q.trim().toLowerCase()
    return (cameras ?? []).filter(
      (c) =>
        (!needle ||
          c.name.toLowerCase().includes(needle) ||
          c.camera_id.includes(needle) ||
          c.tags.some((t) => t.includes(needle))) &&
        (!state || health.get(c.camera_id)?.state === state),
    )
  }, [cameras, q, state, health])

  const summary = useMemo(() => summarize(health.values()), [health])
  const pages = Math.max(1, Math.ceil(filtered.length / layout))
  const cur = Math.min(page, pages - 1)
  const visible = filtered.slice(cur * layout, cur * layout + layout)
  const open = useCallback((id: string) => nav(`/live/${id}`), [nav])
  const side = SIDE[layout]
  const rows = Math.ceil(visible.length / side) || 1

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <PageHeader
        title="Live wall"
        hint="Only the cameras on screen are streamed and subscribed to."
        summary={
          <>
            <span className="inline-flex items-center gap-1.5">
              <span className="size-2 rounded-full bg-ok" aria-hidden />
              <span className="num">{summary.online}</span> online
            </span>
            {summary.degraded > 0 && (
              <span className="inline-flex items-center gap-1.5 text-warn">
                <span className="size-2 rounded-full bg-warn" aria-hidden />
                <span className="num">{summary.degraded}</span> degraded
              </span>
            )}
            {summary.offline > 0 && (
              <span className="inline-flex items-center gap-1.5 text-err">
                <span className="size-2 rounded-full bg-err" aria-hidden />
                <span className="num">{summary.offline}</span> offline
              </span>
            )}
            <span className="num">{cameras?.length ?? summary.total} cameras</span>
          </>
        }
        actions={
          <>
            <div className="relative">
              <Search className="absolute top-2.5 left-2.5 size-4 text-faint" aria-hidden />
              <Input
                className="w-52 pl-8"
                placeholder="Search camera / tag"
                aria-label="Search cameras"
                value={q}
                onChange={(e) => {
                  setQ(e.target.value)
                  setPage(0)
                }}
              />
            </div>
            <Dropdown
              ariaLabel="State filter"
              value={state}
              onChange={(v) => {
                setState(v as '' | HealthState)
                setPage(0)
              }}
              options={STATE_OPTIONS}
              className="w-36"
            />
            <Segmented
              value={layout}
              onChange={(v) => {
                setLayout(v)
                setPage(0)
              }}
              options={LAYOUTS.map((l) => ({ value: l, label: LABEL[l] }))}
            />
            <Pagination
              page={cur}
              pages={pages}
              onChange={setPage}
              total={filtered.length}
              pageSize={layout}
            />
          </>
        }
      />
      {error && (
        <ErrorState
          className="mb-3"
          message={`Could not load cameras: ${String(error)}`}
          onRetry={() => void refetch()}
        />
      )}
      {isLoading ? (
        <div className="grid flex-1 grid-cols-3 gap-2">
          {Array.from({ length: 9 }, (_, i) => (
            <Skeleton key={i} className="aspect-video" />
          ))}
        </div>
      ) : visible.length === 0 ? (
        <EmptyState
          icon={<VideoOff />}
          title="No cameras match"
          hint="Adjust the search or the state filter."
        />
      ) : (
        <div
          className="grid min-h-[360px] flex-1 gap-2"
          style={{
            gridTemplateColumns: `repeat(${side}, minmax(0, 1fr))`,
            gridTemplateRows: `repeat(${rows}, minmax(0, 1fr))`,
          }}
        >
          {visible.map((c) => (
            <LiveTile key={c.camera_id} camera={c} onOpen={open} />
          ))}
        </div>
      )}
    </div>
  )
}
