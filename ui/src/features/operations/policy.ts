import type { AutoscalerPolicy } from '@/lib/api/operations'

export type PolicyErrors = Partial<Record<keyof AutoscalerPolicy, string>>
/** What the form holds: every field as text; utilization is shown as a percentage. */
export type PolicyDraft = Record<keyof AutoscalerPolicy, string>

const int = (n: number) => Number.isInteger(n)

/** API policy -> form text (target_utilization 0..1 -> percent). */
export const policyToDraft = (p: AutoscalerPolicy): PolicyDraft => ({
  min_workers: String(p.min_workers),
  max_workers: String(p.max_workers),
  target_utilization: String(Math.round(p.target_utilization * 100)),
  worker_capacity: String(p.worker_capacity),
  cooldown_s: String(p.cooldown_s),
  drain_timeout_s: String(p.drain_timeout_s),
})

/** Form text -> API policy (the PUT body); empty fields become NaN so validation says "Required". */
export function draftToPolicy(d: PolicyDraft): AutoscalerPolicy {
  const n = (s: string) => (s.trim() === '' ? NaN : Number(s))
  return {
    min_workers: n(d.min_workers),
    max_workers: n(d.max_workers),
    target_utilization: n(d.target_utilization) / 100,
    worker_capacity: n(d.worker_capacity),
    cooldown_s: n(d.cooldown_s),
    drain_timeout_s: n(d.drain_timeout_s),
  }
}

/** Validate an autoscaler policy (ranges as enforced by worker-controller); field -> message, {} = valid. */
export function validatePolicy(p: AutoscalerPolicy): PolicyErrors {
  const e: PolicyErrors = {}
  if (!Number.isFinite(p.min_workers)) e.min_workers = 'Required'
  else if (!int(p.min_workers) || p.min_workers < 0) e.min_workers = 'Whole number, 0 or more'
  if (!Number.isFinite(p.max_workers)) e.max_workers = 'Required'
  else if (!int(p.max_workers) || p.max_workers < 1 || p.max_workers > 1000)
    e.max_workers = 'Whole number, 1 to 1000'
  else if (!e.min_workers && p.max_workers < p.min_workers) e.max_workers = 'Must be at least the minimum'
  if (!Number.isFinite(p.target_utilization)) e.target_utilization = 'Required'
  else if (p.target_utilization < 0.05 || p.target_utilization > 1)
    e.target_utilization = 'Between 5% and 100%'
  if (!Number.isFinite(p.worker_capacity)) e.worker_capacity = 'Required'
  else if (!int(p.worker_capacity) || p.worker_capacity < 1 || p.worker_capacity > 1000)
    e.worker_capacity = 'Whole number, 1 to 1000'
  if (!Number.isFinite(p.cooldown_s)) e.cooldown_s = 'Required'
  else if (p.cooldown_s < 0 || p.cooldown_s > 86400) e.cooldown_s = 'Seconds, 0 to 86400'
  if (!Number.isFinite(p.drain_timeout_s)) e.drain_timeout_s = 'Required'
  else if (p.drain_timeout_s < 1 || p.drain_timeout_s > 86400) e.drain_timeout_s = 'Seconds, 1 to 86400'
  return e
}

/** Same formula as the controller (contract 9.5), minus the unassigned bump. */
export function desiredWorkers(enabledCameras: number, p: AutoscalerPolicy): number {
  const raw = Math.ceil(enabledCameras / (p.worker_capacity * p.target_utilization))
  return Math.max(p.min_workers, Math.min(p.max_workers, raw))
}
