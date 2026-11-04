import { MarkerType, type Edge, type Node } from '@xyflow/react'
import type { StepLive } from '@/lib/stepStatus'
import type { PipelineMetrics, Plan, PlanStep, PlanWhen, ToolName } from '@/lib/types'

export const FRAME_NODE_ID = 'frame'
export const NODE_W = 232
export const NODE_H = 132
const GAP_X = 96
const GAP_Y = 36

/** Which result bucket each tool produces (tools.json `output`). */
export const TOOL_OUTPUT: Record<ToolName, 'objects' | 'plates' | 'faces' | 'texts'> = {
  detect_objects: 'objects',
  read_plate: 'plates',
  read_text: 'texts',
  detect_faces: 'faces',
  identify_faces: 'faces',
}

/** 9.2: class may be a comma-separated any-of list: "car,truck,bus" -> "car | truck | bus" */
export const anyOf = (cls: string): string =>
  cls
    .split(',')
    .map((c) => c.trim())
    .filter(Boolean)
    .join(' | ')

export function describeWhen(w?: PlanWhen): string {
  if (!w || w.type === 'always') return 'always'
  switch (w.type) {
    case 'found':
      return w.min_confidence != null
        ? `found ${anyOf(w.class)} ≥ ${Math.round(w.min_confidence * 100)}%`
        : `found ${anyOf(w.class)}`
    case 'count':
      return `count(${anyOf(w.class)}) > ${w.gt}`
    case 'failed':
      return `${w.step} failed`
  }
}

export type StepNodeData = { step: PlanStep; metrics?: PipelineMetrics; live?: StepLive }
export type SourceNodeData = { label: string }
export type FlowNode = Node<StepNodeData, 'step'> | Node<SourceNodeData, 'source'>

/**
 * Plans are an ordered list; the DAG is implied by each step's `when`:
 *   - always / no `when`  -> fed straight by the camera frame
 *   - found / count       -> fed by the latest earlier step that produces `objects`
 *                            (falls back to the previous step)
 *   - failed {step}       -> fed by that step
 */
export function parentOf(plan: Plan, index: number): string {
  const step = plan.steps[index]
  const w = step.when
  if (!w || w.type === 'always') return FRAME_NODE_ID
  if (w.type === 'failed') return plan.steps.some((s) => s.id === w.step) ? w.step : FRAME_NODE_ID
  for (let i = index - 1; i >= 0; i--) {
    if (TOOL_OUTPUT[plan.steps[i].tool] === 'objects') return plan.steps[i].id
  }
  return index > 0 ? plan.steps[index - 1].id : FRAME_NODE_ID
}

export function planToFlow(
  plan: Plan,
  metrics?: Record<string, PipelineMetrics | undefined>,
  live?: Record<string, StepLive | undefined>,
): { nodes: FlowNode[]; edges: Edge[] } {
  const depth = new Map<string, number>([[FRAME_NODE_ID, 0]])
  const rowsAtDepth = new Map<number, number>()
  const nodes: FlowNode[] = [
    {
      id: FRAME_NODE_ID,
      type: 'source',
      position: { x: 0, y: 0 },
      data: {
        label: plan.camera_ids.length ? `${plan.camera_ids.length} camera(s)` : 'Camera frame',
      },
      draggable: false,
    },
  ]
  const edges: Edge[] = []
  rowsAtDepth.set(0, 1)

  plan.steps.forEach((step, i) => {
    const parent = parentOf(plan, i)
    const d = (depth.get(parent) ?? 0) + 1
    depth.set(step.id, d)
    const row = rowsAtDepth.get(d) ?? 0
    rowsAtDepth.set(d, row + 1)
    nodes.push({
      id: step.id,
      type: 'step',
      position: { x: d * (NODE_W + GAP_X), y: row * (NODE_H + GAP_Y) },
      data: { step, metrics: metrics?.[step.id], live: live?.[step.id] },
    })
    const label = step.when && step.when.type !== 'always' ? describeWhen(step.when) : undefined
    edges.push({
      id: `${parent}->${step.id}`,
      source: parent,
      target: step.id,
      label,
      type: 'smoothstep',
      animated: step.mode === 'inline' && (!!metrics?.[step.id] || !!live?.[step.id]?.ranOnLatest),
      markerEnd: { type: MarkerType.ArrowClosed },
      style: step.when?.type === 'failed' ? { strokeDasharray: '6 4' } : undefined,
    })
  })
  // centre the source node vertically next to its children
  const maxRows = Math.max(...rowsAtDepth.values())
  nodes[0].position.y = ((maxRows - 1) * (NODE_H + GAP_Y)) / 2
  return { nodes, edges }
}
