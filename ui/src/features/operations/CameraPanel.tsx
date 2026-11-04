import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { ExternalLink, Pin, PinOff, RotateCw, Workflow } from 'lucide-react'
import { ModeBadge } from '@/components/ModeBadge'
import { ErrorClassBadge } from '@/components/ErrorClassBadge'
import { MotionSummary } from '@/components/MotionSummary'
import { StateBadge } from '@/components/StatusDot'
import { Sheet } from '@/components/ui/sheet'
import { Badge, Button, EmptyState, Skeleton, Tabs } from '@/components/ui/primitives'
import { useWatchFrames } from '@/features/frames/useWatchFrames'
import { Filmstrip } from '@/features/frames/Filmstrip'
import { FrameMeta, FrameViewer } from '@/features/frames/FrameViewer'
import { useFrameFeed } from '@/features/frames/useFrameFeed'
import { AgeChip } from '@/features/live/AgeChip'
import { CameraPipelinePanel } from '@/features/live/CameraPipelinePanel'
import { DetectionOverlay } from '@/features/live/DetectionOverlay'
import { VideoSurface } from '@/features/live/VideoSurface'
import { assignedOf, type OpsWorker } from '@/lib/api/operations'
import { usePlans } from '@/lib/api/queries'
import { effectiveMode } from '@/lib/mode'
import { hub } from '@/lib/realtime/hub'
import { useCameraHealth } from '@/lib/realtime/stores'
import { useNow } from '@/lib/realtime/useNow'
import type { CameraConfig } from '@/lib/types'
import { formatAge } from '@/lib/utils'
import { maskStreamUrl } from './mask'
import type { OpsActions } from './useOpsActions'

type Tab = 'info' | 'live' | 'pipeline' | 'frames'
const TABS: Array<{ value: Tab; label: string }> = [
  { value: 'info', label: 'Info' },
  { value: 'live', label: 'Live' },
  { value: 'pipeline', label: 'Pipeline' },
  { value: 'frames', label: 'Frames' },
]

function Row({ k, children, mono = true }: { k: string; children: ReactNode; mono?: boolean }) {
  return (
    <div className="flex items-baseline justify-between gap-3 border-b py-1.5 last:border-0">
      <dt className="shrink-0 text-xs text-muted-foreground">{k}</dt>
      <dd className={`num min-w-0 break-all text-right text-xs ${mono ? 'font-mono' : ''}`}>{children}</dd>
    </div>
  )
}
function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section>
      <h3 className="mb-1 text-[11px] font-semibold tracking-wide text-faint uppercase">{title}</h3>
      <dl>{children}</dl>
    </section>
  )
}

/** Side panel for one camera. Only the open tab mounts, so video / polling run only while visible. */
export function CameraPanel({
  cameraId,
  cameras,
  workers,
  ops,
  onSelectWorker,
  onWideChange,
  onClose,
}: {
  cameraId: string
  cameras: CameraConfig[]
  workers: OpsWorker[]
  ops: OpsActions
  onSelectWorker: (id: string) => void
  /** the pipeline tab asks for a wider drawer; the page makes room for it */
  onWideChange: (wide: boolean) => void
  onClose: () => void
}) {
  const [tab, setTab] = useState<Tab>('info')
  useEffect(() => setTab('info'), [cameraId])
  useEffect(() => {
    onWideChange(tab === 'pipeline')
    return () => onWideChange(false)
  }, [tab, onWideChange])
  const cam = cameras.find((c) => c.camera_id === cameraId)
  const health = useCameraHealth(cameraId)
  const name = cam?.name ?? cameraId

  return (
    <Sheet
      open
      wide={tab === 'pipeline'}
      onOpenChange={(o) => !o && onClose()}
      title={name}
      description={`Camera ${cameraId}`}
      actions={
        <div className="mt-1 flex items-center gap-2">
          <span className="font-mono text-xs text-muted-foreground">{cameraId}</span>
          <StateBadge state={health?.state} />
          <ModeBadge mode={effectiveMode(health, cam)} />
        </div>
      }
    >
      <div className="px-4">
        <Tabs<Tab> value={tab} onChange={setTab} options={TABS} />
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto p-4">
        {!cam ? (
          <EmptyState title="Camera not found" hint="It may have been removed or disabled." className="py-8" />
        ) : tab === 'info' ? (
          <InfoTab cam={cam} workers={workers} ops={ops} onSelectWorker={onSelectWorker} />
        ) : tab === 'live' ? (
          <LiveTab cam={cam} />
        ) : tab === 'pipeline' ? (
          <PipelineTab cam={cam} />
        ) : (
          <FramesTab cameraId={cameraId} />
        )}
      </div>
    </Sheet>
  )
}

