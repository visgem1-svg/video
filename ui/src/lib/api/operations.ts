import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { API_BASE } from '../config'
import { MockOps } from '../mock/operations'
import { ApiError } from './types'
import { isMock } from './runtime'
import type { CameraConfig } from '../types'

// ---------- types (contract 9.5) ----------
export type WorkerState = 'live' | 'draining' | 'dead'
export type ManagedBy = 'static' | 'controller'

export interface AssignedCamera {
  camera_id: string
  generation?: number
  pinned?: boolean
}
export interface OpsWorker {
  worker_id: string
  capacity: number
  capacity_override: number | null
  /** weighted load (v1.8: full camera = 1, idle camera = 0.1); older registries omit it */
  load?: number | null
  draining: boolean
  state: WorkerState
  managed_by: ManagedBy
  last_seen: number
  assigned: Array<AssignedCamera | CameraConfig | string>
}

export interface AutoscalerPolicy {
  min_workers: number
  max_workers: number
  target_utilization: number
  /** cameras per worker (API field name, contract 9.5) */
  worker_capacity: number
  cooldown_s: number
  drain_timeout_s: number
}
export interface AutoscalerEvent {
  ts: number
  action: string
  worker_id: string | null
  reason: string
}
export interface AutoscalerState {
  mode: 'auto' | 'manual'
  policy: AutoscalerPolicy
  current: number
  desired: number
  workers: Array<string | { worker_id: string }>
  events: AutoscalerEvent[]
}

/** Normalise the three shapes `assigned` may take into camera ids (+ pin/generation when known). */
export function assignedOf(w: OpsWorker): AssignedCamera[] {
  return w.assigned.map((a) =>
    typeof a === 'string'
      ? { camera_id: a }
      : {
          camera_id: a.camera_id,
          generation: (a as AssignedCamera).generation,
          pinned: (a as AssignedCamera).pinned,
        },
  )
}

// ---------- http ----------
export async function http<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, {
    ...init,
    headers: {
      Accept: 'application/json',
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...init?.headers,
    },
  })
  if (!res.ok) {
    let detail = res.statusText
    try {
      const b = (await res.json()) as Record<string, unknown>
      detail = String(b.detail ?? b.error ?? b.message ?? detail)
    } catch {
      /* not json */
    }
    throw new ApiError(`${res.status} ${detail}`.trim(), res.status)
  }
  if (res.status === 204) return undefined as T
  const text = await res.text()
  return (text ? JSON.parse(text) : undefined) as T
}
const enc = encodeURIComponent
const body = (v: unknown) => JSON.stringify(v)

const mockOps = new MockOps()

export const opsApi = {
  listWorkers: (): Promise<OpsWorker[]> =>
    isMock() ? mockOps.listWorkers() : http('/api/workers'),
  drain: (id: string): Promise<void> =>
    isMock() ? mockOps.drain(id, true) : http(`/api/workers/${enc(id)}/drain`, { method: 'POST' }),
  undrain: (id: string): Promise<void> =>
    isMock()
      ? mockOps.drain(id, false)
      : http(`/api/workers/${enc(id)}/undrain`, { method: 'POST' }),
  setCapacity: (id: string, capacity: number | null): Promise<void> =>
    isMock()
      ? mockOps.setCapacity(id, capacity)
      : http(`/api/workers/${enc(id)}/capacity`, { method: 'PUT', body: body({ capacity }) }),
  pin: (cameraId: string, workerId: string | null): Promise<void> =>
    isMock()
      ? mockOps.pin(cameraId, workerId)
      : http(`/api/cameras/${enc(cameraId)}/pin`, {
          method: 'POST',
          body: body({ worker_id: workerId }),
        }),
  restart: (cameraId: string): Promise<void> =>
    isMock()
      ? mockOps.restart(cameraId)
      : http(`/api/cameras/${enc(cameraId)}/restart`, { method: 'POST' }),
  autoscaler: (): Promise<AutoscalerState> =>
    isMock() ? mockOps.autoscaler() : http('/api/autoscaler'),
  setPolicy: (p: AutoscalerPolicy): Promise<void> =>
    isMock()
      ? mockOps.setPolicy(p)
      : http('/api/autoscaler/policy', { method: 'PUT', body: body(p) }),
  setMode: (mode: 'auto' | 'manual'): Promise<void> =>
    isMock()
      ? mockOps.setMode(mode)
      : http('/api/autoscaler/mode', { method: 'PUT', body: body({ mode }) }),
  scale: (workers: number): Promise<void> =>
    isMock()
      ? mockOps.scale(workers)
      : http('/api/autoscaler/scale', { method: 'POST', body: body({ workers }) }),
}

