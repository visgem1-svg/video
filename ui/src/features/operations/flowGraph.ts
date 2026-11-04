import type { Edge, Node } from '@xyflow/react'
import { assignedOf, type ManagedBy, type OpsWorker, type WorkerState } from '@/lib/api/operations'
import type { CameraConfig, HealthState } from '@/lib/types'

// Geometry (px at zoom 1). Camera chips live inside their worker group, so a group's size follows
// its camera count and the page stays legible at 30-100 cameras without fit-to-view shrinking it.
export const CHIP_W = 172
export const CHIP_H = 42
export const CHIP_GAP = 6
export const GROUP_PAD = 10
export const GROUP_HEAD_H = 96
export const GROUP_GAP = 28
export const EMPTY_BODY_H = 30
const ROW_MAX_W = 1240
const STACK_GAP = 96
export const INFO_W = 176
export const INFO_H = 76
const INFO_GAP = 44

export type NodeKind = 'worker' | 'info'
export type WorkerNodeData = {
  kind: 'worker'
  /** null = the "Unassigned" group */
  worker_id: string | null
  state: WorkerState | null
  managed_by: ManagedBy | null
  capacity: number
  capacity_override: number | null
  /** weighted load vs capacity (idle cameras weigh 0.1); falls back to the camera count */
  load: number
  cameraIds: string[]
  cols: number
}
export type InfoNodeData = {
  kind: 'info'
  id: 'kafka' | 'executor' | 'engines'
  title: string
  subtitle: string
}
export type FlowData = WorkerNodeData | InfoNodeData
export type OpsNode = Node<FlowData, NodeKind>

export interface FlowLayout {
  nodes: OpsNode[]
  edges: Edge[]
  width: number
  height: number
}

const numeric = new Intl.Collator('en', { numeric: true })
const byId = (a: string, b: string) => numeric.compare(a, b)

/** 2 columns for a typical worker, 3 once it carries more than 14 cameras. */
export const chipColumns = (n: number): number => (n > 14 ? 3 : 2)

export function groupSize(n: number, cols = chipColumns(n)): { w: number; h: number } {
  const rows = Math.ceil(n / cols)
  const body = rows === 0 ? EMPTY_BODY_H : rows * CHIP_H + (rows - 1) * CHIP_GAP
  return {
    w: cols * CHIP_W + (cols - 1) * CHIP_GAP + 2 * GROUP_PAD,
    h: GROUP_HEAD_H + body + GROUP_PAD,
  }
}

/**
 * Cache key of everything the layout depends on. Health ticks are NOT part of it: chips read
 * their own live status, so the graph is laid out again only when assignments or worker meta change.
 */
export function layoutKey(workers: OpsWorker[], cameras: CameraConfig[]): string {
  const w = workers
    .map(
      (x) =>
        `${x.worker_id}|${x.state}|${x.managed_by}|${x.capacity}|${x.capacity_override ?? ''}|${x.load ?? ''}|` +
        assignedOf(x)
          .map((a) => a.camera_id)
          .join(','),
    )
    .join(';')
  const c = cameras
    .filter((x) => x.enabled)
    .map((x) => x.camera_id)
    .join(',')
  return `${w}#${c}`
}

export const worstState = (states: Array<HealthState | undefined>): HealthState | undefined => {
  const sev: Record<string, number> = { offline: 3, degraded: 2, unassigned: 1, online: 0 }
  let best: HealthState | undefined
  for (const s of states) if (s && (best === undefined || sev[s] > sev[best])) best = s
  return best
}

const INFO: Array<Omit<InfoNodeData, 'kind'>> = [
  { id: 'kafka', title: 'frames.raw', subtitle: 'Kafka topic' },
  { id: 'executor', title: 'Executor', subtitle: 'runs each camera plan' },
  { id: 'engines', title: 'Engines', subtitle: 'objects, plates, text, faces' },
]

/**
 * Pure layout: workers (+ one Unassigned group) as containers sized by camera count, packed
 * left-to-right into wrapping rows; Kafka -> executor -> engines stacked on the right.
 */
