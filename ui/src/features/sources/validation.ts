import { DEFAULT_MOTION } from '@/lib/motion'
import type { CameraInput, MotionConfig } from '@/lib/types'

export type CameraField =
  | 'camera_id'
  | 'name'
  | 'stream_url'
  | 'analysis_stream_url'
  | 'analysis_fps'
  | 'tags'
  | 'motion_sensitivity'
  | 'motion_heartbeat_s'
export type CameraErrors = Partial<Record<CameraField, string>>

const FIELDS: CameraField[] = [
  'camera_id',
  'name',
  'stream_url',
  'analysis_stream_url',
  'analysis_fps',
  'tags',
  'motion_sensitivity',
  'motion_heartbeat_s',
]
/** Wire path ("motion.heartbeat_s") to form field. */
const WIRE: Record<string, CameraField> = {
  'motion.sensitivity': 'motion_sensitivity',
  'motion.heartbeat_s': 'motion_heartbeat_s',
  motion_sensitivity: 'motion_sensitivity',
  motion_heartbeat_s: 'motion_heartbeat_s',
  heartbeat_s: 'motion_heartbeat_s',
  sensitivity: 'motion_sensitivity',
}
const toField = (path: string): CameraField | undefined => {
  const p = path.replace(/^(body|camera)\./, '')
  return WIRE[p] ?? FIELDS.find((f) => f === p)
}
const WIRE_NAME: Record<CameraField, string> = {
  camera_id: 'camera_id',
  name: 'name',
  stream_url: 'stream_url',
  analysis_stream_url: 'analysis_stream_url',
  analysis_fps: 'analysis_fps',
  tags: 'tags',
  motion_sensitivity: 'sensitivity',
  motion_heartbeat_s: 'heartbeat_s',
}
/** First form field whose wire name appears as a whole word in a plain-text error. */
const mentioned = (text: string): CameraField | undefined =>
  FIELDS.find((f) => new RegExp('(^|[^a-z_])' + WIRE_NAME[f] + '($|[^a-z_])').test(text))
const asText = (v: unknown): string =>
  typeof v === 'string' ? v : Array.isArray(v) ? v.map(asText).join('; ') : JSON.stringify(v)

/**
 * Map a 400/422 body onto form fields (contract 9.9: "validation errors -> 400 with field names").
 * Accepts {errors|detail|fields: {field: msg}}, [{field|loc, msg|message}] (FastAPI) and plain
 * strings that mention a field name. Unmappable text comes back as `rest` for a form-level alert.
 */
export function mapServerErrors(body: unknown): { errors: CameraErrors; rest?: string } {
  const errors: CameraErrors = {}
  const rest: string[] = []
  const put = (path: string, msg: unknown) => {
    const f = toField(path)
    if (f) errors[f] ??= asText(msg)
    else rest.push(`${path}: ${asText(msg)}`)
  }
  const walk = (v: unknown) => {
    if (typeof v === 'string') {
      const f = mentioned(v)
      if (f) errors[f] ??= v
      else rest.push(v)
    } else if (Array.isArray(v)) v.forEach(walk)
    else if (v && typeof v === 'object') {
      const o = v as Record<string, unknown>
      const path = o.field ?? o.loc
      const msg = o.msg ?? o.message ?? o.error
      if (path !== undefined && msg !== undefined)
        put(Array.isArray(path) ? path.join('.') : String(path), msg)
      else
        for (const [k, val] of Object.entries(o)) {
          if (k === 'detail' || k === 'errors' || k === 'fields') walk(val)
          else if (toField(k)) put(k, val)
          else if (k === 'message' || k === 'error') walk(val)
        }
    }
  }
  walk(body)
  return { errors, rest: rest.length ? rest.join('; ') : undefined }
}

/** contract 9.3 (also the MediaMTX path name) */
export const CAMERA_ID_RE = /^[a-z0-9][a-z0-9-]{2,39}$/
const STREAM_RE = /^(rtsp|rtsps|rtmp|srt):\/\/[^\s/]+\S*$/i
const HLS_RE = /^https?:\/\/[^\s/]+\S*\.m3u8(\?\S*)?$/i

export function isValidStreamUrl(u: string, allowPublisher = true): boolean {
  const s = u.trim()
  return (allowPublisher && s === 'publisher') || STREAM_RE.test(s) || HLS_RE.test(s)
}

export function validateMotion(m: MotionConfig): CameraErrors {
  const e: CameraErrors = {}
  if (!['low', 'medium', 'high'].includes(m.sensitivity))
    e.motion_sensitivity = 'Choose low, medium or high'
  if (!Number.isFinite(m.heartbeat_s) || m.heartbeat_s < 1 || m.heartbeat_s > 60)
    e.motion_heartbeat_s = 'Between 1 and 60 seconds'
  return e
}

export function validateCamera(
  c: CameraInput,
  existingIds: string[] = [],
  editing = false,
): CameraErrors {
  const e: CameraErrors = {}
  if (!CAMERA_ID_RE.test(c.camera_id))
    e.camera_id = '3-40 chars: lowercase letters, digits and - (e.g. cam-021)'
  else if (!editing && existingIds.includes(c.camera_id))
    e.camera_id = 'A camera with this id already exists'
  if (!c.name.trim()) e.name = 'Name is required'
  else if (c.name.length > 80) e.name = 'Max 80 characters'
  if (!isValidStreamUrl(c.stream_url))
    e.stream_url = 'rtsp://, rtsps://, rtmp://, srt:// or http(s)://...m3u8 (or "publisher")'
  if (c.analysis_stream_url && !isValidStreamUrl(c.analysis_stream_url, false))
    e.analysis_stream_url = 'rtsp://, rtsps://, rtmp://, srt:// or http(s)://...m3u8'
  if (!Number.isFinite(c.analysis_fps) || c.analysis_fps <= 0 || c.analysis_fps > 30)
    e.analysis_fps = 'Between 0.1 and 30 fps'
  if (c.tags.some((t) => t.length > 32)) e.tags = 'Tags are max 32 characters'
  if (c.motion) Object.assign(e, validateMotion({ ...DEFAULT_MOTION, ...c.motion }))
  return e
}

export const parseTags = (s: string): string[] => [
  ...new Set(
    s
      .split(',')
      .map((t) => t.trim().toLowerCase())
      .filter(Boolean),
  ),
]
