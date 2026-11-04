// Types mirror contracts/CONTRACTS.md (snake_case, exactly as on the wire).

export type Box = [number, number, number, number] // [x1, y1, x2, y2] in analysis-frame pixels

export interface DetObject {
  class: string
  confidence: number
  box: Box
}
export interface DetPlate {
  plate: string
  confidence: number
  box: Box
}
export interface DetFace {
  box: Box
  confidence: number
  person_id: string | null
  person_name: string | null
  /** null for detect_faces (no identification) */
  score: number | null
}
export interface DetText {
  text: string
  confidence: number
  box: Box
}

/** 5.3 detections */
export interface Detection {
  frame_id: string
  camera_id: string
  ts: number
  width: number
  height: number
  plan_id: string
  plan_version: number
  objects: DetObject[]
  plates: DetPlate[]
  faces: DetFace[]
  texts: DetText[]
  timings_ms: Record<string, number>
  total_ms: number
  executor_id: string
  /** 9.1: true for the late result of a queue-mode step (only that step output) */
  partial: boolean
  step_id: string | null
}

/** 5.4 recognitions */
export type RecognitionType = 'plate' | 'face'
export interface Recognition {
  recognition_id: string
  type: RecognitionType
  camera_id: string
  frame_id: string
  ts: number
  value: string
  person_id: string | null
  confidence: number
  box: Box
  frame_key: string
  plan_id: string
}
/** 7.4 GET /api/recognitions item (recognition + snapshot_url / snapshot_key) */
export interface RecognitionRecord extends Recognition {
  snapshot_url?: string | null
  snapshot_key?: string | null
}

/** 9.9 item 1 */
export type MotionSensitivity = 'low' | 'medium' | 'high'
export interface MotionConfig {
  enabled: boolean
  sensitivity: MotionSensitivity
  heartbeat_s: number
}
/** 9.9 item 5; unknown strings from newer backends are tolerated everywhere */
export type RtspErrorClass =
  | 'auth_failed'
  | 'not_found'
  | 'codec_unsupported'
  | 'unreachable'
  | 'stalled'
  | 'decode_error'
  | 'eof'

/** 5.1 camera config */
export interface CameraConfig {
  camera_id: string
  name: string
  /** read-only: always rtsp://mediamtx:8554/{camera_id}, set by the registry (9.3) */
  source_url?: string
  /** the real camera URL the user enters, or "publisher" (9.3) */
  stream_url: string
  /** MediaMTX provisioning failure, retried by the registry (9.3) */
  provision_error?: string | null
  /** 9.4: other cameras pulling the same stream (empty when unique) */
  duplicate_of?: string[]
  analysis_fps: number
  enabled: boolean
  plan_id: string | null
  tags: string[]
  updated_at?: number
  /** read-only registry fields on GET /api/cameras (contract 9.5) */
  pinned_worker?: string | null
  assigned_worker?: string | null
  generation?: number
  pin_fallback?: boolean
  /** 9.8: extraction mode decided by the registry; absent on older backends (= full) */
  extract?: { mode: ExtractMode; fps?: number }
  /** 9.9: optional substream used for analysis (real camera URL) */
  analysis_stream_url?: string | null
  /** 9.9: read-only, set by the registry */
  analysis_source_url?: string | null
  /** 9.9: absent on older backends (= disabled) */
  motion?: Partial<MotionConfig> | null
}
/** What clients send: never source_url (9.3). */
export type CameraInput = Omit<
  CameraConfig,
  'updated_at' | 'source_url' | 'provision_error' | 'duplicate_of' | 'pinned_worker' | 'assigned_worker' | 'generation' | 'pin_fallback' | 'extract' | 'analysis_source_url'
>

export function toCameraInput(c: CameraConfig, patch: Partial<CameraInput> = {}): CameraInput {
  return {
    camera_id: c.camera_id,
    name: c.name,
    stream_url: c.stream_url,
    analysis_fps: c.analysis_fps,
    enabled: c.enabled,
    plan_id: c.plan_id,
    tags: c.tags,
    ...(c.analysis_stream_url ? { analysis_stream_url: c.analysis_stream_url } : {}),
    ...(c.motion ? { motion: c.motion } : {}),
    ...patch,
  }
}

