import { useCallback, useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { Film, Moon, Search } from 'lucide-react'
import { Switch } from '@/components/ui/dialog'
import {
  Button,
  EmptyState,
  ErrorState,
  Input,
  PageHeader,
  Segmented,
  Skeleton,
} from '@/components/ui/primitives'
import { useFrameDetail, useFrameStats } from '@/lib/api/frames'
import { useCameras } from '@/lib/api/queries'
import { ModeBadge } from '@/components/ModeBadge'
import { effectiveMode, framesAtFullRate } from '@/lib/mode'
import { useCameraHealth } from '@/lib/realtime/stores'
import { useNow } from '@/lib/realtime/useNow'
import { cn } from '@/lib/utils'
import { CameraCombobox } from './CameraCombobox'
import { Filmstrip } from './Filmstrip'
import { stepIndex } from './timeline'
import { FrameGrid } from './FrameGrid'
import { FrameMeta, FrameViewer } from './FrameViewer'
import { useFrameFeed } from './useFrameFeed'
import { useWatchFrames } from './useWatchFrames'

type Mode = 'single' | 'grid'

export default function FramesPage() {
  const [params, setParams] = useSearchParams()
  const mode: Mode = params.get('mode') === 'grid' ? 'grid' : 'single'
  const camParam = params.get('camera') ?? params.get('cam')
  const [live, setLive] = useState(true)
  const [analysis, setAnalysis] = useState(false)
  const [gridQuery, setGridQuery] = useState('')
  const cams = useCameras()
  const stats = useFrameStats()
  const cameras = useMemo(() => cams.data ?? [], [cams.data])
  const cameraId = camParam ?? cameras.find((c) => c.enabled)?.camera_id ?? cameras[0]?.camera_id ?? null

  const setParam = useCallback(
    (patch: Record<string, string | null>) =>
      setParams(
        (p) => {
          const n = new URLSearchParams(p)
          for (const [k, v] of Object.entries(patch)) {
            if (v == null) n.delete(k)
            else n.set(k, v)
          }
          return n
        },
        { replace: true },
      ),
    [setParams],
  )

  useWatchFrames(cameraId, mode === 'single')
  const camera = cameras.find((c) => c.camera_id === cameraId)
  const camMode = effectiveMode(useCameraHealth(cameraId ?? ''), camera)
  const feed = useFrameFeed(mode === 'single' ? cameraId : null, live)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  useEffect(() => setSelectedId(null), [cameraId])

  const following = live && selectedId === null
  const idx = useMemo(() => {
    if (following) return feed.frames.length ? 0 : -1
    const i = feed.frames.findIndex((f) => f.frame_id === selectedId)
    return i
  }, [feed.frames, following, selectedId])
  const frame = idx >= 0 ? feed.frames[idx] : (feed.frames[0] ?? null)
  const detail = useFrameDetail(frame?.camera_id ?? null, frame?.frame_id ?? null, analysis && !!frame?.analyzed)

  // ← older, → newer; stepping right past the newest frame resumes following
  useEffect(() => {
    if (mode !== 'single') return
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return
      const t = e.target as HTMLElement | null
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return
      e.preventDefault()
      const dir = e.key === 'ArrowLeft' ? -1 : 1
      const cur = idx >= 0 ? idx : 0
      if (dir === 1 && cur === 0) {
        setSelectedId(null)
        return
      }
      const next = stepIndex(cur, dir, feed.frames.length)
      if (next >= 0) setSelectedId(feed.frames[next].frame_id)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [mode, idx, feed.frames])

  const stat = stats.data?.find((s) => s.camera_id === cameraId)
  const totalFps = (stats.data ?? []).reduce((n, s) => n + s.fps, 0)
  const analyzedPct = stat && stat.frames > 0 ? Math.round((stat.analyzed / stat.frames) * 100) : null

  const gridCams = useMemo(() => {
    const n = gridQuery.trim().toLowerCase()
    return cameras.filter(
      (c) => c.enabled && (!n || c.name.toLowerCase().includes(n) || c.camera_id.includes(n)),
    )
  }, [cameras, gridQuery])

  return (
    <div className="flex min-h-0 flex-col gap-4">
      <PageHeader
        title="Extracted frames"
        hint="Frames the extractor pulled from each source, before the pipeline. Images are kept for 10 minutes."
        summary={
          <>
            <span className="num">
              {stats.data ? `${totalFps.toFixed(1)} fps across ${stats.data.length} sources` : 'Loading stats'}
            </span>
          </>
        }
        actions={
          <Segmented<Mode>
            value={mode}
            onChange={(m) => setParam({ mode: m === 'grid' ? 'grid' : null })}
            options={[
              { value: 'single', label: 'Camera' },
              { value: 'grid', label: 'All sources' },
            ]}
          />
        }
      />

      {cams.isError ? (
        <ErrorState message="Could not load cameras." onRetry={() => void cams.refetch()} />
      ) : cams.isLoading ? (
        <Skeleton className="h-64 w-full" />
      ) : cameras.length === 0 ? (
        <EmptyState icon={<Film />} title="No cameras yet" hint="Add a source and its frames appear here." />
      ) : mode === 'grid' ? (
        <>
          <div className="flex flex-wrap items-center gap-3">
            <div className="relative">
              <Search className="absolute top-2.5 left-2.5 size-4 text-faint" aria-hidden />
              <Input
                className="w-64 pl-8"
                placeholder="Filter sources"
                aria-label="Filter sources"
                value={gridQuery}
                onChange={(e) => setGridQuery(e.target.value)}
              />
            </div>
            <span className="text-xs text-muted-foreground">
              Latest frame per source, updated live. Tiles outside the viewport pause.
            </span>
          </div>
          <FrameGrid
            cameras={gridCams}
            stats={stats.data}
            onOpen={(id) => setParam({ mode: null, cam: id })}
          />
        </>
      ) : (
        <>
          <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
            <CameraCombobox cameras={cameras} value={cameraId} onChange={(id) => setParam({ cam: id })} />
            <ModeBadge mode={camMode} />
            <label className="flex items-center gap-2 text-[13px]">
              <Switch checked={live} onCheckedChange={setLive} aria-label="Live tail" />
              Live tail
            </label>
            <label className="flex items-center gap-2 text-[13px]">
              <Switch checked={analysis} onCheckedChange={setAnalysis} aria-label="Show analysis" />
              Show analysis
            </label>
            <dl className="num ml-auto flex items-center gap-4 text-xs text-muted-foreground">
              <div>
                <dt className="inline">fps </dt>
                <dd className="inline font-medium text-foreground">{stat ? stat.fps.toFixed(1) : '-'}</dd>
              </div>
              <div>
                <dt className="inline">frames </dt>
                <dd className="inline font-medium text-foreground">{stat ? stat.frames : '-'}</dd>
              </div>
              <div>
                <dt className="inline">analyzed </dt>
                <dd className="inline font-medium text-foreground">{analyzedPct != null ? `${analyzedPct}%` : '-'}</dd>
              </div>
            </dl>
          </div>

          {camMode === 'idle' && <IdleHint tsList={feed.frames.slice(0, 2).map((f) => f.ts)} />}
          {feed.error && feed.frames.length === 0 ? (
            <ErrorState message={`Could not load frames: ${feed.error}`} onRetry={feed.refresh} />
          ) : feed.initial ? (
            <Skeleton className="aspect-video w-full max-w-4xl" />
          ) : feed.frames.length === 0 ? (
            <EmptyState
              icon={<Film />}
              title="No frames for this camera yet"
              hint="The extractor has not produced frames in the last 10 minutes. Check the camera is enabled and assigned to a worker."
              action={<Button variant="outline" onClick={feed.refresh}>Refresh</Button>}
            />
          ) : (
            <div className="grid min-h-0 gap-4 min-[1100px]:grid-cols-[minmax(0,1fr)_320px]">
              <div className="flex min-w-0 flex-col gap-3">
                <FrameViewer
                  frame={frame}
                  detail={detail.data}
                  showAnalysis={analysis}
                  detailLoading={detail.isFetching && !detail.data}
                />
                <div className="flex items-center justify-between text-xs text-muted-foreground">
                  <span>
                    <kbd className="rounded border px-1 font-mono">←</kbd>{' '}
                    <kbd className="rounded border px-1 font-mono">→</kbd> step frames
                  </span>
                  {!following && (
                    <Button
                      size="sm"
                      variant="outline"
                      onClick={() => {
                        setSelectedId(null)
                        setLive(true)
                      }}
                    >
                      Jump to live
                    </Button>
                  )}
                  {following && <span className={cn('text-ok')}>Following newest frame</span>}
                </div>
                <Filmstrip
                  frames={feed.frames}
                  selectedId={frame?.frame_id ?? null}
                  follow={following}
                  onSelect={setSelectedId}
                  hasMore={feed.hasMore}
                  loading={feed.loading}
                  onLoadOlder={() => void feed.loadOlder()}
                />
              </div>
              <FrameMeta frame={frame} />
            </div>
          )}
        </>
      )}
    </div>
  )
}

/** Disappears as soon as frames arrive at full rate (the mode flag can lag by a heartbeat). */
function IdleHint({ tsList }: { tsList: number[] }) {
  const now = useNow()
  if (framesAtFullRate(tsList, now)) return null
  return (
    <p className="flex items-center gap-2 rounded-md border bg-muted px-3 py-2 text-xs text-muted-foreground">
      <Moon className="size-3.5 shrink-0" aria-hidden />
      Idle: 1 frame every 5 s. Opening this page switches it to full rate.
    </p>
  )
}
