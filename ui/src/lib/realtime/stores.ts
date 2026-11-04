import { useSyncExternalStore } from 'react'
import { throttle } from '../throttle'
import type { CameraHealth, PipelineMetrics, Recognition } from '../types'
import { hub } from './hub'
import { normalizeHealth } from '../mode'

/** Tiny external store whose notifications are coalesced to at most one per `intervalMs`. */
export function createCoalescedStore<S>(initial: S, intervalMs: number) {
  let state = initial
  const listeners = new Set<() => void>()
  const notify = throttle(() => listeners.forEach((l) => l()), intervalMs)
  return {
    get: () => state,
    /** Mutate immutably; subscribers are woken at most every intervalMs. */
    update(fn: (s: S) => S) {
      const next = fn(state)
      if (next === state) return
      state = next
      notify()
    },
    /** Replace now and notify immediately (initial loads). */
    set(next: S) {
      state = next
      notify.cancel()
      listeners.forEach((l) => l())
    },
    subscribe(cb: () => void) {
      listeners.add(cb)
      return () => listeners.delete(cb)
    },
  }
}

// ---------- camera health: 400 cameras push at 0.2 Hz each; the UI redraws at <= 2 Hz ----------
export type HealthMap = ReadonlyMap<string, CameraHealth>
export const healthStore = createCoalescedStore<HealthMap>(new Map(), 500)

// Buffer updates between flushes so a burst costs one Map copy, not one per message.
let healthBuf = new Map<string, CameraHealth>()
let healthTimer: ReturnType<typeof setTimeout> | null = null
export function applyHealth(h: CameraHealth) {
  healthBuf.set(h.camera_id, normalizeHealth(h))
  healthTimer ??= setTimeout(flushHealth, 250)
}
export function flushHealth() {
  healthTimer = null
  if (healthBuf.size === 0) return
  const buf = healthBuf
  healthBuf = new Map()
  healthStore.update((prev) => {
    const next = new Map(prev)
    buf.forEach((v, k) => next.set(k, v))
    return next
  })
}
export function seedHealth(list: CameraHealth[]) {
  healthStore.update((prev) => {
    const next = new Map(prev)
    // never overwrite a newer live message with older REST data
    for (const raw of list) {
      const h = normalizeHealth(raw)
      const cur = next.get(h.camera_id)
      if (!cur || cur.ts <= h.ts) next.set(h.camera_id, h)
    }
    return next
  })
}

export const useHealthMap = (): HealthMap =>
  useSyncExternalStore(healthStore.subscribe, healthStore.get)
export const useCameraHealth = (id: string): CameraHealth | undefined =>
  useSyncExternalStore(healthStore.subscribe, () => healthStore.get().get(id))

// ---------- live recognitions ----------
export const FEED_MAX = 100
export const recognitionFeed = createCoalescedStore<Recognition[]>([], 250)
let recBuf: Recognition[] = []
let recTimer: ReturnType<typeof setTimeout> | null = null
export function pushRecognition(r: Recognition) {
  recBuf.push(r)
  recTimer ??= setTimeout(() => {
    recTimer = null
    const batch = recBuf
    recBuf = []
    recognitionFeed.update((prev) => {
      const seen = new Set(prev.map((p) => p.recognition_id))
      const fresh = batch.filter((b) => !seen.has(b.recognition_id)).reverse()
      return [...fresh, ...prev].slice(0, FEED_MAX)
    })
  }, 250)
}
export const useRecognitionFeed = () =>
  useSyncExternalStore(recognitionFeed.subscribe, recognitionFeed.get)

// ---------- pipeline metrics history (per plan, per step) ----------
export const METRIC_POINTS = 60
export type MetricsState = ReadonlyMap<string, PipelineMetrics[]> // key = plan_id/step_id
export const metricsStore = createCoalescedStore<MetricsState>(new Map(), 500)
export const metricKey = (planId: string, stepId: string) => `${planId}/${stepId}`
export function pushMetrics(m: PipelineMetrics) {
  metricsStore.update((prev) => {
    const next = new Map(prev)
    const k = metricKey(m.plan_id, m.step_id)
    const arr = [...(next.get(k) ?? []), m].slice(-METRIC_POINTS)
    next.set(k, arr)
    return next
  })
}
export const useMetrics = () => useSyncExternalStore(metricsStore.subscribe, metricsStore.get)

/** Route hub events into the stores. Call once at startup; returns unwire fn. */
export function wireStores(): () => void {
  const offs = [
    hub.on('cameraHealth', applyHealth),
    hub.on('recognition', pushRecognition),
    hub.on('pipelineMetrics', pushMetrics),
  ]
  return () => offs.forEach((o) => o())
}

/** Drop all cached realtime data (e.g. when leaving mock mode for the real backend). */
export function resetStores() {
  healthBuf = new Map()
  healthStore.set(new Map())
  recognitionFeed.set([])
  metricsStore.set(new Map())
}
