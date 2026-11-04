import type {
  Box,
  CameraConfig,
  CameraHealth,
  CameraInput,
  Detection,
  DetFace,
  DetObject,
  DetPlate,
  DetText,
  DraftRequest,
  DraftResponse,
  HealthState,
  HealthSummary,
  PipelineMetrics,
  Person,
  Plan,
  PlanDeleted,
  PlanStep,
  Recognition,
  RecognitionQuery,
  RecognitionRecord,
  ToolName,
  ToolsDoc,
  Worker,
} from '../types'
import { ApiError } from '../api/types'
import { evalWhen } from '../plan'
import { uuid } from '../utils'
import { findDuplicates } from '../url'
import { mockAvatar, mockSnapshot } from './snapshot'

export const FRAME_W = 960
export const FRAME_H = 540
export const CAMERA_COUNT = 20
export const UNSTABLE_CAMERAS = ['cam-007', 'cam-015']
const WORKERS = ['fx-1', 'fx-2', 'fx-3']
const NAMES = ['Dana Levi', 'Omer Katz', 'Noa Ben-David', 'Yossi Cohen']
const CLASSES = ['person', 'car', 'truck', 'bicycle'] as const
const CAMERA_NAMES = [
  'Main Gate',
  'Gate 3',
  'Loading Dock A',
  'Loading Dock B',
  'Parking North',
  'Parking South',
  'Lobby',
  'Warehouse 1',
  'Warehouse 2',
  'Perimeter East',
  'Perimeter West',
  'Reception',
  'Server Room',
  'Cafeteria',
  'Rooftop',
  'Fuel Station',
  'Weigh Bridge',
  'Exit Lane',
  'Entrance Lane',
  'Back Alley',
]