export function layoutOps(input: {
  workers: OpsWorker[]
  cameras: CameraConfig[]
  maxRowWidth?: number
}): FlowLayout {
  const maxRow = input.maxRowWidth ?? ROW_MAX_W
  const enabled = new Set(input.cameras.filter((c) => c.enabled).map((c) => c.camera_id))
  const owner = new Set<string>()
  const groups: WorkerNodeData[] = [...input.workers]
    .sort((a, b) => byId(a.worker_id, b.worker_id))
    .map((w) => {
      const ids: string[] = []
      for (const a of assignedOf(w)) {
        if (!enabled.has(a.camera_id) || owner.has(a.camera_id)) continue
        owner.add(a.camera_id)
        ids.push(a.camera_id)
      }
      ids.sort(byId)
      return {
        kind: 'worker',
        worker_id: w.worker_id,
        state: w.state,
        managed_by: w.managed_by,
        capacity: w.capacity,
        capacity_override: w.capacity_override,
        load: w.load ?? ids.length,
        cameraIds: ids,
        cols: chipColumns(ids.length),
      }
    })
  const loose = [...enabled].filter((id) => !owner.has(id)).sort(byId)
  if (loose.length)
    groups.push({
      kind: 'worker',
      worker_id: null,
      state: null,
      managed_by: null,
      capacity: 0,
      capacity_override: null,
      load: loose.length,
      cameraIds: loose,
      cols: chipColumns(loose.length),
    })

  const nodes: OpsNode[] = []
  let x = 0
  let y = 0
  let rowH = 0
  let usedW = 0
  for (const g of groups) {
    const { w, h } = groupSize(g.cameraIds.length, g.cols)
    if (x > 0 && x + w > maxRow) {
      x = 0
      y += rowH + GROUP_GAP
      rowH = 0
    }
    nodes.push({
      id: g.worker_id ? `worker:${g.worker_id}` : 'unassigned',
      type: 'worker',
      position: { x, y },
      width: w,
      height: h,
      data: g,
      draggable: false,
      selectable: false,
      // RF turns pointer events off on non-interactive nodes; the chips inside are buttons
      style: { pointerEvents: 'all' },
    })
    x += w + GROUP_GAP
    rowH = Math.max(rowH, h)
    usedW = Math.max(usedW, x - GROUP_GAP)
  }
  const height = groups.length ? y + rowH : 0

  const stackH = INFO.length * INFO_H + (INFO.length - 1) * INFO_GAP
  const sx = usedW + STACK_GAP
  const sy = Math.max(0, Math.min((height - stackH) / 2, 160))
  INFO.forEach((d, i) =>
    nodes.push({
      id: d.id,
      type: 'info',
      position: { x: sx, y: sy + i * (INFO_H + INFO_GAP) },
      width: INFO_W,
      height: INFO_H,
      data: { kind: 'info', ...d },
      draggable: false,
      selectable: false,
    }),
  )

  const edges: Edge[] = []
  for (const g of groups) {
    if (!g.worker_id) continue
    edges.push({
      id: `e:${g.worker_id}`,
      source: `worker:${g.worker_id}`,
      target: 'kafka',
      animated: g.state === 'live' && g.cameraIds.length > 0,
      style: {
        stroke: g.state === 'dead' ? 'var(--err)' : g.state === 'draining' ? 'var(--warn)' : 'var(--border-strong)',
        strokeWidth: 1.5,
        strokeDasharray: g.state === 'draining' ? '5 4' : undefined,
      },
    })
  }
  const neutral = { stroke: 'var(--border-strong)', strokeWidth: 2 }
  edges.push(
    { id: 'e:kafka-exec', source: 'kafka', target: 'executor', style: neutral },
    { id: 'e:exec-eng', source: 'executor', target: 'engines', style: neutral },
  )
  return { nodes, edges, width: sx + INFO_W, height: Math.max(height, sy + stackH) }
}
