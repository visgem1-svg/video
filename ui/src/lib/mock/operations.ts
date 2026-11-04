import { api } from '../api/runtime'
import type {
  AutoscalerEvent,
  AutoscalerPolicy,
  AutoscalerState,
  ManagedBy,
  OpsWorker,
} from '../api/operations'
import { ApiError } from '../api/types'

interface MW {
  id: string
  managed: ManagedBy
  override: number | null
  draining: boolean
}

export const DEFAULT_POLICY: AutoscalerPolicy = {
  min_workers: 2,
  max_workers: 6,
  target_utilization: 0.7,
  worker_capacity: 10,
  cooldown_s: 60,
  drain_timeout_s: 120,
}
const MOVE_PER_TICK = 4

/** In-memory stand-in for the worker registry + worker-controller (mock mode only). */
export class MockOps {
  private ready: Promise<void> | null = null
  private workers: MW[] = []
  private assign = new Map<string, string>()
  private pins = new Map<string, string>()
  private gen = new Map<string, number>()
  private mode: 'auto' | 'manual' = 'auto'
  private policy: AutoscalerPolicy = { ...DEFAULT_POLICY }
  private manualTarget = 3
  private events: AutoscalerEvent[] = []
  private lastScaleAt = Date.now() - 180_000
  private autoN = 1

  private init() {
    this.ready ??= (async () => {
      const cams = (await api.listCameras()).filter((c) => c.enabled)
      this.workers = [
        { id: 'fx-1', managed: 'static', override: null, draining: false },
        { id: 'fx-2', managed: 'static', override: null, draining: false },
        { id: 'fx-auto-1', managed: 'controller', override: null, draining: false },
      ]
      cams.forEach((c, i) =>
        this.assign.set(c.camera_id, this.workers[Math.min(2, Math.floor(i / 7))].id),
      )
      this.events = [
        {
          ts: Date.now() - 180_000,
          action: 'scale_up',
          worker_id: 'fx-auto-1',
          reason: `enabled_cameras=${cams.length} exceeds 2 workers x 10 x 0.7`,
        },
      ]
    })()
    return this.ready
  }

  private capOf(w: MW) {
    return w.override ?? this.policy.worker_capacity
  }
  private load(id: string) {
    let n = 0
    for (const w of this.assign.values()) if (w === id) n++
    return n
  }
  private target(): MW[] {
    return this.workers.filter((w) => !w.draining)
  }
  private log(action: string, worker_id: string | null, reason: string) {
    this.events = [{ ts: Date.now(), action, worker_id, reason }, ...this.events].slice(0, 50)
  }
  private leastLoaded(excl?: string) {
    return this.target()
      .filter((w) => w.id !== excl)
      .sort((a, b) => this.load(a.id) / this.capOf(a) - this.load(b.id) / this.capOf(b))[0]
  }
  private rebalance() {
    const live = this.target()
    if (live.length < 2) return
    for (let guard = 0; guard < 100; guard++) {
      const sorted = [...live].sort((a, b) => this.load(b.id) - this.load(a.id))
      const hi = sorted[0]
      const lo = sorted[sorted.length - 1]
      if (this.load(hi.id) - this.load(lo.id) <= 1) return
      const cam = [...this.assign].find(([c, w]) => w === hi.id && !this.pins.has(c))
      if (!cam) return
      this.assign.set(cam[0], lo.id)
    }
  }
  private desired(): number {
    const p = this.policy
    if (this.mode === 'manual') return this.manualTarget
    const n = this.assign.size
    const raw = Math.ceil(n / (p.worker_capacity * p.target_utilization))
    return Math.max(p.min_workers, Math.min(p.max_workers, raw))
  }

