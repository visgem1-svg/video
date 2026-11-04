import { describe, expect, it } from 'vitest'
import { FRAME_NODE_ID, describeWhen, parentOf, planToFlow } from './dag'
import type { PipelineMetrics, Plan } from '@/lib/types'

const plan: Plan = {
  plan_id: 'p1',
  version: 1,
  name: 'T',
  query: 'q',
  camera_ids: ['cam-001', 'cam-002'],
  status: 'active',
  created_at: 0,
  steps: [
    { id: 's1', tool: 'detect_objects', mode: 'inline' },
    {
      id: 's2',
      tool: 'read_plate',
      mode: 'queue',
      when: { type: 'found', class: 'truck', min_confidence: 0.6 },
    },
    {
      id: 's3',
      tool: 'identify_faces',
      mode: 'inline',
      when: { type: 'count', class: 'person', gt: 2 },
    },
    { id: 's4', tool: 'detect_faces', mode: 'inline', when: { type: 'failed', step: 's3' } },
  ],
}

describe('planToFlow', () => {
  const { nodes, edges } = planToFlow(plan)
  it('creates a source node plus one node per step', () => {
    expect(nodes.map((n) => n.id)).toEqual([FRAME_NODE_ID, 's1', 's2', 's3', 's4'])
    expect(nodes.find((n) => n.id === 's2')?.type).toBe('step')
  })
  it('derives edges from `when` and labels the conditions', () => {
    const e = Object.fromEntries(edges.map((x) => [x.target, x]))
    expect(e.s1.source).toBe(FRAME_NODE_ID)
    expect(e.s1.label).toBeUndefined()
    expect(e.s2.source).toBe('s1')
    expect(e.s2.label).toBe('found truck ≥ 60%')
    expect(e.s3.source).toBe('s1')
    expect(e.s3.label).toBe('count(person) > 2')
    expect(e.s4.source).toBe('s3')
    expect(e.s4.label).toBe('s3 failed')
  })
  it('lays out by depth with siblings stacked', () => {
    const pos = Object.fromEntries(nodes.map((n) => [n.id, n.position]))
    expect(pos.s1.x).toBeGreaterThan(pos[FRAME_NODE_ID].x)
    expect(pos.s2.x).toBe(pos.s3.x)
    expect(pos.s3.y).toBeGreaterThan(pos.s2.y)
    expect(pos.s4.x).toBeGreaterThan(pos.s3.x)
  })
  it('attaches metrics to node data', () => {
    const m: PipelineMetrics = {
      plan_id: 'p1',
      step_id: 's2',
      tool: 'read_plate',
      window_s: 5,
      processed: 10,
      skipped: 1,
      errors: 0,
      p50_ms: 1,
      p95_ms: 2,
      executor_id: 'e',
      ts: 1,
    }
    const f = planToFlow(plan, { s2: m })
    expect((f.nodes.find((n) => n.id === 's2')!.data as { metrics?: unknown }).metrics).toEqual(m)
  })
  it('helpers', () => {
    expect(describeWhen(undefined)).toBe('always')
    expect(parentOf(plan, 0)).toBe(FRAME_NODE_ID)
  })
})
