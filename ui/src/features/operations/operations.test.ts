import { describe, expect, it } from 'vitest'
import { patchWorkers, type OpsWorker } from '@/lib/api/operations'
import type { CameraConfig } from '@/lib/types'
import { layoutKey, layoutOps, worstState, type WorkerNodeData } from './flowGraph'
import { maskStreamUrl, sourceInfo } from './mask'
import { desiredWorkers, draftToPolicy, policyToDraft, validatePolicy } from './policy'

const cam = (i: number, extra: Partial<CameraConfig> = {}): CameraConfig => ({
  camera_id: `cam-${String(i).padStart(3, '0')}`,
  name: `Camera ${i}`,
  stream_url: 'rtsp://user:secret@10.0.0.1/stream',
  analysis_fps: 3,
  enabled: true,
  plan_id: null,
  tags: [],
  ...extra,
})
const worker = (id: string, cams: string[], extra: Partial<OpsWorker> = {}): OpsWorker => ({
  worker_id: id,
  capacity: 10,
  capacity_override: null,
  draining: false,
  state: 'live',
  managed_by: 'static',
  last_seen: 0,
  assigned: cams,
  ...extra,
})
describe('layoutOps', () => {
  const ids = (n: number, from = 1) => Array.from({ length: n }, (_, i) => cam(from + i))
  const groupsOf = (g: ReturnType<typeof layoutOps>) => g.nodes.filter((n) => n.type === 'worker')
  const overlap = (a: { position: { x: number; y: number }; width?: number; height?: number }, b: typeof a) =>
    a.position.x < b.position.x + b.width! &&
    b.position.x < a.position.x + a.width! &&
    a.position.y < b.position.y + b.height! &&
    b.position.y < a.position.y + a.height!

  it('lays out 0 cameras: one empty worker group plus the pipeline stack', () => {
    const g = layoutOps({ workers: [worker('fx-1', [])], cameras: [] })
    expect(groupsOf(g)).toHaveLength(1)
    expect((groupsOf(g)[0].data as WorkerNodeData).cameraIds).toEqual([])
    for (const id of ['kafka', 'executor', 'engines']) expect(g.nodes.some((n) => n.id === id)).toBe(true)
    expect(g.edges.filter((e) => e.target === 'kafka')).toHaveLength(1)
  })

  it('lays out nothing but the stack without workers', () => {
    const g = layoutOps({ workers: [], cameras: [] })
    expect(groupsOf(g)).toHaveLength(0)
    expect(g.nodes).toHaveLength(3)
  })

  it('1 camera: 2 columns, camera inside its worker, none unassigned', () => {
    const g = layoutOps({ workers: [worker('fx-1', ['cam-001'])], cameras: ids(1) })
    expect(groupsOf(g)).toHaveLength(1)
    expect(groupsOf(g)[0].data).toMatchObject({ worker_id: 'fx-1', cameraIds: ['cam-001'], cols: 2 })
  })

  it('31 cameras over 3 workers: sized by camera count, no overlap, kafka to the right', () => {
    const cams = ids(31)
    const ws = [
      worker('fx-1', cams.slice(0, 12).map((c) => c.camera_id), { capacity: 12 }),
      worker('fx-2', cams.slice(12, 24).map((c) => c.camera_id), { capacity: 12 }),
      worker('fx-10', cams.slice(24, 31).map((c) => c.camera_id), { capacity: 12 }),
    ]
    const g = layoutOps({ workers: ws, cameras: cams })
    const gs = groupsOf(g)
    expect(gs.map((n) => n.id)).toEqual(['worker:fx-1', 'worker:fx-2', 'worker:fx-10']) // natural order
    expect(gs.every((n) => (n.data as WorkerNodeData).cols === 2)).toBe(true)
    expect(gs[0].height!).toBeGreaterThan(gs[2].height!) // 12 cameras taller than 7
    for (let i = 0; i < gs.length; i++) for (let j = i + 1; j < gs.length; j++) expect(overlap(gs[i], gs[j])).toBe(false)
    const right = Math.max(...gs.map((n) => n.position.x + n.width!))
    expect(g.nodes.find((n) => n.id === 'kafka')!.position.x).toBeGreaterThan(right)
    expect(g.width).toBeLessThan(1600) // fits one screen row at a legible zoom
  })

  it('100 cameras: 3 columns once crowded, wraps into rows, every camera appears once', () => {
    const cams = ids(100)
    const ws = Array.from({ length: 5 }, (_, w) =>
      worker(`fx-${w + 1}`, cams.slice(w * 20, w * 20 + 20).map((c) => c.camera_id), { capacity: 20 }),
    )
    const g = layoutOps({ workers: ws, cameras: cams })
    const gs = groupsOf(g)
    expect(gs.every((n) => (n.data as WorkerNodeData).cols === 3)).toBe(true)
    expect(new Set(gs.map((n) => n.position.y)).size).toBeGreaterThan(1) // wrapped
    for (const n of gs) expect(n.position.x + n.width!).toBeLessThanOrEqual(1240)
    for (let i = 0; i < gs.length; i++) for (let j = i + 1; j < gs.length; j++) expect(overlap(gs[i], gs[j])).toBe(false)
    const all = gs.flatMap((n) => (n.data as WorkerNodeData).cameraIds)
    expect(all).toHaveLength(100)
    expect(new Set(all).size).toBe(100)
  })

  it('puts unowned and disabled-aware cameras into the Unassigned group (no edge)', () => {
    const cams = [...ids(3), cam(4, { enabled: false })]
    const g = layoutOps({ workers: [worker('fx-1', ['cam-001'])], cameras: cams })
    const un = g.nodes.find((n) => n.id === 'unassigned')!
    expect((un.data as WorkerNodeData).cameraIds).toEqual(['cam-002', 'cam-003']) // cam-004 disabled
    expect((un.data as WorkerNodeData).worker_id).toBeNull()
    expect(g.edges.filter((e) => e.target === 'kafka')).toHaveLength(1)
    // a camera listed by two workers is shown once
    const dup = layoutOps({ workers: [worker('fx-1', ['cam-001']), worker('fx-2', ['cam-001'])], cameras: ids(1) })
    expect(groupsOf(dup).flatMap((n) => (n.data as WorkerNodeData).cameraIds)).toEqual(['cam-001'])
  })

  it('styles worker edges by worker state', () => {
    const g = layoutOps({
      workers: [worker('fx-1', ['cam-001']), worker('fx-2', [], { state: 'draining' }), worker('fx-3', [], { state: 'dead' })],
      cameras: ids(1),
    })
    const e = (id: string) => g.edges.find((x) => x.id === id)!
    expect(e('e:fx-1').animated).toBe(true)
    expect(e('e:fx-2').style?.stroke).toBe('var(--warn)')
    expect(e('e:fx-3').style?.stroke).toBe('var(--err)')
  })

  it('layoutKey ignores health but reacts to assignment changes', () => {
    const cams = ids(3)
    const a = layoutKey([worker('fx-1', ['cam-001', 'cam-002'])], cams)
    expect(layoutKey([worker('fx-1', ['cam-001', 'cam-002'], { last_seen: 999 })], [...cams])).toBe(a)
    expect(layoutKey([worker('fx-1', ['cam-001'])], cams)).not.toBe(a)
    expect(layoutKey([worker('fx-1', ['cam-001', 'cam-002'], { state: 'draining' })], cams)).not.toBe(a)
  })

  it('ranks the worst state', () => {
    expect(worstState(['online', 'degraded', 'offline'])).toBe('offline')
    expect(worstState([undefined])).toBeUndefined()
  })
})

