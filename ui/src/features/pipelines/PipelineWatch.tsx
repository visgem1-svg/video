import { memo, useEffect, useMemo, useState } from 'react'
import { Line, LineChart, ResponsiveContainer, Tooltip, YAxis } from 'recharts'
import { Trash2 } from 'lucide-react'
import { Dialog, DialogClose, DialogContent, DialogFooter } from '@/components/ui/dialog'
import { toast } from '@/lib/toast'
import { Badge, Button, Card, CardBody, CardHeader, CardTitle } from '@/components/ui/primitives'
import { useDeletePlan } from '@/lib/api/queries'
import { hub } from '@/lib/realtime/hub'
import { metricKey, useMetrics } from '@/lib/realtime/stores'
import type { PipelineMetrics, Plan } from '@/lib/types'
import { formatDateTime } from '@/lib/utils'
import { PlanGraph, TOOL_ICON } from './PlanGraph'

const Spark = memo(function Spark({
  data,
  field,
  label,
  color,
}: {
  data: PipelineMetrics[]
  field: 'rate' | 'p95_ms'
  label: string
  color: string
}) {
  const points = useMemo(
    () =>
      data.map((m) => ({
        t: m.ts,
        rate: +(m.processed / m.window_s).toFixed(2),
        p95_ms: m.p95_ms,
      })),
    [data],
  )
  return (
    <div>
      <div className="text-[10px] text-muted-foreground">{label}</div>
      <div className="h-12">
        <ResponsiveContainer width="100%" height="100%">
          <LineChart data={points} margin={{ top: 4, right: 2, bottom: 2, left: 2 }}>
            <YAxis hide domain={['dataMin', 'dataMax']} />
            <Tooltip
              contentStyle={{
                background: 'var(--card)',
                border: '1px solid var(--border)',
                fontSize: 11,
                padding: '2px 6px',
              }}
              labelFormatter={(t) => new Date(Number(t)).toLocaleTimeString()}
              formatter={(v) => [String(v), label]}
            />
            <Line
              type="monotone"
              dataKey={field}
              stroke={color}
              strokeWidth={1.75}
              dot={false}
              isAnimationActive={false}
            />
          </LineChart>
        </ResponsiveContainer>
      </div>
    </div>
  )
})

/** Live per-node metrics (hub `pipelineMetrics`) on the graph + sparklines per step. */
export function PipelineWatch({ plan, onDeleted }: { plan: Plan; onDeleted?: () => void }) {
  const store = useMetrics()
  const del = useDeletePlan()
  const [confirm, setConfirm] = useState(false)

  useEffect(() => hub.subscribePlan(plan.plan_id), [plan.plan_id])

  const history = useMemo(
    () =>
      Object.fromEntries(
        plan.steps.map((s) => [s.id, store.get(metricKey(plan.plan_id, s.id)) ?? []]),
      ),
    [store, plan],
  )
  const latest = useMemo(
    () => Object.fromEntries(plan.steps.map((s) => [s.id, history[s.id].at(-1)])),
    [history, plan],
  )

  return (
    <Card>
      <CardHeader>
        <div className="min-w-0">
          <CardTitle className="truncate">{plan.name}</CardTitle>
          <div className="text-xs text-muted-foreground">
            v{plan.version} · {plan.camera_ids.length} cameras · {formatDateTime(plan.created_at)}
          </div>
        </div>
        <div className="flex items-center gap-2">
          <Badge tone="success">{plan.status}</Badge>
          <Button
            variant="outline"
            size="sm"
            disabled={del.isPending}
            onClick={() => setConfirm(true)}
          >
            <Trash2 /> Delete
          </Button>
        </div>
      </CardHeader>
      <CardBody className="space-y-4">
        <p className="text-sm text-muted-foreground">“{plan.query}”</p>
        <PlanGraph plan={plan} metrics={latest} className="h-[340px]" />
        <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
          {plan.steps.map((s) => {
            const Icon = TOOL_ICON[s.tool]
            const h = history[s.id]
            return (
              <div key={s.id} className="rounded-lg border p-3">
                <div className="mb-1 flex items-center gap-2 text-sm font-medium">
                  <Icon className="size-4 text-primary" /> {s.id} · {s.tool}
                </div>
                {h.length === 0 ? (
                  <div className="py-3 text-xs text-muted-foreground">
                    Waiting for metrics (5 s windows)…
                  </div>
                ) : (
                  <div className="grid grid-cols-2 gap-3">
                    <Spark data={h} field="rate" label="processed/s" color="var(--primary)" />
                    <Spark data={h} field="p95_ms" label="p95 ms" color="var(--warning)" />
                  </div>
                )}
              </div>
            )
          })}
        </div>
      </CardBody>
      <Dialog open={confirm} onOpenChange={setConfirm}>
        <DialogContent
          title={`Delete "${plan.name}"?`}
          description={`The plan is removed and its ${plan.camera_ids.length} camera(s) go back to the default detect_objects plan.`}
        >
          <DialogFooter>
            <DialogClose asChild>
              <Button variant="ghost">Cancel</Button>
            </DialogClose>
            <Button
              variant="destructive"
              disabled={del.isPending}
              onClick={() =>
                del.mutate(plan.plan_id, {
                  onSuccess: () => {
                    toast.success('Pipeline deleted', plan.name)
                    setConfirm(false)
                    onDeleted?.()
                  },
                  onError: (e) => toast.error('Could not delete pipeline', String(e)),
                })
              }
            >
              Delete pipeline
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Card>
  )
}
