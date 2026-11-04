import { api } from '../api/runtime'
import type { FrameQuery, FrameRecord, FrameStat } from '../api/frames'
import type { Box, DetFace, DetObject, DetPlate } from '../types'
import { ApiError } from '../api/types'

const W = 1280
const H = 720
const RETENTION = 10 * 60_000
const RING = 600
const LAG_MS = 250
const CLASSES = ['person', 'car', 'truck', 'bus'] as const

function hash(s: string): number {
  let h = 2166136261
  for (let i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 16777619) >>> 0
  return h
}
const frac = (x: number) => x - Math.floor(x)
const fpsOf = (cam: string) => 2 + (hash(cam) % 3) // 2..4 fps
const offsetOf = (cam: string) => hash(cam + 'o') % 1000
const intervalOf = (cam: string) => 1000 / fpsOf(cam)

/** Deterministic, slowly moving scene objects for a camera at a time (drives both image and boxes). */
export function sceneObjects(cam: string, ts: number): DetObject[] {
  const h = hash(cam)
  const n = 1 + (h % 3) + (Math.floor(ts / 20_000 + h) % 2)
  const out: DetObject[] = []
  for (let i = 0; i < n; i++) {
    const cls = CLASSES[(h + i * 7) % CLASSES.length]
    const bw = cls === 'person' ? 110 : cls === 'car' ? 260 : 340
    const bh = cls === 'person' ? 280 : cls === 'car' ? 150 : 200
    const cx =
      W * (0.15 + 0.7 * frac(i * 0.37 + (h % 97) / 97)) + 120 * Math.sin(ts / 4000 + i * 1.7 + h)
    const cy = H * (0.45 + 0.2 * frac(i * 0.61 + (h % 53) / 53)) + 30 * Math.sin(ts / 3000 + i)
    const x1 = Math.max(0, Math.min(W - bw, cx - bw / 2))
    const y1 = Math.max(0, Math.min(H - bh, cy - bh / 2))
    out.push({
      class: cls,
      confidence: 0.62 + 0.36 * frac(i * 0.29 + ts / 9000 + (h % 11) / 11),
      box: [Math.round(x1), Math.round(y1), Math.round(x1 + bw), Math.round(y1 + bh)] as Box,
    })
  }
  return out
}

function detailsOf(cam: string, ts: number) {
  const objects = sceneObjects(cam, ts)
  const plates: DetPlate[] = objects
    .filter((o) => o.class === 'car')
    .map((o, i) => ({
      plate: `${10 + (hash(cam) % 80)}-${100 + ((hash(cam + i) >> 3) % 800)}-${10 + (i % 80)}`,
      confidence: 0.9,
      box: [o.box[0] + 70, o.box[3] - 40, o.box[0] + 190, o.box[3] - 10] as Box,
    }))
  const faces: DetFace[] = objects
    .filter((o) => o.class === 'person')
    .map((o, i) => ({
      box: [o.box[0] + 25, o.box[1] + 8, o.box[0] + 85, o.box[1] + 75] as Box,
      confidence: 0.88,
      person_id: i % 2 ? null : 'p-dana',
      person_name: i % 2 ? null : 'Dana Levi',
      score: 0.8,
    }))
  return { objects, plates, faces }
}

function frameAt(cam: string, seq: number, nowMs: number): FrameRecord {
  const ts = Math.round(seq * intervalOf(cam) + offsetOf(cam))
  const age = nowMs - ts
  const analyzed = age > 1500 && seq % 5 !== 0
  const d = detailsOf(cam, ts)
  const counts: Record<string, number> = {}
  if (analyzed) for (const o of d.objects) counts[o.class] = (counts[o.class] ?? 0) + 1
  return {
    frame_id: `${cam}-${seq}`,
    camera_id: cam,
    ts,
    seq,
    width: W,
    height: H,
    worker_id: ['fx-1', 'fx-2', 'fx-auto-1'][Math.min(2, (Number(cam.split('-')[1]) || 0) % 3)],
    analyzed,
    objects: counts,
    plates: analyzed ? d.plates : [],
    faces: analyzed ? d.faces : [],
    texts: [],
    total_ms: analyzed ? Math.round(60 + 80 * frac(seq * 0.137)) : null,
  }
}

