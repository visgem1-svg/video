const env = import.meta.env

export const API_BASE: string = (env.VITE_API_BASE ?? 'http://localhost:8000').replace(/\/+$/, '')

function flag(v: unknown): boolean {
  return v === '1' || v === 'true' || v === true
}

/** Mock mode forced by build/dev env (VITE_MOCK=1) or by `?mock=1` in the URL. */
export const MOCK_FORCED: boolean =
  flag(env.VITE_MOCK) ||
  (typeof location !== 'undefined' && new URLSearchParams(location.search).get('mock') === '1')

/** Max simultaneous WebRTC sessions in one browser tab. */
export const MAX_WEBRTC_SESSIONS = 16

export const whepUrl = (cameraId: string) =>
  `${API_BASE}/media/${encodeURIComponent(cameraId)}/whep`
