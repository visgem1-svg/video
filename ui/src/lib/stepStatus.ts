import { evalWhen } from './plan'
import type { Detection, Plan, PlanStep } from './types'

export type StepResult = 'ran' | 'skipped' | 'failed' | 'pending' | 'none'

export interface StepLive {
  /** outcome on the most recent frame */
  result: StepResult
  /** duration reported in timings_ms on the last run */
  lastMs: number | null
  /** short description of the last output ("car×2, person", plate, "Lena 0.71", text) */
  summary: string | null
  /** true if the step ran on the latest frame */
  ranOnLatest: boolean
  /** counts over the last 60 s */
  ran60: number
  skipped60: number
}

export interface StepRuntime {
  result: StepResult
  lastMs: number | null
  summary: string | null
  events: Array<{ at: number; kind: 'ran' | 'skipped' }>
}

export interface CameraStepState {
  planId: string
  steps: Record<string, StepRuntime>
  frame: {
    frame_id: string
    ts: number
    arrivedAt: number
    total_ms: number
    ran: string[]
  } | null
}

export const WINDOW_MS = 60_000

export const emptyState = (plan: Plan): CameraStepState => ({
  planId: plan.plan_id,
  steps: Object.fromEntries(
    plan.steps.map((s) => [s.id, { result: 'none', lastMs: null, summary: null, events: [] }]),
  ),
  frame: null,
})

function summarise(step: PlanStep, d: Detection): string {
  switch (step.tool) {
    case 'detect_objects': {
      if (!d.objects.length) return 'nothing found'
      const counts = new Map<string, number>()
      for (const o of d.objects) counts.set(o.class, (counts.get(o.class) ?? 0) + 1)
      return [...counts].map(([c, n]) => (n > 1 ? `${c}×${n}` : c)).join(', ')
    }
    case 'read_plate':
      return d.plates.length ? d.plates.map((p) => p.plate).join(', ') : 'no plate'
    case 'read_text':
      return d.texts.length ? d.texts.map((t) => t.text).join(', ') : 'no text'
    case 'detect_faces':
      return d.faces.length
        ? `${d.faces.length} face${d.faces.length > 1 ? 's' : ''} detected`
        : 'no face'
    case 'identify_faces':
      return d.faces.length
        ? d.faces
            .map((f) =>
              f.person_name ? `${f.person_name} ${(f.score ?? 0).toFixed(2)}` : 'unknown',
            )
            .join(', ')
        : 'no face'
  }
}

const prune = (ev: StepRuntime['events'], now: number) => ev.filter((e) => now - e.at <= WINDOW_MS)

/**
 * Pure reducer: folds one `detections` message (main or partial, contract 9.1) of a camera
 * into per-step status. A step "ran" on a frame iff its id is in timings_ms. A step that did
 * not run is "skipped" when its `when` is false, "pending" when its condition holds but a
 * queue-mode result has not arrived yet. A step is "failed" when a later `failed {step}`
 * step ran for the same frame.
 */
export function reduceStepStatus(
  state: CameraStepState,
  d: Detection,
  plan: Plan,
  now: number,
): CameraStepState {
  const base = state.planId === plan.plan_id ? state : emptyState(plan)
  const steps: Record<string, StepRuntime> = { ...base.steps }

  if (d.partial) {
    const id = d.step_id
    const step = plan.steps.find((s) => s.id === id)
    if (!id || !step) return base
    const prev = steps[id] ?? { result: 'none', lastMs: null, summary: null, events: [] }
    steps[id] = {
      result: base.frame?.frame_id === d.frame_id ? 'ran' : prev.result,
      lastMs: d.timings_ms[id] ?? prev.lastMs,
      summary: summarise(step, d),
      events: [...prune(prev.events, now), { at: now, kind: 'ran' as const }],
    }
    const frame =
      base.frame && base.frame.frame_id === d.frame_id
        ? { ...base.frame, ran: [...new Set([...base.frame.ran, id])] }
        : base.frame
    return { planId: plan.plan_id, steps, frame }
  }

  const ran = new Set(Object.keys(d.timings_ms))
  const failed = new Set<string>()
  for (const s of plan.steps)
    if (s.when?.type === 'failed' && ran.has(s.id)) failed.add(s.when.step)

  for (const step of plan.steps) {
    const prev = steps[step.id] ?? { result: 'none', lastMs: null, summary: null, events: [] }
    const events = prune(prev.events, now)
    if (failed.has(step.id)) {
      steps[step.id] = { ...prev, result: 'failed', events }
    } else if (ran.has(step.id)) {
      steps[step.id] = {
        result: 'ran',
        lastMs: d.timings_ms[step.id],
        summary: summarise(step, d),
        events: [...events, { at: now, kind: 'ran' }],
      }
    } else if (evalWhen(step.when, { objects: d.objects, failed })) {
      // should have run: a queue step answers later with a partial
      steps[step.id] = { ...prev, result: step.mode === 'queue' ? 'pending' : 'failed', events }
    } else {
      steps[step.id] = {
        ...prev,
        result: 'skipped',
        events: [...events, { at: now, kind: 'skipped' }],
      }
    }
  }
  return {
    planId: plan.plan_id,
    steps,
    frame: { frame_id: d.frame_id, ts: d.ts, arrivedAt: now, total_ms: d.total_ms, ran: [...ran] },
  }
}

/** Read-model for the graph: per-step live info as of `now`. */
export function selectStepLive(state: CameraStepState, now: number): Record<string, StepLive> {
  const out: Record<string, StepLive> = {}
  for (const [id, s] of Object.entries(state.steps)) {
    const ev = s.events.filter((e) => now - e.at <= WINDOW_MS)
    out[id] = {
      result: s.result,
      lastMs: s.lastMs,
      summary: s.summary,
      ranOnLatest: s.result === 'ran' && (state.frame?.ran.includes(id) ?? false),
      ran60: ev.filter((e) => e.kind === 'ran').length,
      skipped60: ev.filter((e) => e.kind === 'skipped').length,
    }
  }
  return out
}