export function mulberry32(seed: number) {
  let a = seed >>> 0
  return () => {
    a = (a + 0x6d2b79f5) >>> 0
    let t = Math.imul(a ^ (a >>> 15), 1 | a)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}
const frac = (x: number) => x - Math.floor(x)
const camIndex = (id: string) => Number(id.split('-')[1]) || 0

export const MOCK_TOOLS: ToolsDoc = {
  version: 1,
  tools: [
    {
      name: 'detect_objects',
      description: 'Detect COCO objects (person, car, truck, bus, ...). Cheap.',
      url: 'http://detector:8101/detect_objects',
      cost: 'low',
      output: 'objects',
      params: {
        classes: 'comma-separated COCO class filter (optional)',
        min_confidence: '0..1, default 0.4',
      },
    },
    {
      name: 'read_plate',
      description: 'Find and read vehicle license plates.',
      url: 'http://lpr:8102/read_plate',
      cost: 'medium',
      output: 'plates',
      emits_recognition: 'plate',
      params: {},
    },
    {
      name: 'read_text',
      description: 'Read general text (signs, labels).',
      url: 'http://ocr:8103/read_text',
      cost: 'high',
      output: 'texts',
      params: {},
    },
    {
      name: 'detect_faces',
      description: 'Detect faces (no identity).',
      url: 'http://face:8104/detect_faces',
      cost: 'low',
      output: 'faces',
      params: {},
    },
    {
      name: 'identify_faces',
      description: 'Detect faces and match against enrolled persons.',
      url: 'http://face:8104/identify_faces',
      cost: 'medium',
      output: 'faces',
      emits_recognition: 'face',
      params: { threshold: 'cosine similarity threshold, default 0.363' },
    },
  ],
}

/** Health of one camera as a pure function of time (so it is deterministic and testable). */
export function healthAt(cam: CameraConfig, t: number, workerId: string | null): CameraHealth {
  const idx = camIndex(cam.camera_id)
  const base = {
    camera_id: cam.camera_id,
    name: cam.name,
    fps_target: cam.analysis_fps,
    ts: t,
  }
  if (!cam.enabled) {
    return {
      ...base,
      state: 'unassigned',
      worker_id: null,
      fps_actual: 0,
      last_frame_ts: null,
      reconnects_1h: 0,
      uptime_pct_1h: 0,
      last_error: null,
    }
  }
  let state: HealthState = 'online'
  let fps = cam.analysis_fps * (0.97 + 0.03 * Math.sin(t / 3000 + idx))
  let lastFrame: number | null = t - (t % 333)
  let err: string | null = null
  let errorClass: string | null = null
  let reconnects = idx % 5 === 0 ? 1 : 0
  let uptime = 99.9 - (idx % 4) * 0.1

  if (UNSTABLE_CAMERAS.includes(cam.camera_id)) {
    const period = cam.camera_id === 'cam-007' ? 60_000 : 44_000
    const phase = (t + idx * 7000) % period
    const cycle = Math.floor((t + idx * 7000) / period)
    reconnects = 2 + (cycle % 9)
    uptime = 87.5 + (cycle % 5)
    if (phase > period * 0.5 && phase <= period * 0.7) {
      state = 'degraded'
      fps = cam.analysis_fps * 0.45
      err = 'low fps: packet loss 12%'
    } else if (phase > period * 0.7 && phase <= period * 0.9) {
      state = 'offline'
      fps = 0
      lastFrame = t - (phase - period * 0.7) - 5000
      err = 'rtsp: connection timed out (reconnecting, backoff 4s)'
      errorClass = 'unreachable'
    } else if (phase > period * 0.9) {
      state = 'degraded'
      fps = cam.analysis_fps * 0.6
      err = 'stream recovered, stabilising'
    }
  }
  return {
    ...base,
    state,
    worker_id: workerId,
    fps_actual: Math.round(fps * 100) / 100,
    last_frame_ts: lastFrame,
    reconnects_1h: reconnects,
    uptime_pct_1h: Math.round(uptime * 10) / 10,
    last_error: err,
    error_class: errorClass,
    error_hint: errorClass ? 'Camera unreachable: check network and power' : null,
    motion_skipped_pct:
      cam.motion?.enabled && state !== 'offline' ? 80 + Math.round(8 * Math.sin(t / 5000 + idx)) : null,
  }
}

interface Slot {
  cls: (typeof CLASSES)[number]
  speed: number
  phase: number
  lane: number
}
function slotsFor(idx: number): Slot[] {
  const n = 2 + (idx % 3)
  return Array.from({ length: n }, (_, k) => ({
    cls: CLASSES[(idx + k) % CLASSES.length],
    speed: 1 / (14_000 + ((idx * 7 + k * 5) % 9) * 2000),
    phase: (idx * 0.137 + k * 0.331) % 1,
    lane: k,
  }))
}
const SIZE: Record<string, [number, number]> = {
  person: [60, 150],
  car: [170, 95],
  truck: [260, 150],
  bicycle: [90, 100],
}
const plateFor = (idx: number, lane: number, pass: number) =>
  String(10_000_000 + ((idx * 7919 + lane * 104729 + pass * 15485863) % 90_000_000))

/** A realistic detections message with objects moving across the frame. */
export function detectionAt(
  cam: CameraConfig,
  t: number,
  planId = 'default',
  version = 1,
): Detection {
  const idx = camIndex(cam.camera_id)
  const objects: DetObject[] = []
  const plates: DetPlate[] = []
  const faces: DetFace[] = []
  const texts: DetText[] = []
  for (const s of slotsFor(idx)) {
    const pos = s.phase + t * s.speed * (s.lane % 2 ? -1 : 1)
    const f = frac(pos)
    const pass = Math.floor(pos)
    const [w, h] = SIZE[s.cls]
    const x1 = 20 + f * (FRAME_W - w - 40)
    const y1 = 150 + s.lane * 90 + 18 * Math.sin(t / 900 + s.lane) + (s.cls === 'person' ? 0 : 40)
    const box: Box = [
      Math.round(x1),
      Math.round(y1),
      Math.round(x1 + w),
      Math.round(Math.min(FRAME_H - 4, y1 + h)),
    ]
    objects.push({
      class: s.cls,
      confidence: round2(0.72 + 0.25 * Math.abs(Math.sin(t / 1300 + s.lane))),
      box,
    })
    if (s.cls === 'car' || s.cls === 'truck') {
      plates.push({
        plate: plateFor(idx, s.lane, pass),
        confidence: round2(0.88 + 0.1 * Math.abs(Math.sin(t / 1700))),
        box: [box[0] + w * 0.3, box[3] - 28, box[0] + w * 0.7, box[3] - 8].map(Math.round) as Box,
      })
    }
    if (s.cls === 'person') {
      const known = (idx + s.lane + pass) % 2 === 0
      faces.push({
        box: [box[0] + 18, box[1] + 4, box[2] - 18, box[1] + 50],
        confidence: 0.92,
        person_id: known ? `p-${(idx + pass) % NAMES.length}` : null,
        person_name: known ? NAMES[(idx + pass) % NAMES.length] : null,
        score: known ? round2(0.55 + 0.3 * frac(t / 4000)) : 0.2,
      })
    }
  }
  if (idx % 5 === 0)
    texts.push({ text: cam.name.toUpperCase(), confidence: 0.9, box: [40, 30, 260, 70] })
  const total = 60 + 40 * Math.abs(Math.sin(t / 2500 + idx))
  return {
    frame_id: uuid(),
    camera_id: cam.camera_id,
    ts: t,
    width: FRAME_W,
    height: FRAME_H,
    plan_id: planId,
    plan_version: version,
    objects,
    plates,
    faces,
    texts,
    timings_ms: { s1: round2(total * 0.6), s2: round2(total * 0.3) },
    total_ms: round2(total),
    executor_id: 'ex-1',
    partial: false,
    step_id: null,
  }
}
const round2 = (n: number) => Math.round(n * 100) / 100

export function recognitionFromDetection(d: Detection, rng: () => number): Recognition | null {
  const pickPlate = d.plates.length > 0 && (d.faces.length === 0 || rng() < 0.6)
  if (pickPlate) {
    const p = d.plates[Math.floor(rng() * d.plates.length)]
    return mk('plate', p.plate, null, p.confidence, p.box)
  }
  if (d.faces.length) {
    const f = d.faces[Math.floor(rng() * d.faces.length)]
    return mk('face', f.person_name ?? 'unknown', f.person_id, f.confidence, f.box)
  }
  return null
  function mk(
    type: 'plate' | 'face',
    value: string,
    pid: string | null,
    conf: number,
    box: Box,
  ): Recognition {
    return {
      recognition_id: uuid(),
      type,
      camera_id: d.camera_id,
      frame_id: d.frame_id,
      ts: d.ts,
      value,
      person_id: pid,
      confidence: conf,
      box,
      frame_key: `${d.camera_id}/${new Date(d.ts).toISOString().slice(0, 13).replace(/\D/g, '')}/${d.frame_id}.jpg`,
      plan_id: d.plan_id,
    }
  }
}

/** The mock still emits overlay detections for plan-less cameras (live overlay unchanged). */
const MOCK_UNPLANNED_STEPS: PlanStep[] = [{ id: 's1', tool: 'detect_objects', mode: 'inline' }]

const TOOL_COST: Record<ToolName, number> = {
  detect_objects: 38,
  read_plate: 55,
  read_text: 210,
  detect_faces: 22,
  identify_faces: 48,
}

export function metricsAt(plan: Plan, t: number): PipelineMetrics[] {
  const cams = Math.max(1, plan.camera_ids.length)
  const window_s = 5
  const frames = Math.round(cams * 3 * window_s)
  let carried = frames
  return plan.steps.map((s, i) => {
    const gated = s.when && s.when.type !== 'always'
    const processed = Math.max(
      0,
      Math.round(gated ? carried * (0.25 + 0.1 * Math.sin(t / 7000 + i)) : carried),
    )
    carried = processed
    const base = TOOL_COST[s.tool] * (s.mode === 'queue' ? 1.8 : 1)
    const wob = 1 + 0.18 * Math.sin(t / 4000 + i * 2)
    return {
      plan_id: plan.plan_id,
      step_id: s.id,
      tool: s.tool,
      window_s,
      processed,
      skipped: gated ? frames - processed : 0,
      errors: s.tool === 'read_text' && Math.sin(t / 9000) > 0.85 ? 1 : 0,
      p50_ms: round2(base * wob),
      p95_ms: round2(base * wob * 1.7),
      executor_id: 'ex-1',
      ts: t,
    }
  })
}

export class MockWorld {
  cameras: CameraConfig[]
  plans: Plan[]
  history: RecognitionRecord[] = []
  rng: () => number
  now: () => number
  onPlanChanged: ((p: Plan | PlanDeleted) => void) | null = null
  persons: Person[] = []
  /** normalized query -> hit count (mock of the planner draft cache, contract 9.4) */
  draftCache = new Map<string, number>([
    ['read license plates of trucks entering the gates', 3],
    ['identify known people in the lobby', 1],
  ])
  recentQueries = [
    'identify known people in the lobby',
    'read license plates of trucks entering the gates',
  ]
  private workerOf = new Map<string, string>()

  constructor(opts: { seed?: number; now?: () => number } = {}) {
    this.rng = mulberry32(opts.seed ?? 42)
    this.now = opts.now ?? (() => Date.now())
    const t0 = this.now()
    this.cameras = Array.from({ length: CAMERA_COUNT }, (_, i) => {
      const id = `cam-${String(i + 1).padStart(3, '0')}`
      return {
        camera_id: id,
        name: CAMERA_NAMES[i],
        source_url: `rtsp://mediamtx:8554/${id}`,
        stream_url: 'publisher',
        provision_error:
          id === 'cam-018' ? 'mediamtx: source unreachable (retrying in 30 s)' : null,
        analysis_fps: 3,
        enabled: id !== 'cam-020',
        plan_id: null,
        tags: i % 3 === 0 ? ['gate'] : i % 3 === 1 ? ['yard'] : ['indoor'],
        updated_at: t0,
      }
    })
    const trucks = this.buildPlan(
      'Trucks and plates',
      'read plates of trucks and cars at gates',
      ['cam-001', 'cam-002', 'cam-003', 'cam-004', 'cam-005'],
      [
        {
          id: 's1',
          tool: 'detect_objects',
          mode: 'inline',
          params: { classes: 'car,truck', min_confidence: 0.5 },
        },
        {
          id: 's2',
          tool: 'read_plate',
          mode: 'queue',
          when: { type: 'found', class: 'truck', min_confidence: 0.6 },
        },
        { id: 's3', tool: 'read_text', mode: 'queue', when: { type: 'found', class: 'truck' } },
      ],
    )
    const people = this.buildPlan(
      'Known people',
      'identify people entering the lobby',
      ['cam-007', 'cam-012', 'cam-013'],
      [
        { id: 's1', tool: 'detect_objects', mode: 'inline', params: { classes: 'person' } },
        {
          id: 's2',
          tool: 'identify_faces',
          mode: 'inline',
          params: { threshold: 0.363 },
          when: { type: 'found', class: 'person' },
        },
        { id: 's3', tool: 'detect_faces', mode: 'inline', when: { type: 'failed', step: 's2' } },
      ],
    )
    this.plans = [trucks, people]
    this.persons = NAMES.map((name, i) => ({
      person_id: `p-${i}`,
      name,
      photos: 1 + (i % 3),
      created_at: t0 - (i + 1) * 86_400_000 * 3,
      photo_url: mockAvatar(name),
    }))
    // two cameras pulling the same stream (different spelling) -> duplicate_of
    const c16 = this.cameras.find((c) => c.camera_id === 'cam-016')
    const c17 = this.cameras.find((c) => c.camera_id === 'cam-017')
    if (c16 && c17) {
      c16.stream_url = 'rtsp://10.0.0.5:554/stream1'
      c17.stream_url = 'RTSP://admin:pw@10.0.0.5/stream1/'
    }
    this.recomputeDuplicates()
    this.syncPlanIds()
    this.assignWorkers()
    // 80 historical recognitions over the last 2 hours
    for (let i = 0; i < 80; i++) {
      const cam = this.cameras[Math.floor(this.rng() * 19)]
      const t = t0 - Math.floor(this.rng() * 2 * 3600_000)
      const rec = recognitionFromDetection(detectionAt(cam, t), this.rng)
      if (rec) this.history.push(this.withSnapshot(rec))
    }
    this.history.sort((a, b) => b.ts - a.ts)
  }

  private buildPlan(name: string, query: string, cams: string[], steps: PlanStep[]): Plan {
    return {
      plan_id: uuid(),
      version: 1,
      name,
      query,
      camera_ids: cams,
      status: 'active',
      created_at: this.now() - 3600_000,
      steps,
    }
  }
  private syncPlanIds() {
    for (const c of this.cameras) c.plan_id = null
    for (const p of this.plans)
      for (const id of p.camera_ids) {
        const c = this.cameras.find((x) => x.camera_id === id)
        if (c) c.plan_id = p.plan_id
      }
  }
  recomputeDuplicates() {
    for (const c of this.cameras)
      c.duplicate_of = findDuplicates(c.stream_url, this.cameras, c.camera_id)
  }
  private assignWorkers() {
    this.workerOf.clear()
    this.cameras.forEach(
      (c, i) =>
        c.enabled && this.workerOf.set(c.camera_id, WORKERS[Math.min(2, Math.floor(i / 7))]),
    )
  }
  withSnapshot(r: Recognition): RecognitionRecord {
    return {
      ...r,
      snapshot_key: `${r.type}/${new Date(r.ts).toISOString().slice(0, 10).replace(/-/g, '')}/${r.recognition_id}.jpg`,
      snapshot_url: mockSnapshot(r),
    }
  }

  health(t = this.now()): CameraHealth[] {
    return this.cameras.map((c) => {
      const h = healthAt(c, t, this.workerOf.get(c.camera_id) ?? null)
      return c.enabled && !c.plan_id ? { ...h, mode: 'idle' as const } : h
    })
  }
  summary(t = this.now()): HealthSummary {
    const h = this.health(t)
    const n = (s: HealthState) => h.filter((x) => x.state === s).length
    return {
      total: h.length,
      online: n('online'),
      degraded: n('degraded'),
      offline: n('offline'),
      unassigned: n('unassigned'),
      idle: h.filter((x) => x.mode === 'idle').length,
    }
  }
  workers(t = this.now()): Worker[] {
    return WORKERS.map((w) => ({
      worker_id: w,
      capacity: 25,
      last_seen: t - 1500,
      assigned: this.cameras.filter((c) => this.workerOf.get(c.camera_id) === w),
    }))
  }
  detection(cameraId: string, t = this.now()): Detection | null {
    const cam = this.cameras.find((c) => c.camera_id === cameraId)
    if (!cam || !cam.enabled) return null
    const plan = this.plans.find((p) => p.plan_id === cam.plan_id)
    const d = detectionAt(cam, t, plan?.plan_id ?? 'default', plan?.version ?? 1)
    const steps = plan?.steps ?? MOCK_UNPLANNED_STEPS
    // which steps actually run on this frame (their `when` is evaluated on the detector output)
    const ran = steps.filter((s) => evalWhen(s.when, { objects: d.objects }))
    const idx = Number(cam.camera_id.split('-')[1]) || 0
    d.timings_ms = Object.fromEntries(
      ran.map((s) => [s.id, round2(TOOL_COST[s.tool] * (0.8 + 0.4 * frac(t / 1000 + idx * 0.37)))]),
    )
    d.total_ms = round2(
      ran.filter((s) => s.mode === 'inline').reduce((n, s) => n + d.timings_ms[s.id], 0),
    )
    const ranTools = new Set(ran.map((s) => s.tool))
    if (!ranTools.has('read_plate')) d.plates = []
    if (!ranTools.has('identify_faces') && !ranTools.has('detect_faces')) d.faces = []
    if (!ranTools.has('read_text')) d.texts = []
    // detect_faces only: no identification (contract 9.1)
    if (!ranTools.has('identify_faces') && ranTools.has('detect_faces'))
      d.faces = d.faces.map((f) => ({ ...f, person_id: null, person_name: null, score: null }))
    return d
  }

  /**
   * Splits queue-step results into separate partial messages (contract 9.1), one for every
   * queue step that ran, even with an empty result. Mutates `main`.
   */
  splitPartials(main: Detection): Detection[] {
    const plan = this.plans.find((p) => p.plan_id === main.plan_id)
    if (!plan) return []
    const out: Detection[] = []
    for (const s of plan.steps) {
      if (s.mode !== 'queue' || !(s.id in main.timings_ms)) continue
      const key = (
        {
          detect_objects: 'objects',
          read_plate: 'plates',
          read_text: 'texts',
          detect_faces: 'faces',
          identify_faces: 'faces',
        } as const
      )[s.tool]
      const moved = main[key] as unknown[]
      const part: Detection = {
        ...main,
        objects: [],
        plates: [],
        faces: [],
        texts: [],
        timings_ms: { [s.id]: main.timings_ms[s.id] },
        partial: true,
        step_id: s.id,
      }
      ;(part[key] as unknown[]) = moved
      ;(main[key] as unknown[]) = []
      delete main.timings_ms[s.id]
      out.push(part)
    }
    return out
  }

  metrics(planId: string, t = this.now()): PipelineMetrics[] {
    const p = this.plans.find((x) => x.plan_id === planId)
    return p ? metricsAt(p, t) : []
  }

  // ---- REST-ish operations
  listRecognitions(q: RecognitionQuery): RecognitionRecord[] {
    return this.history
      .filter(
        (r) =>
          (!q.camera_id || r.camera_id === q.camera_id) &&
          (!q.type || r.type === q.type) &&
          (!q.person_id || r.person_id === q.person_id) &&
          (!q.since || r.ts >= q.since),
      )
      .slice(0, q.limit ?? 50)
  }
  addRecognition(r: Recognition) {
    this.history.unshift(this.withSnapshot(r))
    if (this.history.length > 500) this.history.pop()
  }
  upsertCamera(c: CameraInput, existing?: string): CameraConfig {
    const prev = this.cameras.find((x) => x.camera_id === (existing ?? c.camera_id))
    const full: CameraConfig = {
      ...c,
      source_url: `rtsp://mediamtx:8554/${c.camera_id}`,
      provision_error:
        prev?.provision_error && prev.stream_url === c.stream_url ? prev.provision_error : null,
      updated_at: this.now(),
    }
    const i = this.cameras.findIndex((x) => x.camera_id === (existing ?? c.camera_id))
    if (i >= 0) this.cameras[i] = full
    else this.cameras.push(full)
    this.assignWorkers()
    this.recomputeDuplicates()
    return full
  }
  removeCamera(id: string) {
    this.cameras = this.cameras.filter((c) => c.camera_id !== id)
    this.assignWorkers()
    this.recomputeDuplicates()
  }

  /** Draft with the 9.4 cache: only valid drafts are cached; refresh bypasses and replaces. */
  draft(req: DraftRequest, refresh = false): DraftResponse {
    const key = req.query
      .toLowerCase()
      .trim()
      .replace(/\s+/g, ' ')
      .replace(/[.!?]+$/, '')
    const res = this.buildDraft(req)
    if (!res.valid) return { ...res, cached: false, cache_hits: 0 }
    const hits = this.draftCache.get(key)
    if (hits !== undefined && !refresh) {
      this.draftCache.set(key, hits + 1)
      return { ...res, cached: true, cache_hits: hits + 1 }
    }
    this.draftCache.set(key, 0)
    this.recentQueries = [key, ...this.recentQueries.filter((q) => q !== key)].slice(0, 10)
    return { ...res, cached: false, cache_hits: 0 }
  }

  // ---- people library (9.4)
  enrollPerson(name: string, fileName: string): Person {
    this.checkFaces(fileName)
    const p: Person = {
      person_id: `p-${Date.now().toString(36)}`,
      name,
      photos: 1,
      created_at: this.now(),
      photo_url: mockAvatar(name),
    }
    this.persons.push(p)
    return p
  }
  /** Mock of the face service: a file name containing "group"/"noface" simulates 2+/0 faces. */
  checkFaces(fileName: string) {
    if (/group/i.test(fileName))
      throw new ApiError('400 expected exactly one face in the image, found 2', 400)
    if (/noface/i.test(fileName)) throw new ApiError('400 no face detected in the image', 400)
  }
  private buildDraft(req: DraftRequest): DraftResponse {
    const q = req.query.toLowerCase()
    const steps: PlanStep[] = []
    const errors: string[] = []
    const add = (s: Omit<PlanStep, 'id'>) =>
      steps.push({ id: `s${steps.length + 1}`, ...s } as PlanStep)
    const wantsPlate = /plate|license|lpr|vehicle number/.test(q)
    const wantsFace = /face|who|identify|person|people/.test(q)
    const wantsText = /text|sign|label|container|ocr/.test(q)
    const classes = ['truck', 'car', 'bus', 'person', 'motorcycle', 'bicycle'].filter((c) =>
      q.includes(c),
    )
    if (/invalid|hack|delete everything/.test(q)) {
      errors.push('step s1: unknown tool "run_shell"', 'plan has no valid detection step')
    }
    add({
      tool: 'detect_objects',
      mode: 'inline',
      params: classes.length ? { classes: classes.join(','), min_confidence: 0.5 } : {},
    })
    if (wantsPlate)
      add({
        tool: 'read_plate',
        mode: 'queue',
        when: {
          type: 'found',
          class: classes.filter((c) => c !== 'person').join(',') || 'car,truck,bus',
          min_confidence: 0.5,
        },
      })
    if (wantsFace) {
      add({
        tool: 'identify_faces',
        mode: 'inline',
        params: { threshold: 0.363 },
        when: { type: 'found', class: 'person' },
      })
      add({
        tool: 'detect_faces',
        mode: 'inline',
        when: { type: 'failed', step: `s${steps.length}` },
      })
    }
    if (wantsText)
      add({
        tool: 'read_text',
        mode: 'queue',
        when: { type: 'count', class: classes[0] ?? 'truck', gt: 0 },
      })
    const plan: Plan = {
      plan_id: uuid(),
      version: 1,
      name: req.query.slice(0, 60) || 'Untitled plan',
      query: req.query,
      camera_ids: req.camera_ids,
      status: 'draft',
      created_at: this.now(),
      steps,
    }
    const explanation = `Runs ${steps.map((s) => s.tool).join(' → ')}. Expensive tools run only when the cheap detector finds something relevant, to save CPU across ${req.camera_ids.length || 'all'} cameras.`
    return { plan, valid: errors.length === 0, errors, explanation }
  }
  activate(plan: Plan): Plan {
    const active: Plan = { ...plan, status: 'active', created_at: this.now() }
    this.plans.push(active)
    this.syncPlanIds()
    this.onPlanChanged?.(active)
    return active
  }
  removePlan(id: string) {
    const p = this.plans.find((x) => x.plan_id === id)
    this.plans = this.plans.filter((x) => x.plan_id !== id)
    this.syncPlanIds()
    if (p) this.onPlanChanged?.({ plan_id: id, deleted: true })
  }
}
