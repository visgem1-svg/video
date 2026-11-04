import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { API_BASE } from '../config'
import { mockFrames } from '../mock/frames'
import type { DetFace, DetObject, DetPlate, DetText } from '../types'
import { http } from './operations'
import { isMock } from './runtime'

// ---------- types (contract 9.5 Frame catalog) ----------
export interface FrameRecord {
  frame_id: string
  camera_id: string
  ts: number
  seq: number
  width: number
  height: number
  worker_id: string
  analyzed: boolean
  /** list: {class: count}; detail (GET one): boxes. Both are handled. */
  objects: Record<string, number> | DetObject[]
  plates: DetPlate[]
  faces: DetFace[]
  texts: DetText[]
  total_ms: number | null
}
export interface FrameStat {
  camera_id: string
  frames: number
  analyzed: number
  oldest_ts: number | null
  newest_ts: number | null
  fps: number
}
export interface FrameQuery {
  camera_id: string
  before?: number
  limit?: number
}

/** 10 min bucket retention: older images 404. */
export const FRAME_RETENTION_MS = 10 * 60_000

export const framesApi = {
  list: (q: FrameQuery): Promise<FrameRecord[]> => {
    if (isMock()) return mockFrames.list(q)
    const p = new URLSearchParams({ camera_id: q.camera_id, limit: String(q.limit ?? 60) })
    if (q.before != null) p.set('before', String(q.before))
    // frame-catalog answers { items, count, next_before }; older shapes kept for the mock
    return http<FrameRecord[] | { items?: FrameRecord[]; frames?: FrameRecord[] }>(
      `/api/frames?${p}`,
    ).then((r) => (Array.isArray(r) ? r : (r.items ?? r.frames ?? [])))
  },
  get: (cam: string, id: string): Promise<FrameRecord> =>
    isMock()
      ? mockFrames.get(cam, id)
      : http(`/api/frames/${encodeURIComponent(cam)}/${encodeURIComponent(id)}`),
  stats: (): Promise<FrameStat[]> =>
    isMock()
      ? mockFrames.stats()
      : http<FrameStat[] | { cameras: FrameStat[] }>('/api/frames/stats').then((r) =>
          Array.isArray(r) ? r : (r.cameras ?? []),
        ),
}

/** Image URL for a frame (JPEG from the gateway; an SVG stand-in in mock mode). */
export function frameImageUrl(f: Pick<FrameRecord, 'camera_id' | 'frame_id' | 'ts'>): string {
  if (isMock()) return mockFrames.imageUrl(f)
  return `${API_BASE}/api/frames/${encodeURIComponent(f.camera_id)}/${encodeURIComponent(f.frame_id)}/image`
}

export const useFrameStats = () =>
  useQuery({
    queryKey: ['frame-stats'],
    queryFn: () => framesApi.stats(),
    refetchInterval: 5_000,
    placeholderData: keepPreviousData,
  })

/** Full record (with boxes) of one frame; only fetched when the analysis overlay is on. */
export const useFrameDetail = (cam: string | null, id: string | null, enabled: boolean) =>
  useQuery({
    queryKey: ['frame', cam, id],
    queryFn: () => framesApi.get(cam!, id!),
    enabled: enabled && !!cam && !!id,
    staleTime: 60_000,
  })

/** Latest extracted frame of one camera (grid tiles). Polls only while `active`. */
export const useLatestFrame = (cam: string, active: boolean, everyMs = 1_500) =>
  useQuery({
    queryKey: ['latest-frame', cam],
    queryFn: async () => (await framesApi.list({ camera_id: cam, limit: 1 }))[0] ?? null,
    enabled: active,
    refetchInterval: active ? everyMs : false,
    placeholderData: keepPreviousData,
  })
