import { API_BASE } from '../config'
import type { Person, RecognitionQuery } from '../types'
import { ApiError, type RestApi } from './types'

/** Planner calls can take minutes with a local CPU LLM (contract 9.2). */
export const PLANNER_TIMEOUT_MS = 6 * 60_000

interface RequestOpts {
  signal?: AbortSignal
  timeoutMs?: number
}

async function request<T>(path: string, init?: RequestInit, opts: RequestOpts = {}): Promise<T> {
  const ctrl = new AbortController()
  let timedOut = false
  const forward = () => ctrl.abort()
  if (opts.signal?.aborted) ctrl.abort()
  else opts.signal?.addEventListener('abort', forward)
  const timer = opts.timeoutMs
    ? setTimeout(() => {
        timedOut = true
        ctrl.abort()
      }, opts.timeoutMs)
    : null
  try {
    const res = await fetch(`${API_BASE}${path}`, {
      ...init,
      signal: ctrl.signal,
      headers: {
        Accept: 'application/json',
        ...(init?.body && !(init.body instanceof FormData)
          ? { 'Content-Type': 'application/json' }
          : {}),
        ...init?.headers,
      },
    })
    if (!res.ok) {
      let body: unknown
      try {
        body = await res.json()
      } catch {
        /* not json */
      }
      const detail =
        body && typeof body === 'object'
          ? String(
              (body as Record<string, unknown>).detail ??
                (body as Record<string, unknown>).error ??
                (body as Record<string, unknown>).message ??
                res.statusText,
            )
          : res.statusText
      throw new ApiError(`${res.status} ${detail}`.trim(), res.status, body)
    }
    if (res.status === 204) return undefined as T
    const text = await res.text()
    return (text ? JSON.parse(text) : undefined) as T
  } catch (e) {
    if (timedOut)
      throw new ApiError(`Timed out after ${Math.round((opts.timeoutMs ?? 0) / 60000)} min`, 408)
    throw e
  } finally {
    if (timer) clearTimeout(timer)
    opts.signal?.removeEventListener('abort', forward)
  }
}

const enc = encodeURIComponent
const json = (v: unknown) => JSON.stringify(v)

export function qs(q: Record<string, string | number | undefined>): string {
  const p = new URLSearchParams()
  for (const [k, v] of Object.entries(q)) if (v !== undefined && v !== '') p.set(k, String(v))
  const s = p.toString()
  return s ? `?${s}` : ''
}

const absolutize = (u: string | undefined) =>
  !u ? '' : /^(https?:|data:)/.test(u) ? u : `${API_BASE}${u.startsWith('/') ? '' : '/'}${u}`
const withPhoto = (p: Person): Person => ({
  ...p,
  photo_url: absolutize(p.photo_url || `/api/persons/${p.person_id}/photo`),
})

export const httpBackend: RestApi = {
  listCameras: () => request('/api/cameras'),
  createCamera: (c) => request('/api/cameras', { method: 'POST', body: json(c) }),
  updateCamera: (id, c) => request(`/api/cameras/${enc(id)}`, { method: 'PUT', body: json(c) }),
  deleteCamera: (id) => request(`/api/cameras/${enc(id)}`, { method: 'DELETE' }),
  listWorkers: () => request('/api/workers'),

  healthCameras: () => request('/api/health/cameras'),
  healthSummary: () => request('/api/health/summary'),

  listRecognitions: (q: RecognitionQuery) => request(`/api/recognitions${qs({ ...q })}`),

  recentDrafts: async (limit = 10) => {
    const r = await request<unknown>(`/api/plans/drafts/recent?limit=${limit}`)
    const list = Array.isArray(r) ? r : ((r as { drafts?: unknown[] })?.drafts ?? [])
    return list
      .map((x) => (typeof x === 'string' ? x : String((x as { query?: string })?.query ?? '')))
      .filter(Boolean)
  },
  draftPlan: (req, signal, opts) =>
    request(
      `/api/plans/draft${opts?.refresh ? '?refresh=true' : ''}`,
      { method: 'POST', body: json(req) },
      { signal, timeoutMs: PLANNER_TIMEOUT_MS },
    ),
  activatePlan: (plan, signal) =>
    request(
      '/api/plans',
      { method: 'POST', body: json(plan) },
      { signal, timeoutMs: PLANNER_TIMEOUT_MS },
    ),
  listPlans: () => request('/api/plans'),
  getPlan: (id) => request(`/api/plans/${enc(id)}`),
  deletePlan: (id) => request(`/api/plans/${enc(id)}`, { method: 'DELETE' }),
  listTools: () => request('/api/tools'),

  listPersons: async () => {
    const r = await request<{ persons: Person[] } | Person[]>('/api/persons')
    const list = Array.isArray(r) ? r : (r?.persons ?? [])
    return list.map((p) => ({ ...p, photo_url: absolutize(p.photo_url) }))
  },
  enrollPerson: async (name, image) => {
    const fd = new FormData()
    fd.append('name', name)
    fd.append('image', image)
    return withPhoto(await request<Person>('/api/persons', { method: 'POST', body: fd }))
  },
  addPersonPhoto: async (id, image) => {
    const fd = new FormData()
    fd.append('image', image)
    return withPhoto(
      await request<Person>(`/api/persons/${enc(id)}/photos`, { method: 'POST', body: fd }),
    )
  },
  renamePerson: async (id, name) =>
    withPhoto(
      await request<Person>(`/api/persons/${enc(id)}`, { method: 'PUT', body: json({ name }) }),
    ),
  deletePerson: (id) => request(`/api/persons/${enc(id)}`, { method: 'DELETE' }),
}

/** Reachability probe used for the automatic mock fallback. */
export async function probeGateway(timeoutMs = 2500): Promise<boolean> {
  const ctrl = new AbortController()
  const t = setTimeout(() => ctrl.abort(), timeoutMs)
  try {
    const res = await fetch(`${API_BASE}/api/health/summary`, { signal: ctrl.signal })
    return res.status < 500
  } catch {
    return false
  } finally {
    clearTimeout(t)
  }
}