function InfoTab({
  cam,
  workers,
  ops,
  onSelectWorker,
}: {
  cam: CameraConfig
  workers: OpsWorker[]
  ops: OpsActions
  onSelectWorker: (id: string) => void
}) {
  const health = useCameraHealth(cam.camera_id)
  const now = useNow()
  const { data: plans } = usePlans()
  const owner = useMemo(() => {
    for (const w of workers) {
      const a = assignedOf(w).find((x) => x.camera_id === cam.camera_id)
      if (a) return { worker: w.worker_id, pinned: !!a.pinned, generation: a.generation }
    }
    return null
  }, [workers, cam.camera_id])
  const assigned = owner?.worker ?? cam.assigned_worker ?? health?.worker_id ?? null
  const pinned = owner?.pinned ? assigned : (cam.pinned_worker ?? null)
  const generation = owner?.generation ?? cam.generation
  const plan = plans?.find((p) => p.plan_id === cam.plan_id)

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap gap-2">
        <Button size="sm" variant="outline" onClick={() => ops.restart(cam.camera_id, cam.name)}>
          <RotateCw /> Restart stream
        </Button>
        <Button size="sm" variant="outline" onClick={() => ops.move(cam.camera_id, cam.name, assigned)}>
          <Pin /> {pinned ? 'Re-pin to worker' : 'Pin to worker'}
        </Button>
        {pinned && (
          <Button size="sm" variant="ghost" onClick={() => ops.unpin(cam.camera_id)}>
            <PinOff /> Unpin
          </Button>
        )}
      </div>

      {cam.provision_error && (
        <p className="rounded-md border border-err/30 bg-err-bg px-3 py-2 text-xs text-err">
          Provisioning failed: {cam.provision_error}
        </p>
      )}

      <Section title="Status">
        <Row k="State" mono={false}>
          <StateBadge state={health?.state} />
        </Row>
        <Row k="Extraction" mono={false}>
          {effectiveMode(health, cam) === 'idle' ? 'idle (1 frame / 5 s)' : 'full rate'}
        </Row>
        <Row k="FPS (actual / target)">
          {health ? `${health.fps_actual.toFixed(1)} / ${health.fps_target}` : '-'}
        </Row>
        <Row k="Last frame">
          {health?.last_frame_ts ? `${formatAge(now - health.last_frame_ts)} ago` : '-'}
        </Row>
        <Row k="Restarts (1h)">{health ? String(health.reconnects_1h) : '-'}</Row>
        <Row k="Uptime (1h)">{health ? `${health.uptime_pct_1h}%` : '-'}</Row>
        {health?.error_class && (
          <Row k="Error" mono={false}>
            <div className="flex flex-col items-end gap-1">
              <ErrorClassBadge health={health} cameraId={cam.camera_id} />
              {health.error_hint && (
                <span className="text-muted-foreground">{health.error_hint}</span>
              )}
            </div>
          </Row>
        )}
        {health?.last_error && (
          <Row k="Last error">
            <span className="text-err">{health.last_error}</span>
          </Row>
        )}
      </Section>

      <section>
        <h3 className="mb-1.5 text-[11px] font-semibold tracking-wide text-faint uppercase">
          Motion detection
        </h3>
        <MotionSummary camera={cam} health={health} />
      </section>

      <Section title="Placement">
        <Row k="Assigned worker" mono={false}>
          {assigned ? (
            <button type="button" className="font-mono text-primary hover:underline" onClick={() => onSelectWorker(assigned)}>
              {assigned}
            </button>
          ) : (
            <Badge tone="unassigned">unassigned</Badge>
          )}
        </Row>
        <Row k="Pinned worker">{pinned ?? 'not pinned'}</Row>
        <Row k="Generation">{generation != null ? String(generation) : '-'}</Row>
      </Section>

      <Section title="Configuration">
        <Row k="camera_id">{cam.camera_id}</Row>
        <Row k="Name" mono={false}>
          {cam.name}
        </Row>
        <Row k="Enabled" mono={false}>
          {cam.enabled ? 'yes' : 'no'}
        </Row>
        <Row k="Analysis FPS">{String(cam.analysis_fps)}</Row>
        <Row k="Plan" mono={false}>
          {cam.plan_id ? (
            <Link className="text-primary hover:underline" to={`/pipelines?plan=${cam.plan_id}`}>
              {plan ? `${plan.name} v${plan.version}` : cam.plan_id}
            </Link>
          ) : (
            'none'
          )}
        </Row>
        <Row k="source_url">{cam.source_url ?? '-'}</Row>
        <Row k="stream_url">{maskStreamUrl(cam.stream_url) === 'sim publisher' ? 'publisher' : maskStreamUrl(cam.stream_url)}</Row>
        {cam.analysis_stream_url && (
          <Row k="analysis_stream_url">{maskStreamUrl(cam.analysis_stream_url)}</Row>
        )}
        <Row k="Duplicate of">{cam.duplicate_of?.length ? cam.duplicate_of.join(', ') : 'unique'}</Row>
        <Row k="Provision error">{cam.provision_error ?? 'none'}</Row>
      </Section>
    </div>
  )
}