/** 7.2 GET /api/workers (assigned may be camera ids or full configs; both are handled) */
export interface Worker {
  worker_id: string
  capacity: number
  assigned: Array<CameraConfig | string>
  last_seen: number
}

/** 5.6 camera.health */
export type HealthState = 'online' | 'degraded' | 'offline' | 'unassigned'
/** 9.8: `idle` = keyframes only, 1 frame / 5 s */
export type ExtractMode = 'full' | 'idle'
export interface CameraHealth {
  camera_id: string
  name: string
  state: HealthState
  worker_id: string | null
  fps_actual: number
  fps_target: number
  last_frame_ts: number | null
  reconnects_1h: number
  uptime_pct_1h: number
  last_error: string | null
  ts: number
  /** 9.8; absent on older backends (= full) */
  mode?: ExtractMode
  /** 9.9: rolling 60 s, 0-100; null/absent when motion is off, idle or unsupported */
  motion_skipped_pct?: number | null
  /** 9.9 item 5: RTSP failure class (string: newer backends may add classes) */
  error_class?: RtspErrorClass | (string & {}) | null
  error_hint?: string | null
}
export interface HealthSummary {
  total: number
  online: number
  degraded: number
  offline: number
  unassigned: number
  /** 9.8: cameras in idle extraction mode (also counted in their state) */
  idle: number
  /** 9.9: offline cameras per error class; omitted when none */
  by_error_class?: Record<string, number>
}

/** 6 / plan.schema.json */
export type ToolName =
  'detect_objects' | 'read_plate' | 'read_text' | 'detect_faces' | 'identify_faces'
export type StepMode = 'inline' | 'queue'
export type PlanWhen =
  | { type: 'always' }
  | { type: 'found'; class: string; min_confidence?: number }
  | { type: 'count'; class: string; gt: number }
  | { type: 'failed'; step: string }
export interface PlanStep {
  id: string
  tool: ToolName
  mode: StepMode
  params?: Record<string, string | number>
  when?: PlanWhen
}
export type PlanStatus = 'draft' | 'active' | 'disabled'
export interface Plan {
  plan_id: string
  version: number
  name: string
  query: string
  camera_ids: string[]
  status: PlanStatus
  created_at: number
  steps: PlanStep[]
}
/** 7.5 */
export interface DraftRequest {
  query: string
  camera_ids: string[]
}
export interface DraftResponse {
  plan: Plan
  valid: boolean
  errors: string[]
  explanation: string
  /** 9.4 draft cache */
  cached?: boolean
  cache_hits?: number
}

/** 9.4 GET /api/persons item */
export interface Person {
  person_id: string
  name: string
  photos: number
  created_at: number
  photo_url: string
}
export interface PersonsResponse {
  persons: Person[]
  count: number
}
export interface ToolDef {
  name: ToolName
  description: string
  url: string
  cost: 'low' | 'medium' | 'high'
  output: string
  emits_recognition?: RecognitionType
  params: Record<string, string>
}
export interface ToolsDoc {
  version: number
  tools: ToolDef[]
}

/** 9.1: planChanged for a deleted plan */
export interface PlanDeleted {
  plan_id: string
  deleted: true
}
export const isPlanDeleted = (p: Plan | PlanDeleted): p is PlanDeleted =>
  (p as PlanDeleted).deleted === true

/** 5.8 pipeline.metrics */
export interface PipelineMetrics {
  plan_id: string
  step_id: string
  tool: ToolName
  window_s: number
  processed: number
  skipped: number
  errors: number
  p50_ms: number
  p95_ms: number
  executor_id: string
  ts: number
}

export interface RecognitionQuery {
  camera_id?: string
  type?: RecognitionType
  person_id?: string
  since?: number
  limit?: number
}