describe('sourceInfo', () => {
  it('tells publishers from stream hosts without credentials', () => {
    expect(sourceInfo('publisher')).toEqual({ kind: 'publisher', label: 'publisher' })
    expect(sourceInfo(undefined).kind).toBe('publisher')
    const s = sourceInfo('rtsp://admin:hunter2@cam.local:554/ch1')
    expect(s).toEqual({ kind: 'stream', label: 'cam.local:554' })
  })
})

describe('maskStreamUrl', () => {
  it('hides credentials and query secrets', () => {
    expect(maskStreamUrl('publisher')).toBe('sim publisher')
    const m = maskStreamUrl('rtsp://admin:hunter2@cam.local:554/ch1?token=abc')
    expect(m).not.toContain('hunter2')
    expect(m).not.toContain('abc')
    expect(m).toContain('cam.local')
  })
})

describe('validatePolicy', () => {
  const ok = { min_workers: 2, max_workers: 6, target_utilization: 0.7, worker_capacity: 10, cooldown_s: 60, drain_timeout_s: 120 }
  it('accepts a sane policy', () => expect(validatePolicy(ok)).toEqual({}))
  it('rejects bad values', () => {
    expect(validatePolicy({ ...ok, min_workers: -1 }).min_workers).toBeTruthy()
    expect(validatePolicy({ ...ok, max_workers: 1 }).max_workers).toMatch(/at least/)
    expect(validatePolicy({ ...ok, target_utilization: 1.2 }).target_utilization).toBeTruthy()
    expect(validatePolicy({ ...ok, worker_capacity: 2.5 }).worker_capacity).toBeTruthy()
    expect(validatePolicy({ ...ok, cooldown_s: -1 }).cooldown_s).toBeTruthy()
    expect(validatePolicy({ ...ok, worker_capacity: NaN }).worker_capacity).toBe('Required')
  })
  it('maps the real API policy to the form and back (percent <-> 0..1)', () => {
    const api = { worker_capacity: 12, target_utilization: 0.8, min_workers: 2, max_workers: 10, cooldown_s: 60, drain_timeout_s: 120 }
    const d = policyToDraft(api)
    expect(d.worker_capacity).toBe('12')
    expect(d.target_utilization).toBe('80')
    expect(Object.values(d)).not.toContain('undefined')
    expect(draftToPolicy(d)).toEqual(api)
    expect(Object.keys(draftToPolicy(d)).sort()).toEqual(Object.keys(api).sort())
    expect(validatePolicy(draftToPolicy({ ...d, worker_capacity: '' })).worker_capacity).toBe('Required')
  })
  it('computes desired workers like the controller', () => {
    expect(desiredWorkers(20, ok)).toBe(3)
    expect(desiredWorkers(1, ok)).toBe(2)
    expect(desiredWorkers(500, ok)).toBe(6)
  })
})

describe('patchWorkers (optimistic updates)', () => {
  const ws = [worker('fx-1', ['cam-001']), worker('fx-2', [])]
  it('drains and undrains', () => {
    const d = patchWorkers(ws, { type: 'drain', id: 'fx-1', on: true })
    expect(d[0]).toMatchObject({ draining: true, state: 'draining' })
    expect(patchWorkers(d, { type: 'drain', id: 'fx-1', on: false })[0].state).toBe('live')
  })
  it('moves a camera when pinned', () => {
    const p = patchWorkers(ws, { type: 'pin', cameraId: 'cam-001', workerId: 'fx-2' })
    expect(p[0].assigned).toHaveLength(0)
    expect(p[1].assigned).toEqual([{ camera_id: 'cam-001', pinned: true }])
  })
})
