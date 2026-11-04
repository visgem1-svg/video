import { ApiError, type RestApi } from '../api/types'
import type { ConnState, HubEvent, HubMethod, HubTransport } from '../realtime/types'
import { MOCK_TOOLS, MockWorld, recognitionFromDetection } from './world'

const delay = <T>(v: T, ms = 120, signal?: AbortSignal): Promise<T> =>
  new Promise((resolve, reject) => {
    const t = setTimeout(() => resolve(v), ms)
    signal?.addEventListener('abort', () => {
      clearTimeout(t)
      reject(new DOMException('aborted', 'AbortError'))
    })
  })
const clone = <T>(v: T): T => structuredClone(v)

export function createMockBackend(world: MockWorld): RestApi {
  return {
    listCameras: () => delay(clone(world.cameras)),
    createCamera: async (c) => {
      if (world.cameras.some((x) => x.camera_id === c.camera_id))
        throw new ApiError('409 camera already exists', 409)
      return delay(clone(world.upsertCamera(c)))
    },
    updateCamera: async (id, c) => {
      if (!world.cameras.some((x) => x.camera_id === id)) throw new ApiError('404 not found', 404)
      return delay(clone(world.upsertCamera({ ...c, camera_id: id }, id)))
    },
    deleteCamera: async (id) => {
      world.removeCamera(id)
      await delay(undefined)
    },
    listWorkers: () => delay(clone(world.workers())),
    healthCameras: () => delay(world.health()),
    healthSummary: () => delay(world.summary()),
    listRecognitions: (q) => delay(clone(world.listRecognitions(q))),
    draftPlan: (req, signal, opts) => {
      const res = world.draft(req, opts?.refresh)
      return delay(res, res.cached ? 250 : 3000, signal)
    },
    recentDrafts: (limit = 10) => delay(world.recentQueries.slice(0, limit)),
    listPersons: () => delay(clone(world.persons)),
    enrollPerson: async (name, image) => delay(clone(world.enrollPerson(name, image.name)), 500),
    addPersonPhoto: async (id, image) => {
      const p = world.persons.find((x) => x.person_id === id)
      if (!p) throw new ApiError('404 person not found', 404)
      world.checkFaces(image.name)
      p.photos++
      return delay(clone(p), 400)
    },
    renamePerson: async (id, name) => {
      const p = world.persons.find((x) => x.person_id === id)
      if (!p) throw new ApiError('404 person not found', 404)
      p.name = name
      return delay(clone(p))
    },
    deletePerson: async (id) => {
      world.persons = world.persons.filter((x) => x.person_id !== id)
      await delay(undefined)
    },
    activatePlan: async (plan, signal) => {
      await delay(null, 300, signal)
      return clone(world.activate(plan))
    },
    listPlans: () => delay(clone(world.plans)),
    getPlan: async (id) => {
      const p = world.plans.find((x) => x.plan_id === id)
      if (!p) throw new ApiError('404 plan not found', 404)
      return clone(p)
    },
    deletePlan: async (id) => {
      world.removePlan(id)
      await delay(undefined)
    },
    listTools: () => delay(clone(MOCK_TOOLS)),
  }
}

/** Fake SignalR hub: detections at 3/s for subscribed cameras, health, recognitions, metrics. */
export class MockTransport implements HubTransport {
  private onEv: (e: HubEvent) => void = () => {}
  private onSt: (s: ConnState) => void = () => {}
  private cams = new Set<string>()
  private plans = new Set<string>()
  private frames = new Set<string>()
  private timers: ReturnType<typeof setInterval>[] = []

  constructor(private readonly world: MockWorld) {}

  onEvent(h: (e: HubEvent) => void) {
    this.onEv = h
  }
  onState(h: (s: ConnState) => void) {
    this.onSt = h
  }

  async start() {
    this.onSt('connecting')
    const w = this.world
    this.world.onPlanChanged = (p) => this.onEv({ type: 'planChanged', data: p })
    this.timers.push(
      // 3 detections/s per subscribed camera
      setInterval(() => {
        const t = w.now()
        for (const id of this.cams) {
          const d = w.detection(id, t)
          if (!d) continue
          const partials = w.splitPartials(d)
          this.onEv({ type: 'detections', data: d })
          // queue-mode steps answer later with a partial for the same frame
          partials.forEach((p) => setTimeout(() => this.onEv({ type: 'detections', data: p }), 250))
        }
      }, 333),
      setInterval(
        () => w.health().forEach((h) => this.onEv({ type: 'cameraHealth', data: h })),
        2000,
      ),
      // plate/face recognitions: a few per 10 s from any enabled camera
      setInterval(() => {
        const t = w.now()
        const live = w.cameras.filter((c) => c.enabled)
        const cam = live[Math.floor(w.rng() * live.length)]
        const d = cam && w.detection(cam.camera_id, t)
        const rec = d && recognitionFromDetection(d, w.rng)
        if (rec && (d.plan_id !== 'default' || w.rng() < 0.3)) {
          w.addRecognition(rec)
          this.onEv({ type: 'recognition', data: rec })
        }
      }, 1800),
      setInterval(() => {
        const t = w.now()
        this.plans.forEach((id) =>
          w.metrics(id, t).forEach((m) => this.onEv({ type: 'pipelineMetrics', data: m })),
        )
      }, 2000),
    )
    this.onSt('connected')
    // 9.1: snapshot on connect - health for every camera and planChanged for every plan
    w.health().forEach((h) => this.onEv({ type: 'cameraHealth', data: h }))
    w.plans.forEach((p) => this.onEv({ type: 'planChanged', data: p }))
  }

  async stop() {
    this.timers.forEach(clearInterval)
    this.timers = []
    this.onSt('disconnected')
  }

  async invoke(method: HubMethod, ids: string[]) {
    if (method === 'SubscribeCameras') ids.forEach((i) => this.cams.add(i))
    else if (method === 'UnsubscribeCameras') ids.forEach((i) => this.cams.delete(i))
    else if (method === 'WatchFrames') ids.forEach((i) => this.frames.add(i))
    else if (method === 'UnwatchFrames') ids.forEach((i) => this.frames.delete(i))
    else {
      ids.forEach((i) => this.plans.add(i))
      // immediate first sample so the watch view isn't empty for 2 s
      const t = this.world.now()
      ids.forEach((id) =>
        this.world.metrics(id, t).forEach((m) => this.onEv({ type: 'pipelineMetrics', data: m })),
      )
    }
  }

  get watchedFrames() {
    return this.frames
  }

  get subscribedCameras() {
    return this.cams
  }
}