function LiveTab({ cam }: { cam: CameraConfig }) {
  useEffect(() => hub.subscribeCamera(cam.camera_id), [cam.camera_id])
  return (
    <div className="flex flex-col gap-2">
      <div className="relative aspect-video overflow-hidden rounded-lg border bg-black">
        <VideoSurface cameraId={cam.camera_id} label={cam.name} active />
        <DetectionOverlay cameraId={cam.camera_id} active />
        <AgeChip cameraId={cam.camera_id} active className="absolute right-2 bottom-2" />
      </div>
      <Link to={`/live/${cam.camera_id}`} className="inline-flex items-center gap-1 self-start text-xs text-primary hover:underline">
        Open full view <ExternalLink className="size-3" aria-hidden />
      </Link>
    </div>
  )
}

function PipelineTab({ cam }: { cam: CameraConfig }) {
  const { data: plans, isLoading } = usePlans()
  useEffect(() => hub.subscribeCamera(cam.camera_id), [cam.camera_id])
  const plan = plans?.find((p) => p.plan_id === cam.plan_id)
  if (isLoading) return <Skeleton className="h-72 w-full" />
  if (!plan)
    return (
      <EmptyState
        icon={<Workflow />}
        title="No pipeline on this camera"
        hint="Assign a plan to watch it execute here."
        action={
          <Button asChild variant="outline" size="sm">
            <Link to="/pipelines">Open pipelines</Link>
          </Button>
        }
        className="py-8"
      />
    )
  return <CameraPipelinePanel cameraId={cam.camera_id} plan={plan} />
}

function FramesTab({ cameraId }: { cameraId: string }) {
  useWatchFrames(cameraId)
  const feed = useFrameFeed(cameraId, true)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  useEffect(() => setSelectedId(null), [cameraId])
  const following = selectedId === null
  const frame = (following ? feed.frames[0] : feed.frames.find((f) => f.frame_id === selectedId)) ?? feed.frames[0] ?? null
  return (
    <div className="flex flex-col gap-3">
      {feed.initial ? (
        <Skeleton className="aspect-video w-full" />
      ) : feed.frames.length === 0 ? (
        <EmptyState
          title="No frames yet"
          hint="The extractor has not produced frames for this camera in the last 10 minutes."
          className="py-8"
        />
      ) : (
        <>
          <FrameViewer frame={frame} detail={undefined} showAnalysis={false} detailLoading={false} />
          <Filmstrip
            frames={feed.frames}
            selectedId={frame?.frame_id ?? null}
            follow={following}
            onSelect={setSelectedId}
            hasMore={feed.hasMore}
            loading={feed.loading}
            onLoadOlder={() => void feed.loadOlder()}
          />
          {!following && (
            <Button size="sm" variant="outline" className="self-start" onClick={() => setSelectedId(null)}>
              Jump to newest
            </Button>
          )}
          <FrameMeta frame={frame} />
        </>
      )}
      <Link
        to={`/frames?camera=${encodeURIComponent(cameraId)}`}
        className="inline-flex items-center gap-1 self-start text-xs text-primary hover:underline"
      >
        Open in Extracted frames <ExternalLink className="size-3" aria-hidden />
      </Link>
    </div>
  )
}
