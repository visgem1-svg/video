import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { API_BASE } from '../config'
import type {
  CameraInput,
  DraftRequest,
  Plan,
  Recognition,
  RecognitionQuery,
  RecognitionRecord,
} from '../types'
import { mockSnapshot } from '../mock/snapshot'
import { api, isMock } from './runtime'

export const keys = {
  cameras: ['cameras'] as const,
  workers: ['workers'] as const,
  plans: ['plans'] as const,
  tools: ['tools'] as const,
  health: ['health'] as const,
  persons: ['persons'] as const,
  recentDrafts: ['recent-drafts'] as const,
  recognitions: (q: RecognitionQuery) => ['recognitions', q] as const,
}

export const useCameras = () =>
  useQuery({ queryKey: keys.cameras, queryFn: () => api.listCameras(), staleTime: 15_000 })
export const useWorkers = () =>
  useQuery({ queryKey: keys.workers, queryFn: () => api.listWorkers(), refetchInterval: 5_000 })
export const usePlans = () =>
  useQuery({ queryKey: keys.plans, queryFn: () => api.listPlans(), staleTime: 10_000 })
export const useTools = () =>
  useQuery({ queryKey: keys.tools, queryFn: () => api.listTools(), staleTime: Infinity })
export const useHealthSeed = () =>
  useQuery({ queryKey: keys.health, queryFn: () => api.healthCameras(), refetchInterval: 30_000 })

export const useRecognitionHistory = (q: RecognitionQuery) =>
  useQuery({
    queryKey: keys.recognitions(q),
    queryFn: () => api.listRecognitions(q),
    placeholderData: keepPreviousData,
    refetchInterval: 15_000,
  })

export function useSaveCamera() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, camera }: { id?: string; camera: CameraInput }) =>
      id ? api.updateCamera(id, camera) : api.createCamera(camera),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: keys.cameras })
      void qc.invalidateQueries({ queryKey: keys.workers })
      void qc.invalidateQueries({ queryKey: keys.health })
    },
  })
}
export function useDeleteCamera() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => api.deleteCamera(id),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: keys.cameras })
      void qc.invalidateQueries({ queryKey: keys.workers })
    },
  })
}
export const useDraftPlan = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({
      req,
      signal,
      refresh,
    }: {
      req: DraftRequest
      signal?: AbortSignal
      refresh?: boolean
    }) => api.draftPlan(req, signal, { refresh }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: keys.recentDrafts }),
  })
}
export const useRecentDrafts = () =>
  useQuery({ queryKey: keys.recentDrafts, queryFn: () => api.recentDrafts(10), staleTime: 30_000 })

export const usePersons = () =>
  useQuery({ queryKey: keys.persons, queryFn: () => api.listPersons(), staleTime: 10_000 })
function usePersonMutation<V, R>(fn: (v: V) => Promise<R>) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSuccess: () => void qc.invalidateQueries({ queryKey: keys.persons }),
  })
}
export const useEnrollPerson = () =>
  usePersonMutation(({ name, image }: { name: string; image: File }) =>
    api.enrollPerson(name, image),
  )
export const useAddPersonPhoto = () =>
  usePersonMutation(({ id, image }: { id: string; image: File }) => api.addPersonPhoto(id, image))
export const useRenamePerson = () =>
  usePersonMutation(({ id, name }: { id: string; name: string }) => api.renamePerson(id, name))
export const useDeletePerson = () => usePersonMutation((id: string) => api.deletePerson(id))
export function useActivatePlan() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (p: Plan) => api.activatePlan(p),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: keys.plans })
      void qc.invalidateQueries({ queryKey: keys.cameras })
    },
  })
}
export function useDeletePlan() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => api.deletePlan(id),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: keys.plans })
      void qc.invalidateQueries({ queryKey: keys.cameras })
    },
  })
}

const pad = (n: number) => String(n).padStart(2, '0')

/**
 * Image URL for a recognition. History rows carry `snapshot_url`; live hub events do not,
 * so we derive it from the 7.4 route /api/snapshots/{type}/{yyyyMMdd}/{id}.jpg (UTC date).
 */
export function snapshotSrc(r: Recognition | RecognitionRecord): string {
  if (isMock()) return (r as RecognitionRecord).snapshot_url ?? mockSnapshot(r)
  const url = (r as RecognitionRecord).snapshot_url
  if (url)
    return /^https?:|^data:/.test(url) ? url : `${API_BASE}${url.startsWith('/') ? '' : '/'}${url}`
  const d = new Date(r.ts)
  const day = `${d.getUTCFullYear()}${pad(d.getUTCMonth() + 1)}${pad(d.getUTCDate())}`
  return `${API_BASE}/api/snapshots/${r.type}/${day}/${r.recognition_id}.jpg`
}
