import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, useLocation, useNavigate, useParams } from 'react-router-dom'
import { ArrowLeft, ChevronLeft, ChevronRight, Workflow } from 'lucide-react'
import { ErrorClassBadge } from '@/components/ErrorClassBadge'
import { MotionSummary } from '@/components/MotionSummary'
import { StateBadge } from '@/components/StatusDot'
import {
  Badge,
  Card,
  CardBody,
  CardHeader,
  CardTitle,
  EmptyState,
} from '@/components/ui/primitives'
import { describeWhen } from '@/features/pipelines/dag'
import { TOOL_ICON } from '@/features/pipelines/PlanGraph'
import { useCameras, usePlans } from '@/lib/api/queries'
import { faceLabel } from '@/lib/overlay'
import { hub } from '@/lib/realtime/hub'
import { useCameraHealth } from '@/lib/realtime/stores'
import { useNow } from '@/lib/realtime/useNow'
import type { Detection } from '@/lib/types'
import { formatAge, formatTime, pct } from '@/lib/utils'
import { AgeChip } from './AgeChip'
import { CameraPipelinePanel } from './CameraPipelinePanel'
import { DetectionOverlay } from './DetectionOverlay'
import { VideoSurface } from './VideoSurface'

export interface FeedRow {
  key: string
  ts: number
  kind: 'object' | 'plate' | 'face' | 'text'
  label: string
  confidence: number
}

export function flattenDetection(d: Detection): FeedRow[] {
  const rows: FeedRow[] = []
  const k = (s: string, i: number) => `${d.frame_id}:${d.step_id ?? 'main'}:${s}:${i}`
  d.objects.forEach((o, i) =>
    rows.push({
      key: k('o', i),
      ts: d.ts,
      kind: 'object',
      label: o.class,
      confidence: o.confidence,
    }),
  )
  d.plates.forEach((p, i) =>
    rows.push({
      key: k('p', i),
      ts: d.ts,
      kind: 'plate',
      label: p.plate,
      confidence: p.confidence,
    }),
  )
  d.faces.forEach((f, i) =>
    rows.push({
      key: k('f', i),
      ts: d.ts,
      kind: 'face',
      label: faceLabel(f),
      confidence: f.confidence,
    }),
  )
  d.texts.forEach((t, i) =>
    rows.push({ key: k('t', i), ts: d.ts, kind: 'text', label: t.text, confidence: t.confidence }),
  )
  return rows
}

/** Last N detection rows, React state updated at most ~3x/s. */
function useDetectionFeed(cameraId: string, max = 40): FeedRow[] {
  const [rows, setRows] = useState<FeedRow[]>([])
  const buf = useRef<FeedRow[]>([])
  useEffect(() => {
    buf.current = []
    setRows([])
    const off = hub.onDetections(cameraId, (d) => {
      buf.current = [...flattenDetection(d), ...buf.current].slice(0, max)
    })
    const id = setInterval(
      () => setRows((prev) => (prev[0]?.key === buf.current[0]?.key ? prev : buf.current)),
      333,
    )
    return () => {
      off()
      clearInterval(id)
    }
  }, [cameraId, max])
  return rows
}

const KIND_TONE = { object: 'info', plate: 'warning', face: 'success', text: 'neutral' } as const