// ---------- hooks ----------
export const OPS_POLL_MS = 5_000
export const opsKeys = { workers: ['ops-workers'] as const, autoscaler: ['autoscaler'] as const }

export const useOpsWorkers = () =>
  useQuery({
    queryKey: opsKeys.workers,
    queryFn: () => opsApi.listWorkers(),
    refetchInterval: OPS_POLL_MS,
  })
export const useAutoscaler = () =>
  useQuery({
    queryKey: opsKeys.autoscaler,
    queryFn: () => opsApi.autoscaler(),
    refetchInterval: OPS_POLL_MS,
    retry: 1,
  })

export type WorkerPatch =
  | { type: 'drain'; id: string; on: boolean }
  | { type: 'capacity'; id: string; capacity: number | null }
  | { type: 'pin'; cameraId: string; workerId: string | null }

/** Pure cache patch used for optimistic updates (exported for tests). */
export function patchWorkers(list: OpsWorker[], op: WorkerPatch): OpsWorker[] {
  switch (op.type) {
    case 'drain':
      return list.map((w) =>
        w.worker_id !== op.id || w.state === 'dead'
          ? w
          : { ...w, draining: op.on, state: op.on ? 'draining' : 'live' },
      )
    case 'capacity':
      return list.map((w) =>
        w.worker_id !== op.id
          ? w
          : { ...w, capacity_override: op.capacity, capacity: op.capacity ?? w.capacity },
      )
    case 'pin': {
      if (!op.workerId) {
        return list.map((w) => ({
          ...w,
          assigned: w.assigned.map((a) =>
            typeof a !== 'string' && a.camera_id === op.cameraId ? { ...a, pinned: false } : a,
          ),
        }))
      }
      let moved: AssignedCamera = { camera_id: op.cameraId, pinned: true }
      const stripped = list.map((w) => ({
        ...w,
        assigned: w.assigned.filter((a) => {
          const id = typeof a === 'string' ? a : a.camera_id
          if (id !== op.cameraId) return true
          moved = {
            ...(typeof a === 'string' ? { camera_id: a } : (a as AssignedCamera)),
            pinned: true,
          }
          return false
        }),
      }))
      return stripped.map((w) =>
        w.worker_id === op.workerId ? { ...w, assigned: [...w.assigned, moved] } : w,
      )
    }
  }
}

/** Optimistic mutation helper: patch cache, roll back on error, refetch when settled. */
function useOptimistic<V>(call: (v: V) => Promise<unknown>, toOp: ((v: V) => WorkerPatch) | null) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: call,
    onMutate: async (v: V) => {
      if (!toOp) return { prev: undefined }
      await qc.cancelQueries({ queryKey: opsKeys.workers })
      const prev = qc.getQueryData<OpsWorker[]>(opsKeys.workers)
      if (prev) qc.setQueryData(opsKeys.workers, patchWorkers(prev, toOp(v)))
      return { prev }
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.prev) qc.setQueryData(opsKeys.workers, ctx.prev)
    },
    onSettled: () => {
      void qc.invalidateQueries({ queryKey: opsKeys.workers })
      void qc.invalidateQueries({ queryKey: opsKeys.autoscaler })
    },
  })
}

export const useDrainWorker = () =>
  useOptimistic<{ id: string; on: boolean }>(
    ({ id, on }) => (on ? opsApi.drain(id) : opsApi.undrain(id)),
    ({ id, on }) => ({ type: 'drain', id, on }),
  )
export const useSetCapacity = () =>
  useOptimistic<{ id: string; capacity: number | null }>(
    ({ id, capacity }) => opsApi.setCapacity(id, capacity),
    ({ id, capacity }) => ({ type: 'capacity', id, capacity }),
  )
export const usePinCamera = () =>
  useOptimistic<{ cameraId: string; workerId: string | null }>(
    ({ cameraId, workerId }) => opsApi.pin(cameraId, workerId),
    ({ cameraId, workerId }) => ({ type: 'pin', cameraId, workerId }),
  )
export const useRestartCamera = () => useOptimistic<string>((id) => opsApi.restart(id), null)
export const useSetPolicy = () => useOptimistic<AutoscalerPolicy>((p) => opsApi.setPolicy(p), null)
export const useSetMode = () => useOptimistic<'auto' | 'manual'>((m) => opsApi.setMode(m), null)
export const useScale = () => useOptimistic<number>((n) => opsApi.scale(n), null)