const newestSeq = (cam: string, now: number) =>
  Math.floor((now - LAG_MS - offsetOf(cam)) / intervalOf(cam))

const parseId = (cam: string, id: string) => {
  const seq = Number(id.slice(cam.length + 1))
  return Number.isFinite(seq) ? seq : null
}

const esc = (s: string) => s.replace(/[<>&"]/g, '')
const imgCache = new Map<string, string>()

function svgImage(f: Pick<FrameRecord, 'camera_id' | 'frame_id' | 'ts'>): string {
  const cached = imgCache.get(f.frame_id)
  if (cached) return cached
  const hue = hash(f.camera_id) % 360
  const t = new Date(f.ts)
  const clock = `${t.toLocaleTimeString([], { hour12: false })}.${String(f.ts % 1000).padStart(3, '0')}`
  const shapes = sceneObjects(f.camera_id, f.ts)
    .map((o) => {
      const [x1, y1, x2, y2] = o.box
      const fill = o.class === 'person' ? '#64748b' : '#475569'
      return `<rect x="${x1}" y="${y1}" width="${x2 - x1}" height="${y2 - y1}" rx="${o.class === 'person' ? 40 : 18}" fill="${fill}" opacity=".85"/>`
    })
    .join('')
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}"><defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="hsl(${hue} 30% 22%)"/><stop offset="1" stop-color="hsl(${hue} 25% 10%)"/></linearGradient></defs><rect width="${W}" height="${H}" fill="url(#g)"/><rect y="${H * 0.62}" width="${W}" height="${H * 0.38}" fill="hsl(${hue} 15% 14%)"/>${shapes}<text x="28" y="56" font-family="monospace" font-size="38" fill="#e2e8f0">${esc(f.camera_id)}</text><text x="28" y="${H - 28}" font-family="monospace" font-size="38" fill="#cbd5e1">${clock}</text></svg>`
  const uri = `data:image/svg+xml;utf8,${encodeURIComponent(svg)}`
  if (imgCache.size > 500) imgCache.delete(imgCache.keys().next().value as string)
  imgCache.set(f.frame_id, uri)
  return uri
}

export const mockFrames = {
  async list(q: FrameQuery): Promise<FrameRecord[]> {
    const now = Date.now()
    const cam = q.camera_id
    const newest = newestSeq(cam, now)
    const oldest = newest - RING + 1
    const iv = intervalOf(cam)
    let start = newest
    if (q.before != null) start = Math.min(start, Math.ceil((q.before - offsetOf(cam)) / iv) - 1)
    const out: FrameRecord[] = []
    const limit = Math.min(q.limit ?? 60, 200)
    for (let s = start; s >= oldest && out.length < limit; s--) out.push(frameAt(cam, s, now))
    return out
  },
  async get(cam: string, id: string): Promise<FrameRecord> {
    const seq = parseId(cam, id)
    if (seq == null || seq > newestSeq(cam, Date.now())) throw new ApiError('404 not found', 404)
    const now = Date.now()
    const rec = frameAt(cam, seq, now)
    // detail carries boxes for the objects
    return { ...rec, objects: rec.analyzed ? detailsOf(cam, rec.ts).objects : [] }
  },
  async stats(): Promise<FrameStat[]> {
    const cams = await api.listCameras()
    const now = Date.now()
    return cams
      .filter((c) => c.enabled)
      .map((c) => {
        const newest = newestSeq(c.camera_id, now)
        return {
          camera_id: c.camera_id,
          frames: RING,
          analyzed: Math.round(RING * 0.78),
          oldest_ts: frameAt(c.camera_id, newest - RING + 1, now).ts,
          newest_ts: frameAt(c.camera_id, newest, now).ts,
          fps: fpsOf(c.camera_id) + (hash(c.camera_id + 'f') % 10) / 100,
        }
      })
  },
  imageUrl(f: Pick<FrameRecord, 'camera_id' | 'frame_id' | 'ts'>): string {
    // older than the bucket retention: behave like the 404 (undecodable payload -> img onError)
    if (Date.now() - f.ts > RETENTION) return 'data:image/jpeg;base64,AAAA'
    return svgImage(f)
  },
}