export default function FocusPage() {
  const { cameraId = '' } = useParams()
  const nav = useNavigate()
  const { data: cameras } = useCameras()
  const { data: plans } = usePlans()
  const health = useCameraHealth(cameraId)
  const now = useNow()
  const camera = cameras?.find((c) => c.camera_id === cameraId)
  const plan = plans?.find((p) => p.plan_id === camera?.plan_id)
  const rows = useDetectionFeed(cameraId)

  useEffect(() => hub.subscribeCamera(cameraId), [cameraId])
  const { hash } = useLocation()
  useEffect(() => {
    if (hash === '#pipeline' && camera)
      document.getElementById('pipeline')?.scrollIntoView({ behavior: 'smooth' })
  }, [hash, camera])

  const idx = useMemo(
    () => cameras?.findIndex((c) => c.camera_id === cameraId) ?? -1,
    [cameras, cameraId],
  )
  const go = (d: number) => {
    if (!cameras?.length) return
    nav(`/live/${cameras[(idx + d + cameras.length) % cameras.length].camera_id}`)
  }

  if (cameras && !camera) return <EmptyState title="Camera not found" hint={cameraId} />

  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-center gap-3">
        <Link
          to="/"
          className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
        >
          <ArrowLeft className="size-4" /> Wall
        </Link>
        <h1 className="text-xl font-semibold">{camera?.name ?? cameraId}</h1>
        <span className="font-mono text-xs text-muted-foreground">{cameraId}</span>
        <StateBadge state={health?.state} />
        <div className="ml-auto flex gap-1">
          <button
            className="rounded p-1.5 hover:bg-accent"
            aria-label="Previous camera"
            onClick={() => go(-1)}
          >
            <ChevronLeft className="size-4" />
          </button>
          <button
            className="rounded p-1.5 hover:bg-accent"
            aria-label="Next camera"
            onClick={() => go(1)}
          >
            <ChevronRight className="size-4" />
          </button>
        </div>
      </div>

      <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_340px]">
        <div className="relative aspect-video overflow-hidden rounded-lg border bg-black">
          <VideoSurface cameraId={cameraId} label={camera?.name ?? cameraId} active />
          <DetectionOverlay cameraId={cameraId} active />
          <AgeChip cameraId={cameraId} active className="absolute right-2 bottom-2" />
        </div>

        <div className="flex flex-col gap-4">
          <Card>
            <CardHeader>
              <CardTitle>Stream</CardTitle>
            </CardHeader>
            <CardBody className="grid grid-cols-2 gap-y-2 text-sm">
              <Stat
                label="FPS"
                value={health ? `${health.fps_actual.toFixed(1)} / ${health.fps_target}` : '–'}
              />
              <Stat
                label="Last frame"
                value={formatAge(health?.last_frame_ts ? now - health.last_frame_ts : null)}
              />
              <Stat label="Worker" value={health?.worker_id ?? '–'} />
              <Stat label="Uptime 1h" value={health ? `${health.uptime_pct_1h}%` : '–'} />
              {health?.error_class && (
                <div className="col-span-2 space-y-1">
                  <ErrorClassBadge health={health} cameraId={cameraId} />
                  {health.error_hint && (
                    <div className="text-xs text-muted-foreground">{health.error_hint}</div>
                  )}
                </div>
              )}
              {health?.last_error && (
                <div className="col-span-2 text-xs text-destructive">{health.last_error}</div>
              )}
              <div className="col-span-2 border-t pt-2">
                <div className="mb-1.5 text-[11px] text-muted-foreground">Motion detection</div>
                <MotionSummary camera={camera} health={health} />
              </div>
            </CardBody>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle className="flex items-center gap-2">
                <Workflow className="size-4" /> Active plan
              </CardTitle>
              {plan ? (
                <Link
                  className="text-xs text-primary hover:underline"
                  to={`/pipelines?plan=${plan.plan_id}`}
                >
                  Watch
                </Link>
              ) : null}
            </CardHeader>
            <CardBody>
              {!plan ? (
                <div className="text-sm text-muted-foreground">
                  No pipeline on this camera.{' '}
                  <Link className="text-primary hover:underline" to="/pipelines">
                    Open pipelines
                  </Link>
                </div>
              ) : (
                <div className="space-y-2">
                  <div className="text-sm font-medium">
                    {plan.name}{' '}
                    <span className="text-xs text-muted-foreground">v{plan.version}</span>
                  </div>
                  <ol className="space-y-1.5">
                    {plan.steps.map((s) => {
                      const Icon = TOOL_ICON[s.tool]
                      return (
                        <li key={s.id} className="flex items-center gap-2 text-xs">
                          <Icon className="size-3.5 text-primary" />
                          <span className="font-mono">{s.id}</span>
                          <span className="truncate">{s.tool}</span>
                          <Badge tone={s.mode === 'queue' ? 'warning' : 'info'} className="ml-auto">
                            {s.mode}
                          </Badge>
                          <span className="hidden text-muted-foreground 2xl:inline">
                            {describeWhen(s.when)}
                          </span>
                        </li>
                      )
                    })}
                  </ol>
                </div>
              )}
            </CardBody>
          </Card>
        </div>
      </div>

      <CameraPipelinePanel cameraId={cameraId} plan={plan} />

      <Card>
        <CardHeader>
          <CardTitle>Live detections</CardTitle>
          <span className="text-xs text-muted-foreground">last {rows.length}</span>
        </CardHeader>
        <div className="max-h-64 overflow-auto">
          {rows.length === 0 ? (
            <EmptyState
              title="Waiting for detections…"
              hint="Detections stream at up to 3/s while the camera is online."
              className="py-6"
            />
          ) : (
            <table className="w-full text-sm">
              <tbody>
                {rows.map((r) => (
                  <tr key={r.key} className="border-b last:border-0">
                    <td className="w-24 px-4 py-1.5 font-mono text-xs text-muted-foreground">
                      {formatTime(r.ts)}
                    </td>
                    <td className="w-24 px-2">
                      <Badge tone={KIND_TONE[r.kind]}>{r.kind}</Badge>
                    </td>
                    <td className="px-2 font-medium">{r.label}</td>
                    <td className="px-4 text-right tabular-nums text-muted-foreground">
                      {pct(r.confidence)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </Card>
    </div>
  )
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <div className="text-[11px] text-muted-foreground">{label}</div>
      <div className="font-medium tabular-nums">{value}</div>
    </div>
  )
}
