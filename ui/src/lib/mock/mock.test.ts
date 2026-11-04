import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Hub } from '../realtime/hub'
import type { Detection } from '../types'
import { createMockBackend, MockTransport } from './mockBackend'
import { CAMERA_COUNT, FRAME_H, FRAME_W, MockWorld, UNSTABLE_CAMERAS, healthAt } from './world'

const T0 = 1_700_000_000_000

describe('MockWorld', () => {
  const w = new MockWorld({ seed: 1, now: () => T0 })
  it('has 20 cameras, 2 of them unstable', () => {
    expect(w.cameras).toHaveLength(CAMERA_COUNT)
    const states = new Set<string>()
    for (let t = 0; t < 120_000; t += 1000) {
      for (const id of UNSTABLE_CAMERAS)
        states.add(
          healthAt(
            w.cameras.find((c) => c.camera_id === id)!,
            T0 + t,
            'fx-1',
          ).state,
        )
    }
    expect(states).toContain('online')
    expect(states).toContain('degraded')
    expect(states).toContain('offline')
    const stable = new Set<string>()
    for (let t = 0; t < 120_000; t += 1000) stable.add(healthAt(w.cameras[0], T0 + t, 'fx-1').state)
    expect([...stable]).toEqual(['online'])
  })
  it('generates in-bounds detections that move over time', () => {
    const a = w.detection('cam-001', T0)!
    const b = w.detection('cam-001', T0 + 3000)!
    for (const o of a.objects) {
      expect(o.box[0]).toBeGreaterThanOrEqual(0)
      expect(o.box[2]).toBeLessThanOrEqual(FRAME_W)
      expect(o.box[3]).toBeLessThanOrEqual(FRAME_H)
    }
    expect(a.objects[0].box).not.toEqual(b.objects[0].box)
    expect(a.partial).toBe(false)
    expect(a.step_id).toBeNull()
  })
  it('splits queue-step output into partials for the same frame', () => {
    const d = w.detection('cam-001', T0)!
    const parts = w.splitPartials(d)
    expect(parts.length).toBeGreaterThan(0)
    parts.forEach((p) => {
      expect(p.partial).toBe(true)
      expect(p.frame_id).toBe(d.frame_id)
      expect(p.step_id).toMatch(/^s\d+$/)
    })
  })
  it('summarises health and serves the REST interface', { timeout: 20000 }, async () => {
    const s = w.summary()
    expect(s.total).toBe(20)
    expect(s.online + s.degraded + s.offline + s.unassigned).toBe(20)
    const api = createMockBackend(w)
    expect((await api.listCameras()).length).toBe(20)
    const draft = await api.draftPlan({ query: 'read plates of trucks', camera_ids: ['cam-001'] })
    expect(draft.valid).toBe(true)
    expect(draft.plan.steps.map((x) => x.tool)).toContain('read_plate')
    expect((await api.draftPlan({ query: 'invalid', camera_ids: [] })).valid).toBe(false)
    expect(
      (await api.listRecognitions({ type: 'plate', limit: 5 })).every((r) => r.type === 'plate'),
    ).toBe(true)
  })
})

describe('MockTransport via Hub', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => vi.useRealTimers())
  it('streams ~3 detections/s only for subscribed cameras and sends a connect snapshot', async () => {
    const world = new MockWorld({ seed: 2 })
    const hub = new Hub(10)
    hub.attach(new MockTransport(world))
    const got: Detection[] = []
    let health = 0
    let plans = 0
    hub.onDetections('cam-002', (d) => got.push(d))
    hub.on('cameraHealth', () => health++)
    hub.on('planChanged', () => plans++)
    const unsub = hub.subscribeCamera('cam-002')
    await hub.start()
    await vi.advanceTimersByTimeAsync(20)
    expect(health).toBe(20)
    expect(plans).toBe(2)
    got.length = 0
    await vi.advanceTimersByTimeAsync(3000)
    const main = got.filter((d) => !d.partial)
    expect(main.length).toBeGreaterThanOrEqual(8)
    expect(main.length).toBeLessThanOrEqual(10)
    unsub()
    await vi.advanceTimersByTimeAsync(500)
    got.length = 0
    await vi.advanceTimersByTimeAsync(1000)
    expect(got).toHaveLength(0)
    await hub.stop()
  })
})
