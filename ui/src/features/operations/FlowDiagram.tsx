import { createContext, memo, useContext, useMemo, useRef } from 'react'
import {
  Background,
  Controls,
  Handle,
  Position,
  ReactFlow,
  type NodeProps,
  type NodeTypes,
  type ReactFlowInstance,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { CircleDashed, Cpu, Globe, Layers, Moon, OctagonAlert, Radio, Server, TriangleAlert } from 'lucide-react'
import { StatusDot } from '@/components/StatusDot'
import { Badge } from '@/components/ui/primitives'
import { useTheme } from '@/app/theme'
import type { OpsWorker } from '@/lib/api/operations'
import { useCameraHealth, useHealthMap } from '@/lib/realtime/stores'
import { errorBadgeOf, errorHintOf } from '@/lib/errorClass'
import { effectiveMode } from '@/lib/mode'
import type { CameraConfig } from '@/lib/types'
import { cn } from '@/lib/utils'
import {
  CHIP_GAP,
  CHIP_H,
  CHIP_W,
  GROUP_PAD,
  layoutKey,
  layoutOps,
  type InfoNodeData,
  type OpsNode,
  type WorkerNodeData,
} from './flowGraph'
import { fmtLoad, utilTone } from './loadTone'
import { sourceInfo } from './mask'

const handleCls = '!size-1.5 !border-0 !bg-[var(--border-strong)]'
const STATE_TONE = { live: 'success', draining: 'warning', dead: 'danger' } as const

export type FlowSelection = { camera: string | null; worker: string | null }
interface FlowCtx {
  cams: ReadonlyMap<string, CameraConfig>
  selected: FlowSelection
  onCamera: (id: string) => void
  onWorker: (id: string) => void
}
const FlowContext = createContext<FlowCtx | null>(null)
const useFlow = () => {
  const c = useContext(FlowContext)
  if (!c) throw new Error('FlowContext missing')
  return c
}

/** One camera inside its worker. Reads its own live health, so ticks never touch the graph. */
const CameraChip = memo(function CameraChip({ id, assigned }: { id: string; assigned: boolean }) {
  const { cams, selected, onCamera } = useFlow()
  const cam = cams.get(id)
  const h = useCameraHealth(id)
  const state = assigned ? h?.state : 'unassigned'
  const src = sourceInfo(cam?.stream_url)
  const low = h != null && h.fps_target > 0 && h.fps_actual < h.fps_target * 0.8
  const idle = effectiveMode(h, cam) === 'idle'
  const err = state === 'online' || state === 'unassigned' ? null : errorBadgeOf(h)
  const SrcIcon = src.kind === 'publisher' ? Radio : Globe
  return (
    <button
      type="button"
      onClick={() => onCamera(id)}
      aria-pressed={selected.camera === id}
      aria-label={`Camera ${cam?.name ?? id}, ${state ?? 'unknown'}`}
      title={`${cam?.name ?? id} (${id})\n${src.kind === 'publisher' ? 'Publisher' : `Stream ${src.label}`}${err && h ? ' | ' + errorHintOf(h) : ''}`}
      style={{ width: CHIP_W, height: CHIP_H }}
      className={cn(
        'flex flex-col justify-center gap-0.5 rounded-md border bg-background px-2 text-left transition-colors hover:bg-muted',
        selected.camera === id && 'border-primary bg-accent ring-1 ring-primary',
      )}
    >
      <span className="flex items-center gap-1.5">
        <StatusDot state={state} />
        <span className="min-w-0 flex-1 truncate text-xs font-medium">{cam?.name ?? id}</span>
        <span className={cn('num shrink-0 text-[11px]', low ? 'text-warn' : 'text-muted-foreground')}>
          {h ? h.fps_actual.toFixed(1) : '-'}
          <span className="text-faint"> fps</span>
        </span>
      </span>
      <span className="flex items-center gap-1 text-[10px] text-faint">
        <SrcIcon className="size-3 shrink-0" aria-hidden />
        <span className="min-w-0 flex-1 truncate font-mono">{src.label}</span>
        {idle && state !== 'offline' && state !== 'unassigned' && (
          <span
            className="flex shrink-0 items-center gap-0.5 rounded-full bg-muted px-1.5 font-sans font-medium text-muted-foreground"
            title="Idle: 1 frame every 5 s"
          >
            <Moon className="size-2.5" aria-hidden />
            idle
          </span>
        )}
        {state && state !== 'online' && state !== 'unassigned' && (
          <span
            className={cn(
              'flex max-w-[92px] shrink-0 items-center gap-0.5 font-sans font-medium',
              err?.terminal || (!err && state === 'offline') ? 'text-err' : 'text-warn',
            )}
          >
            {err && (err.terminal ? <OctagonAlert className="size-2.5" aria-hidden /> : <TriangleAlert className="size-2.5" aria-hidden />)}
            <span className="truncate">{err ? err.label : state}</span>
          </span>
        )}
      </span>
    </button>
  )
})

/** Aggregates for a group header; the only per-tick work outside the chips. */
function GroupStats({ ids }: { ids: string[] }) {
  const health = useHealthMap()
  let fps = 0
  let bad = 0
  for (const id of ids) {
    const h = health.get(id)
    if (!h) continue
    fps += h.fps_actual
    if (h.state === 'degraded' || h.state === 'offline') bad++
  }
  return (
    <div className="num flex items-center justify-between text-[11px] text-muted-foreground">
      <span>{fps.toFixed(1)} fps total</span>
      {bad > 0 ? <span className="text-warn">{bad} unhealthy</span> : <span className="text-ok">all healthy</span>}
    </div>
  )
}

const WorkerGroup = memo(function WorkerGroup({ data }: NodeProps) {
  const d = data as WorkerNodeData
  const { selected, onWorker } = useFlow()
  const assigned = d.worker_id != null
  const n = d.cameraIds.length
  const pct = d.capacity > 0 ? Math.round((d.load / d.capacity) * 100) : 0
  const sel = assigned && selected.worker === d.worker_id
  return (
    <div
      className={cn(
        'size-full rounded-xl border bg-card',
        !assigned && 'border-dashed border-unas/50',
        d.state === 'dead' && 'border-err/60',
        d.state === 'draining' && 'border-dashed border-warn/60',
        sel && 'ring-2 ring-primary',
      )}
      style={{ padding: GROUP_PAD }}
    >
      {assigned && <Handle type="source" position={Position.Right} className={handleCls} />}
      {assigned ? (
        <button
          type="button"
          onClick={() => onWorker(d.worker_id!)}
          aria-pressed={sel}
          aria-label={`Worker ${d.worker_id}`}
          className="mb-1.5 block h-[80px] w-full rounded-md px-1 text-left hover:bg-muted/60"
        >
          <span className="flex items-center justify-between gap-2">
            <span className="flex items-center gap-1.5 font-mono text-[13px] font-semibold">
              <Server className="size-4 text-faint" aria-hidden />
              {d.worker_id}
            </span>
            <span className="flex items-center gap-1">
              <Badge tone="neutral" className="h-[18px] text-[10px]">
                {d.managed_by === 'controller' ? 'autoscaled' : 'static'}
              </Badge>
              {d.state && <Badge tone={STATE_TONE[d.state]}>{d.state}</Badge>}
            </span>
          </span>
          <span className="num mt-1.5 flex items-baseline justify-between text-xs">
            <span className="text-muted-foreground">
              <b className="text-foreground">{fmtLoad(d.load)}</b> / {d.capacity} load · {n} cams
            </span>
            <span className="font-medium">{pct}%</span>
          </span>
          <span
            className="mt-1 block h-1.5 overflow-hidden rounded-full bg-muted"
            role="progressbar"
            aria-valuenow={pct}
            aria-valuemin={0}
            aria-valuemax={100}
            aria-label={`${d.worker_id} load`}
          >
            <span className={cn('block h-full rounded-full', utilTone(pct))} style={{ width: `${Math.min(100, pct)}%` }} />
          </span>
          <span className="mt-1 block">
            <GroupStats ids={d.cameraIds} />
          </span>
        </button>
      ) : (
        <div className="mb-1.5 h-[80px] px-1">
          <div className="flex items-center gap-1.5 text-[13px] font-semibold">
            <CircleDashed className="size-4 text-unas" aria-hidden /> Unassigned
          </div>
          <div className="num mt-1.5 text-xs text-muted-foreground">
            <b className="text-foreground">{n}</b> cameras waiting for a worker
          </div>
        </div>
      )}
      {n === 0 ? (
        <div className="grid h-[30px] place-items-center text-[11px] text-faint">No cameras assigned</div>
      ) : (
        <div
          className="grid"
          style={{ gridTemplateColumns: `repeat(${d.cols}, ${CHIP_W}px)`, gap: CHIP_GAP }}
        >
          {d.cameraIds.map((id) => (
            <CameraChip key={id} id={id} assigned={assigned} />
          ))}
        </div>
      )}
    </div>
  )
})

function KafkaRate() {
  const health = useHealthMap()
  let fps = 0
  for (const h of health.values()) if (h.state !== 'offline' && h.state !== 'unassigned') fps += h.fps_actual
  return <li>{fps.toFixed(1)} frames/s</li>
}

const InfoNode = memo(function InfoNode({ data }: NodeProps) {
  const d = data as InfoNodeData
  const Icon = d.id === 'engines' ? Cpu : d.id === 'executor' ? Cpu : Layers
  return (
    <div className="size-full rounded-xl border bg-card p-3">
      <Handle type="target" position={d.id === 'kafka' ? Position.Left : Position.Top} className={handleCls} />
      <div className="flex items-center gap-1.5 text-[13px] font-semibold">
        <Icon className="size-4 text-faint" aria-hidden />
        {d.title}
      </div>
      <div className="text-[11px] text-muted-foreground">{d.subtitle}</div>
      <ul className="num mt-1.5 text-[11px] text-muted-foreground">{d.id === 'kafka' && <KafkaRate />}</ul>
      {d.id !== 'engines' && <Handle type="source" position={Position.Bottom} className={handleCls} />}
    </div>
  )
})

const nodeTypes: NodeTypes = { worker: WorkerGroup, info: InfoNode }
const FIT = { padding: 0.04, minZoom: 0.55, maxZoom: 1 }

/**
 * Workers as containers holding their camera chips, wrapping into rows, feeding
 * Kafka -> executor -> engines. Laid out once per assignment set; status ticks only re-render chips.
 */
export function FlowDiagram({
  workers,
  cameras,
  selection,
  onSelectCamera,
  onSelectWorker,
}: {
  workers: OpsWorker[]
  cameras: CameraConfig[]
  selection: FlowSelection
  onSelectCamera: (id: string) => void
  onSelectWorker: (id: string) => void
}) {
  const { theme } = useTheme()
  const wrap = useRef<HTMLDivElement>(null)
  const key = layoutKey(workers, cameras)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const graph = useMemo(() => layoutOps({ workers, cameras }), [key])
  const cams = useMemo(() => new Map(cameras.map((c) => [c.camera_id, c])), [cameras])
  const ctx = useMemo<FlowCtx>(
    () => ({ cams, selected: selection, onCamera: onSelectCamera, onWorker: onSelectWorker }),
    [cams, selection, onSelectCamera, onSelectWorker],
  )

  // Open at a readable zoom (never below 0.75), anchored top-left; bigger graphs are panned.
  const onInit = (rf: ReactFlowInstance<OpsNode>) => {
    const w = wrap.current?.clientWidth ?? 1200
    const zoom = Math.min(1, Math.max(0.75, (w - 24) / Math.max(graph.width, 1)))
    void rf.setViewport({ x: 12, y: 12, zoom })
  }

  return (
    <FlowContext.Provider value={ctx}>
      <div
        ref={wrap}
        style={{ height: `min(${Math.max(300, Math.ceil(graph.height) + 56)}px, max(440px, calc(100vh - 200px)))` }}
        className="relative w-full overflow-hidden rounded-xl border bg-background"
      >
        <ReactFlow<OpsNode>
          nodes={graph.nodes}
          edges={graph.edges}
          nodeTypes={nodeTypes}
          colorMode={theme}
          onInit={onInit}
          fitViewOptions={FIT}
          nodesDraggable={false}
          nodesConnectable={false}
          elementsSelectable={false}
          zoomOnScroll={false}
          preventScrolling={false}
          proOptions={{ hideAttribution: true }}
          minZoom={0.3}
          maxZoom={1.5}
        >
          <Background gap={20} />
          <Controls showInteractive={false} fitViewOptions={FIT} />
        </ReactFlow>
        <div className="pointer-events-none absolute bottom-2 left-14 flex items-center gap-3 rounded-md bg-background/80 px-2 py-1 text-[11px] text-muted-foreground backdrop-blur-sm">
          <span className="inline-flex items-center gap-1"><StatusDot state="online" /> online</span>
          <span className="inline-flex items-center gap-1"><StatusDot state="degraded" /> degraded</span>
          <span className="inline-flex items-center gap-1"><StatusDot state="offline" /> offline</span>
          <span className="inline-flex items-center gap-1"><StatusDot state="unassigned" /> unassigned</span>
          <span className="hidden sm:inline">Drag to pan, Ctrl+scroll to zoom</span>
        </div>
      </div>
    </FlowContext.Provider>
  )
}