  /** One controller step; runs on every poll. */
  private tick() {
    // draining workers hand cameras over a few at a time
    for (const w of this.workers.filter((x) => x.draining)) {
      let moved = 0
      for (const [cam, wid] of [...this.assign]) {
        if (wid !== w.id || moved >= MOVE_PER_TICK) continue
        const dst = this.leastLoaded(w.id)
        if (!dst) break
        this.assign.set(cam, dst.id)
        this.pins.delete(cam)
        moved++
      }
      if (w.managed === 'controller' && this.load(w.id) === 0) {
        this.workers = this.workers.filter((x) => x.id !== w.id)
        this.log('scale_down', w.id, 'drained, container removed')
      }
    }
    const cooled = Date.now() - this.lastScaleAt >= this.policy.cooldown_s * 1000
    const cur = this.target().length
    const want = this.desired()
    if (!cooled) return
    if (want > cur) {
      const id = `fx-auto-${++this.autoN}`
      this.workers.push({ id, managed: 'controller', override: null, draining: false })
      this.lastScaleAt = Date.now()
      this.rebalance()
      this.log('scale_up', id, `desired=${want} > current=${cur}`)
    } else if (want < cur) {
      const cand = this.target()
        .filter((w) => w.managed === 'controller')
        .sort((a, b) => this.load(a.id) - this.load(b.id))[0]
      if (cand) {
        cand.draining = true
        this.lastScaleAt = Date.now()
        this.log('drain', cand.id, `desired=${want} < current=${cur}`)
      }
    }
  }

  async listWorkers(): Promise<OpsWorker[]> {
    await this.init()
    this.tick()
    const now = Date.now()
    return this.workers.map((w) => ({
      worker_id: w.id,
      capacity: this.capOf(w),
      capacity_override: w.override,
      draining: w.draining,
      state: w.draining ? 'draining' : 'live',
      managed_by: w.managed,
      last_seen: now - 1200,
      assigned: [...this.assign]
        .filter(([, wid]) => wid === w.id)
        .map(([camera_id]) => ({
          camera_id,
          generation: this.gen.get(camera_id) ?? 0,
          pinned: this.pins.get(camera_id) === w.id,
        })),
    }))
  }

  async drain(id: string, on: boolean) {
    await this.init()
    const w = this.workers.find((x) => x.id === id)
    if (!w) throw new ApiError('404 unknown worker', 404)
    if (on && this.target().length <= 1)
      throw new ApiError('409 cannot drain the last live worker', 409)
    w.draining = on
    this.log(on ? 'drain' : 'undrain', id, 'operator request')
  }
  async setCapacity(id: string, capacity: number | null) {
    await this.init()
    const w = this.workers.find((x) => x.id === id)
    if (!w) throw new ApiError('404 unknown worker', 404)
    w.override = capacity
  }
  async pin(cameraId: string, workerId: string | null) {
    await this.init()
    if (!this.assign.has(cameraId)) throw new ApiError('404 unknown camera', 404)
    if (workerId == null) {
      this.pins.delete(cameraId)
      return
    }
    const w = this.workers.find((x) => x.id === workerId)
    if (!w || w.draining) throw new ApiError('409 worker is not live', 409)
    this.pins.set(cameraId, workerId)
    this.assign.set(cameraId, workerId)
  }
  async restart(cameraId: string) {
    await this.init()
    this.gen.set(cameraId, (this.gen.get(cameraId) ?? 0) + 1)
  }

  async autoscaler(): Promise<AutoscalerState> {
    await this.init()
    this.tick()
    return {
      mode: this.mode,
      policy: this.policy,
      current: this.target().length,
      desired: this.desired(),
      workers: this.workers.filter((w) => w.managed === 'controller').map((w) => w.id),
      events: this.events,
    }
  }
  async setPolicy(p: AutoscalerPolicy) {
    await this.init()
    if (p.min_workers > p.max_workers) throw new ApiError('422 min_workers > max_workers', 422)
    this.policy = { ...p }
    this.log(
      'policy',
      null,
      `min=${p.min_workers} max=${p.max_workers} target=${p.target_utilization}`,
    )
  }
  async setMode(mode: 'auto' | 'manual') {
    await this.init()
    this.mode = mode
    this.manualTarget = this.target().length
    this.log('mode', null, `mode set to ${mode}`)
  }
  async scale(workers: number) {
    await this.init()
    if (this.mode !== 'manual') throw new ApiError('409 autoscaler is in auto mode', 409)
    this.manualTarget = workers
    this.lastScaleAt = 0
    this.log('scale', null, `manual target ${workers}`)
  }
}
