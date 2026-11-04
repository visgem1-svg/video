import type { Box, Detection } from './types'

export interface Rect {
  x: number
  y: number
  w: number
  h: number
}

/** How long a box stays fully visible, and when it is completely gone (ms since arrival). */
/**
 * Detections arrive every ~3 s per camera on a CPU host, so the last one is HELD until the next
 * arrives: full opacity for BOX_HOLD_MS, then dimmed linearly down to MIN_ALPHA at BOX_HIDE_MS,
 * hidden when older than BOX_HIDE_MS.
 */
export const BOX_HOLD_MS = 500
export const BOX_HIDE_MS = 12000
export const MIN_ALPHA = 0.35

/**
 * Letterbox transform for `object-fit: contain`: where the source frame
 * (srcW x srcH) lands inside a destination (dstW x dstH) surface.
 */
export function containTransform(srcW: number, srcH: number, dstW: number, dstH: number) {
  if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0) return { scale: 0, offsetX: 0, offsetY: 0 }
  const scale = Math.min(dstW / srcW, dstH / srcH)
  return { scale, offsetX: (dstW - srcW * scale) / 2, offsetY: (dstH - srcH * scale) / 2 }
}

/** Scale an analysis-frame box ([x1,y1,x2,y2] px of width x height) onto a canvas of dstW x dstH. */
export function scaleBox(
  box: Box,
  srcW: number,
  srcH: number,
  dstW: number,
  dstH: number,
  fit: 'contain' | 'stretch' = 'contain',
): Rect {
  const [x1, y1, x2, y2] = box
  let sx: number, sy: number, ox: number, oy: number
  if (fit === 'stretch') {
    sx = dstW / srcW
    sy = dstH / srcH
    ox = oy = 0
  } else {
    const t = containTransform(srcW, srcH, dstW, dstH)
    sx = sy = t.scale
    ox = t.offsetX
    oy = t.offsetY
  }
  const left = Math.min(x1, x2)
  const top = Math.min(y1, y2)
  return {
    x: ox + left * sx,
    y: oy + top * sy,
    w: Math.abs(x2 - x1) * sx,
    h: Math.abs(y2 - y1) * sy,
  }
}

/** 1 while fresh, dims linearly to MIN_ALPHA at hideMs, 0 (hidden) once older than hideMs. */
export function fadeAlpha(ageMs: number, holdMs = BOX_HOLD_MS, hideMs = BOX_HIDE_MS): number {
  if (ageMs > hideMs) return 0
  if (ageMs <= holdMs) return 1
  return 1 - ((1 - MIN_ALPHA) * (ageMs - holdMs)) / (hideMs - holdMs)
}

export const isStale = (ageMs: number, hideMs = BOX_HIDE_MS): boolean => ageMs > hideMs

/** "updated 3s ago" chip text (null until the first detection arrives). */
export function ageChipText(ageMs: number | null): string | null {
  if (ageMs == null) return null
  const s = Math.max(0, Math.round(ageMs / 1000))
  return s < 1 ? 'updated now' : `updated ${s}s ago`
}

/**
 * 9.1: a queue-mode step answers with a second message for the same frame_id carrying only that
 * step output (partial=true). Merge it into the frame being shown instead of replacing it.
 * Works in either arrival order.
 */
export function mergeDetection(cur: Detection | null, inc: Detection): Detection {
  if (!cur || cur.frame_id !== inc.frame_id) return inc
  return {
    ...cur,
    objects: [...cur.objects, ...inc.objects],
    plates: [...cur.plates, ...inc.plates],
    faces: [...cur.faces, ...inc.faces],
    texts: [...cur.texts, ...inc.texts],
    timings_ms: { ...cur.timings_ms, ...inc.timings_ms },
    partial: cur.partial && inc.partial,
    step_id: cur.partial ? inc.step_id : cur.step_id,
  }
}

/** Label for a face: identified name, "unknown" (identified but no match) or "face detected" (detect_faces). */
export function faceLabel(f: { person_name: string | null; score: number | null }): string {
  if (f.person_name) return f.person_name
  return f.score == null ? 'face detected' : 'unknown'
}

export type DrawKind = 'object' | 'plate' | 'face' | 'face_known' | 'text'
export interface DrawItem {
  rect: Rect
  label: string
  kind: DrawKind
  key: string
}

/** Pure conversion detection -> things to draw on a canvas of dstW x dstH. */
export function buildDrawItems(det: Detection, dstW: number, dstH: number): DrawItem[] {
  const s = (b: Box) => scaleBox(b, det.width, det.height, dstW, dstH)
  const items: DrawItem[] = []
  det.objects?.forEach((o) =>
    items.push({
      rect: s(o.box),
      label: `${o.class} ${Math.round(o.confidence * 100)}%`,
      kind: 'object',
      key: o.class,
    }),
  )
  det.plates?.forEach((p) =>
    items.push({ rect: s(p.box), label: p.plate, kind: 'plate', key: 'plate' }),
  )
  det.faces?.forEach((f) =>
    items.push({
      rect: s(f.box),
      label: faceLabel(f),
      kind: f.person_name ? 'face_known' : 'face',
      key: f.person_id ?? 'face',
    }),
  )
  det.texts?.forEach((t) =>
    items.push({ rect: s(t.box), label: t.text, kind: 'text', key: 'text' }),
  )
  return items
}

/** Detection class palette (DESIGN.md section 2): legible on video, used by boxes and chips. */
export const CLASS_COLORS: Record<string, string> = {
  person: '#38BDF8',
  car: '#A3E635',
  truck: '#FB923C',
  bus: '#F472B6',
  motorcycle: '#22D3EE',
  bicycle: '#2DD4BF',
  plate: '#FACC15',
  face: '#E879F9',
  face_known: '#4ADE80',
  text: '#C4B5FD',
}

function hashHue(s: string): number {
  let h = 0
  for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) >>> 0
  return h % 360
}

export function colorFor(it: Pick<DrawItem, 'kind' | 'key'>): string {
  if (it.kind === 'object') return CLASS_COLORS[it.key] ?? `hsl(${hashHue(it.key)} 85% 62%)`
  return CLASS_COLORS[it.kind] ?? CLASS_COLORS.text
}

/** Plates and text are "read" results: dashed stroke as a colour-blind-safe cue. */
export const isDashed = (it: Pick<DrawItem, 'kind'>) => it.kind === 'plate' || it.kind === 'text'

/** Label text on the pill; known faces get a check glyph (shape redundancy). */
export const pillText = (it: Pick<DrawItem, 'kind' | 'label'>) =>
  it.kind === 'face_known' ? `✓ ${it.label}` : it.label

/** Object labels are hidden on boxes narrower than 140 px; read results always keep theirs. */
export const showLabel = (it: Pick<DrawItem, 'kind' | 'rect'>, canvasW: number) =>
  canvasW > 160 && (it.kind !== 'object' || it.rect.w >= 140)

/** AgeChip tone: <5 s neutral, 5-15 s warn, >15 s err. */
export function ageTone(ageMs: number): 'neutral' | 'warn' | 'err' {
  return ageMs < 5000 ? 'neutral' : ageMs <= 15000 ? 'warn' : 'err'
}
