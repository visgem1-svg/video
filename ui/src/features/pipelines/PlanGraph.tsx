import { memo, useMemo } from 'react'
import {
  Background,
  Controls,
  Handle,
  Position,
  ReactFlow,
  type NodeProps,
  type NodeTypes,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import {
  Camera,
  Car,
  ScanFace,
  ScanSearch,
  ScanText,
  UserCheck,
  type LucideIcon,
} from 'lucide-react'
import { Badge } from '@/components/ui/primitives'
import type { StepLive } from '@/lib/stepStatus'
import { useTheme } from '@/app/theme'
import type { PipelineMetrics, Plan, ToolName } from '@/lib/types'
import { cn } from '@/lib/utils'
import {
  NODE_H,
  NODE_W,
  planToFlow,
  type FlowNode,
  type SourceNodeData,
  type StepNodeData,
} from './dag'

export const TOOL_ICON: Record<ToolName, LucideIcon> = {
  detect_objects: ScanSearch,
  read_plate: Car,
  read_text: ScanText,
  detect_faces: ScanFace,
  identify_faces: UserCheck,
}

const StepNodeView = memo(function StepNodeView({ data }: NodeProps & { data: StepNodeData }) {
  const { step, metrics: m, live } = data
  const Icon = TOOL_ICON[step.tool]
  const params = Object.entries(step.params ?? {})
    .map(([k, v]) => `${k}=${v}`)
    .join(' ')
  return (
    <div
      style={{ width: NODE_W, minHeight: NODE_H }}
      className={cn(
        'rounded-lg border bg-card p-2.5 shadow-sm',
        m && m.errors > 0 && 'border-destructive/70',
        live?.ranOnLatest && 'border-success ring-2 ring-success/40',
        live?.result === 'failed' && 'border-destructive/70',
      )}
    >
      <Handle type="target" position={Position.Left} className="!bg-muted-foreground" />
      <div className="flex items-center gap-2">
        <span className="grid size-7 place-items-center rounded-md bg-primary/15 text-primary">
          <Icon className="size-4" />
        </span>
        <div className="min-w-0 flex-1">
          <div className="truncate text-sm font-semibold">{step.tool}</div>
          <div className="text-[11px] text-muted-foreground">{step.id}</div>
        </div>
        <Badge tone={step.mode === 'queue' ? 'warning' : 'info'}>{step.mode}</Badge>
      </div>
      {params && (
        <div className="mt-1 truncate font-mono text-[11px] text-muted-foreground">{params}</div>
      )}
      {live && <LiveBlock live={live} />}
      {m && (
        <div className="mt-2 grid grid-cols-3 gap-x-2 gap-y-0.5 border-t pt-1.5 text-[11px] tabular-nums">
          <Metric label="proc/s" value={(m.processed / m.window_s).toFixed(1)} />
          <Metric label="skipped" value={String(m.skipped)} />
          <Metric label="errors" value={String(m.errors)} bad={m.errors > 0} />
          <Metric label="p50" value={`${m.p50_ms.toFixed(0)}ms`} />
          <Metric label="p95" value={`${m.p95_ms.toFixed(0)}ms`} />
        </div>
      )}
      <Handle type="source" position={Position.Right} className="!bg-muted-foreground" />
    </div>
  )
})

function Metric({ label, value, bad }: { label: string; value: string; bad?: boolean }) {
  return (
    <div>
      <div className="text-[10px] text-muted-foreground">{label}</div>
      <div className={cn('font-medium', bad && 'text-destructive')}>{value}</div>
    </div>
  )
}

const RESULT_TONE = {
  ran: 'success',
  skipped: 'neutral',
  failed: 'danger',
  pending: 'warning',
  none: 'neutral',
} as const
const RESULT_LABEL = {
  ran: 'ran',
  skipped: 'skipped (condition)',
  failed: 'failed',
  pending: 'queued',
  none: 'no data yet',
} as const

function LiveBlock({ live }: { live: StepLive }) {
  return (
    <div className="mt-1.5 space-y-1 border-t pt-1.5 text-[11px]">
      <div className="flex items-center justify-between gap-1">
        <Badge tone={RESULT_TONE[live.result]}>{RESULT_LABEL[live.result]}</Badge>
        <span className="tabular-nums text-muted-foreground">
          {live.lastMs != null ? `${live.lastMs.toFixed(0)} ms` : '–'}
        </span>
      </div>
      <div className="tabular-nums text-muted-foreground">
        60s: ran {live.ran60} · skipped {live.skipped60}
      </div>
      {live.summary && (
        <div className="truncate font-medium" title={live.summary}>
          {live.summary}
        </div>
      )}
    </div>
  )
}

const SourceNodeView = memo(function SourceNodeView({
  data,
}: NodeProps & { data: SourceNodeData }) {
  return (
    <div className="flex items-center gap-2 rounded-full border bg-muted px-3 py-2 text-xs font-medium">
      <Camera className="size-4 text-primary" />
      {data.label}
      <Handle type="source" position={Position.Right} className="!bg-muted-foreground" />
    </div>
  )
})

const nodeTypes: NodeTypes = {
  step: StepNodeView as unknown as NodeTypes[string],
  source: SourceNodeView as unknown as NodeTypes[string],
}

export function PlanGraph({
  plan,
  metrics,
  live,
  className,
}: {
  plan: Plan
  metrics?: Record<string, PipelineMetrics | undefined>
  live?: Record<string, StepLive | undefined>
  className?: string
}) {
  const { theme } = useTheme()
  const { nodes, edges } = useMemo(() => planToFlow(plan, metrics, live), [plan, metrics, live])
  return (
    <div
      className={cn('h-[360px] w-full overflow-hidden rounded-lg border bg-background', className)}
    >
      <ReactFlow<FlowNode>
        nodes={nodes}
        edges={edges}
        nodeTypes={nodeTypes}
        colorMode={theme}
        fitView
        fitViewOptions={{ padding: 0.15, maxZoom: 1 }}
        nodesDraggable={false}
        nodesConnectable={false}
        elementsSelectable={false}
        proOptions={{ hideAttribution: true }}
        minZoom={0.3}
      >
        <Background gap={20} />
        <Controls showInteractive={false} />
      </ReactFlow>
    </div>
  )
}
