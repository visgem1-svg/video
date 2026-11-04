import { describe, expect, it } from 'vitest'
import { evalWhen } from './plan'

const DEFAULT_PLAN: Plan = {
  plan_id: 'default',
  version: 1,
  name: 'Default plan',
  query: 'detect objects',
  camera_ids: [],
  status: 'active',
  created_at: 0,
  steps: [{ id: 's1', tool: 'detect_objects', mode: 'inline' }],
}
import { emptyState, reduceStepStatus, selectStepLive } from './stepStatus'
import type { Detection, Plan } from './types'

const plan: Plan = {
  plan_id: 'p',
  version: 1,
  name: 'n',
  query: 'q',
  camera_ids: ['cam-001'],
  status: 'active',
  created_at: 0,
  steps: [
    { id: 's1', tool: 'detect_objects', mode: 'inline' },
    {
      id: 's2',
      tool: 'read_plate',
      mode: 'queue',
      when: { type: 'found', class: 'car,truck', min_confidence: 0.6 },
    },
    { id: 's3', tool: 'identify_faces', mode: 'inline', when: { type: 'found', class: 'person' } },
    { id: 's4', tool: 'detect_faces', mode: 'inline', when: { type: 'failed', step: 's3' } },
  ],
}

const det = (over: Partial<Detection>): Detection => ({
  frame_id: 'f1',
  camera_id: 'cam-001',
  ts: 1000,
  width: 960,
  height: 540,
  plan_id: 'p',
  plan_version: 1,
  objects: [],
  plates: [],
  faces: [],
  texts: [],
  timings_ms: {},
  total_ms: 0,
  executor_id: 'ex',
  partial: false,
  step_id: null,
  ...over,
})
const obj = (c: string, confidence = 0.9) => ({
  class: c,
  confidence,
  box: [0, 0, 1, 1] as [number, number, number, number],
})

describe('evalWhen', () => {
  it('handles any-of lists, confidence and count', () => {
    const objects = [obj('truck', 0.7), obj('person', 0.4)]
    expect(evalWhen({ type: 'found', class: 'car,truck' }, { objects })).toBe(true)
    expect(evalWhen({ type: 'found', class: 'car,truck', min_confidence: 0.8 }, { objects })).toBe(
      false,
    )
    expect(evalWhen({ type: 'count', class: 'person,truck', gt: 1 }, { objects })).toBe(true)
    expect(evalWhen({ type: 'failed', step: 's1' }, { objects, failed: new Set(['s1']) })).toBe(
      true,
    )
    expect(evalWhen(undefined, { objects: [] })).toBe(true)
  })
})

describe('reduceStepStatus', () => {
  it('marks ran / skipped by condition and summarises output', () => {
    const d = det({
      objects: [obj('person'), obj('person'), obj('bicycle')],
      faces: [
        { box: [0, 0, 1, 1], confidence: 0.9, person_id: 'p1', person_name: 'Lena', score: 0.71 },
      ],
      timings_ms: { s1: 38.2, s3: 48.9 },
      total_ms: 87,
    })
    const st = reduceStepStatus(emptyState(plan), d, plan, 10_000)
    const live = selectStepLive(st, 10_000)
    expect(live.s1).toMatchObject({
      result: 'ran',
      lastMs: 38.2,
      summary: 'person×2, bicycle',
      ranOnLatest: true,
      ran60: 1,
    })
    expect(live.s2).toMatchObject({ result: 'skipped', skipped60: 1, ranOnLatest: false })
    expect(live.s3).toMatchObject({ result: 'ran', summary: 'Lena 0.71' })
    expect(live.s4.result).toBe('skipped')
    expect(st.frame).toMatchObject({ total_ms: 87, frame_id: 'f1' })
  })

  it('queue step is pending until its partial arrives, then ran with plate summary', () => {
    const main = det({ objects: [obj('truck')], timings_ms: { s1: 30 } })
    let st = reduceStepStatus(emptyState(plan), main, plan, 1000)
    expect(selectStepLive(st, 1000).s2.result).toBe('pending')
    const part = det({
      partial: true,
      step_id: 's2',
      timings_ms: { s2: 55 },
      plates: [{ plate: 'PL8REC', confidence: 0.95, box: [0, 0, 1, 1] }],
    })
    st = reduceStepStatus(st, part, plan, 1400)
    expect(selectStepLive(st, 1400).s2).toMatchObject({
      result: 'ran',
      lastMs: 55,
      summary: 'PL8REC',
      ranOnLatest: true,
      ran60: 1,
    })
  })

  it('a step is failed when its failed-handler ran', () => {
    const d = det({ objects: [obj('person')], timings_ms: { s1: 30, s4: 20 } })
    const live = selectStepLive(reduceStepStatus(emptyState(plan), d, plan, 1), 1)
    expect(live.s3.result).toBe('failed')
    expect(live.s4.result).toBe('ran')
  })

  it('counts only events from the last 60 s', () => {
    let st = emptyState(plan)
    st = reduceStepStatus(st, det({ frame_id: 'a', timings_ms: { s1: 1 } }), plan, 0)
    st = reduceStepStatus(st, det({ frame_id: 'b', timings_ms: { s1: 1 } }), plan, 50_000)
    expect(selectStepLive(st, 50_000).s1.ran60).toBe(2)
    expect(selectStepLive(st, 70_000).s1.ran60).toBe(1)
  })

  it('falls back to the default plan (detect_objects only)', () => {
    const d = det({ plan_id: 'default', objects: [obj('car')], timings_ms: { s1: 12 } })
    const live = selectStepLive(reduceStepStatus(emptyState(DEFAULT_PLAN), d, DEFAULT_PLAN, 5), 5)
    expect(Object.keys(live)).toEqual(['s1'])
    expect(live.s1.summary).toBe('car')
  })

  it('resets when the plan changes', () => {
    const st = reduceStepStatus(emptyState(plan), det({ timings_ms: { s1: 1 } }), plan, 1)
    const st2 = reduceStepStatus(
      st,
      det({ plan_id: 'default', timings_ms: { s1: 2 } }),
      DEFAULT_PLAN,
      2,
    )
    expect(st2.planId).toBe('default')
    expect(Object.keys(st2.steps)).toEqual(['s1'])
  })
})
