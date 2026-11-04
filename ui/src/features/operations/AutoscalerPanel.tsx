import { useEffect, useMemo, useState } from 'react'
import { Gauge } from 'lucide-react'
import {
  Badge,
  Button,
  Card,
  CardBody,
  CardHeader,
  CardTitle,
  ErrorState,
  Input,
  Label,
  Segmented,
  Skeleton,
} from '@/components/ui/primitives'
import {
  useAutoscaler,
  useScale,
  useSetMode,
  useSetPolicy,
  type AutoscalerPolicy,
} from '@/lib/api/operations'
import { toast } from '@/lib/toast'
import { formatTime } from '@/lib/utils'
import { draftToPolicy, policyToDraft, validatePolicy, type PolicyDraft } from './policy'

const errMsg = (e: unknown) => (e instanceof Error ? e.message : 'Request failed')

const FIELDS: Array<{ key: keyof AutoscalerPolicy; label: string; unit?: string }> = [
  { key: 'min_workers', label: 'Min workers' },
  { key: 'max_workers', label: 'Max workers' },
  { key: 'target_utilization', label: 'Target utilization', unit: '%' },
  { key: 'worker_capacity', label: 'Capacity per worker', unit: 'cameras' },
  { key: 'cooldown_s', label: 'Cooldown', unit: 's' },
  { key: 'drain_timeout_s', label: 'Drain timeout', unit: 's' },
]

const ACTION_TONE: Record<string, 'success' | 'warning' | 'neutral' | 'danger'> = {
  scale_up: 'success',
  scale_down: 'warning',
  drain: 'warning',
}

export function AutoscalerPanel() {
  const q = useAutoscaler()
  const setPolicy = useSetPolicy()
  const setMode = useSetMode()
  const scale = useScale()
  const data = q.data
  const [draft, setDraft] = useState<PolicyDraft | null>(null)
  const [manual, setManual] = useState('')

  // adopt server policy until the user starts editing
  const serverKey = data ? JSON.stringify(data.policy) : ''
  const [editing, setEditing] = useState(false)
  useEffect(() => {
    if (data && !editing) setDraft(policyToDraft(data.policy))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [serverKey, editing])
  useEffect(() => {
    if (data) setManual((m) => (m === '' ? String(data.desired) : m))
  }, [data])

  const errors = useMemo(() => (draft ? validatePolicy(draftToPolicy(draft)) : {}), [draft])
  const valid = Object.keys(errors).length === 0
  const dirty = !!data && !!draft && JSON.stringify(policyToDraft(data.policy)) !== JSON.stringify(draft)

  if (q.isLoading) return <Skeleton className="h-96 w-full" />
  if (q.isError || !data || !draft)
    return (
      <ErrorState
        message={`Autoscaler unavailable: ${errMsg(q.error)}. The worker controller may not be running.`}
        onRetry={() => void q.refetch()}
      />
    )
  const manualMode = data.mode === 'manual'
  const manualN = Number(manual)
  const manualOk =
    Number.isInteger(manualN) && manualN >= data.policy.min_workers && manualN <= data.policy.max_workers

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Gauge className="size-4 text-faint" aria-hidden /> Autoscaler
        </CardTitle>
        <Segmented<'auto' | 'manual'>
          value={data.mode}
          onChange={(m) =>
            setMode.mutate(m, {
              onSuccess: () => toast.success(`Autoscaler set to ${m}`),
              onError: (e) => toast.error('Could not change mode', errMsg(e)),
            })
          }
          options={[
            { value: 'auto', label: 'Auto' },
            { value: 'manual', label: 'Manual' },
          ]}
        />
      </CardHeader>
      <CardBody className="space-y-5">
        <div className="grid grid-cols-2 gap-3">
          <div className="rounded-lg border p-3">
            <div className="text-xs text-muted-foreground">Current workers</div>
            <div className="num text-[28px] leading-8 font-semibold tracking-[-0.02em]">{data.current}</div>
          </div>
          <div className="rounded-lg border p-3">
            <div className="text-xs text-muted-foreground">Desired workers</div>
            <div className="num text-[28px] leading-8 font-semibold tracking-[-0.02em]">
              {data.desired}
              {data.desired !== data.current && (
                <span className="ml-2 text-xs font-normal text-warn">
                  {data.desired > data.current ? 'scaling up' : 'scaling down'}
                </span>
              )}
            </div>
          </div>
        </div>

        <form
          className="space-y-3"
          onSubmit={(e) => {
            e.preventDefault()
            if (!valid) return
            setPolicy.mutate(draftToPolicy(draft), {
              onSuccess: () => {
                setEditing(false)
                toast.success('Policy saved')
              },
              onError: (e2) => toast.error('Could not save policy', errMsg(e2)),
            })
          }}
        >
          <div className="text-[13px] font-semibold">Policy</div>
          <div className="grid grid-cols-2 gap-3">
            {FIELDS.map((f) => (
              <div key={f.key}>
                <Label htmlFor={`pol-${f.key}`}>
                  {f.label}
                  {f.unit ? ` (${f.unit})` : ''}
                </Label>
                <Input
                  id={`pol-${f.key}`}
                  className="mt-1"
                  inputMode="decimal"
                  value={draft[f.key]}
                  aria-invalid={!!errors[f.key]}
                  aria-describedby={errors[f.key] ? `err-${f.key}` : undefined}
                  onChange={(e) => {
                    setEditing(true)
                    setDraft({ ...draft, [f.key]: e.target.value })
                  }}
                />
                {errors[f.key] && (
                  <p id={`err-${f.key}`} className="mt-1 text-xs text-err">
                    {errors[f.key]}
                  </p>
                )}
              </div>
            ))}
          </div>
          <div className="flex justify-end gap-2">
            <Button
              type="button"
              variant="ghost"
              disabled={!dirty}
              onClick={() => {
                setEditing(false)
                setDraft(policyToDraft(data.policy))
              }}
            >
              Reset
            </Button>
            <Button type="submit" disabled={!valid || !dirty || setPolicy.isPending}>
              Save policy
            </Button>
          </div>
        </form>

        <div className="space-y-2 border-t pt-4">
          <div className="text-[13px] font-semibold">Manual scale</div>
          <div className="flex items-end gap-2">
            <div className="w-28">
              <Label htmlFor="manual-n">Workers</Label>
              <Input
                id="manual-n"
                className="mt-1"
                inputMode="numeric"
                disabled={!manualMode}
                value={manual}
                aria-invalid={manualMode && !manualOk}
                onChange={(e) => setManual(e.target.value)}
              />
            </div>
            <Button
              disabled={!manualMode || !manualOk || scale.isPending}
              onClick={() =>
                scale.mutate(manualN, {
                  onSuccess: () => toast.success(`Scaling to ${manualN} workers`),
                  onError: (e) => toast.error('Could not scale', errMsg(e)),
                })
              }
            >
              Apply
            </Button>
          </div>
          <p className="text-xs text-muted-foreground">
            {manualMode
              ? `Between ${data.policy.min_workers} and ${data.policy.max_workers} workers.`
              : 'Switch to Manual to set the worker count yourself.'}
          </p>
        </div>

        <div className="border-t pt-4">
          <div className="mb-2 text-[13px] font-semibold">Events</div>
          {data.events.length === 0 ? (
            <p className="text-[13px] text-muted-foreground">No scaling events yet.</p>
          ) : (
            <div className="max-h-56 overflow-auto rounded-lg border">
              <table className="w-full text-left text-xs">
                <thead className="sticky top-0 bg-card text-muted-foreground">
                  <tr>
                    <th className="px-2 py-1.5 font-medium">Time</th>
                    <th className="px-2 py-1.5 font-medium">Action</th>
                    <th className="px-2 py-1.5 font-medium">Worker</th>
                    <th className="px-2 py-1.5 font-medium">Reason</th>
                  </tr>
                </thead>
                <tbody>
                  {data.events.map((e, i) => (
                    <tr key={`${e.ts}-${i}`} className="border-t align-top">
                      <td className="num px-2 py-1.5 font-mono whitespace-nowrap">{formatTime(e.ts)}</td>
                      <td className="px-2 py-1.5">
                        <Badge tone={ACTION_TONE[e.action] ?? 'neutral'}>{e.action}</Badge>
                      </td>
                      <td className="px-2 py-1.5 font-mono">{e.worker_id ?? '-'}</td>
                      <td className="px-2 py-1.5 text-muted-foreground">{e.reason}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </CardBody>
    </Card>
  )
}
