import { useEffect, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import { toast } from '@/lib/toast'
import { RefreshCw, X, AlertTriangle, CheckCircle2, Loader2, Play, Sparkles } from 'lucide-react'
import {
  Badge,
  Button,
  Card,
  CardBody,
  CardHeader,
  CardTitle,
  EmptyState,
  Label,
  PageHeader,
  Tabs,
  Textarea,
} from '@/components/ui/primitives'
import {
  keys,
  useActivatePlan,
  useCameras,
  useDraftPlan,
  useRecentDrafts,
  usePlans,
} from '@/lib/api/queries'
import { hub } from '@/lib/realtime/hub'
import { isPlanDeleted, type DraftResponse, type Plan } from '@/lib/types'
import { cn } from '@/lib/utils'
import { formatElapsed, useElapsed } from './useElapsed'
import { CameraPicker } from './CameraPicker'
import { PipelineWatch } from './PipelineWatch'
import { PlanGraph } from './PlanGraph'

const EXAMPLES = [
  'Read license plates of trucks entering the gates',
  'Identify known people in the lobby',
  'Read text on containers when a truck is present',
]

function Builder({ onActivated }: { onActivated: (id: string) => void }) {
  const { data: cameras = [] } = useCameras()
  const [query, setQuery] = useState('')
  const [selected, setSelected] = useState<string[]>([])
  const [draft, setDraft] = useState<DraftResponse | null>(null)
  const draftMut = useDraftPlan()
  const activate = useActivatePlan()

  const abortRef = useRef<AbortController | null>(null)
  const [cancelled, setCancelled] = useState(false)
  const busy = draftMut.isPending || activate.isPending
  const elapsed = useElapsed(busy)

  const recent = useRecentDrafts()
  const suggestions = recent.data?.length ? recent.data : EXAMPLES

  const run = (refresh = false) => {
    const ctrl = new AbortController()
    abortRef.current = ctrl
    setCancelled(false)
    draftMut.mutate(
      { req: { query: query.trim(), camera_ids: selected }, signal: ctrl.signal, refresh },
      { onSuccess: (d) => setDraft({ ...d, plan: { ...d.plan, camera_ids: selected } }) },
    )
  }
  const cancel = () => {
    setCancelled(true)
    abortRef.current?.abort()
  }
  const isAbort = (e: unknown) => (e as Error)?.name === 'AbortError'

  return (
    <div className="grid gap-4 xl:grid-cols-[420px_minmax(0,1fr)]">
      <Card>
        <CardHeader>
          <CardTitle>Describe what to detect</CardTitle>
        </CardHeader>
        <CardBody className="space-y-3">
          <Textarea
            placeholder="e.g. read license plates of trucks entering gate 3"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            aria-label="Pipeline query"
            maxLength={1000}
          />
          <div className="flex flex-wrap gap-1.5">
            {suggestions.map((e) => (
              <button
                key={e}
                onClick={() => setQuery(e)}
                className="inline-flex h-6 items-center gap-1 rounded-full border bg-muted px-2.5 text-xs text-muted-foreground transition-colors hover:text-foreground"
              >
                {e}
              </button>
            ))}
          </div>
          <div>
            <Label className="mb-1 block">Cameras</Label>
            <CameraPicker cameras={cameras} value={selected} onChange={setSelected} />
          </div>
          <Button
            className="w-full"
            disabled={query.trim().length < 3 || draftMut.isPending}
            onClick={() => run()}
          >
            {draftMut.isPending ? <Loader2 className="animate-spin" /> : <Sparkles />} Draft plan
          </Button>
          {draftMut.isError && !isAbort(draftMut.error) && (
            <p className="text-sm text-destructive">Draft failed: {String(draftMut.error)}</p>
          )}
        </CardBody>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Draft</CardTitle>
          {draft && (
            <Badge tone={draft.valid ? 'success' : 'danger'}>
              {draft.valid ? 'valid' : 'invalid'}
            </Badge>
          )}
        </CardHeader>
        <CardBody className="space-y-3">
          {draftMut.isPending && (
            <div
              role="status"
              className="flex items-center gap-3 rounded-md border border-primary/30 bg-primary/10 p-3 text-sm"
            >
              <Loader2 className="size-5 shrink-0 animate-spin text-primary" />
              <div className="flex-1">
                <div className="font-medium">
                  Drafting plan… <span className="tabular-nums">{formatElapsed(elapsed)}</span>
                </div>
                <div className="text-xs text-muted-foreground">
                  Local LLM, this can take a few minutes.
                </div>
              </div>
              <Button variant="outline" size="sm" onClick={cancel}>
                <X /> Cancel
              </Button>
            </div>
          )}
          {cancelled && !draftMut.isPending && (
            <p className="text-sm text-muted-foreground">Draft cancelled.</p>
          )}
          {!draft ? (
            <EmptyState
              icon={<Sparkles className="size-8" />}
              title="No draft yet"
              hint="The planner turns your request into a step graph. Nothing runs until you activate it."
            />
          ) : (
            <>
              <div className="flex flex-wrap items-center gap-2">
                <h3 className="text-base font-semibold">{draft.plan.name}</h3>
                {draft.cached && (
                  <Badge tone="info">From cache · used {draft.cache_hits ?? 0} times</Badge>
                )}
                {draft.cached && (
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={draftMut.isPending}
                    onClick={() => run(true)}
                  >
                    <RefreshCw /> Regenerate
                  </Button>
                )}
              </div>
              <p className="text-sm text-muted-foreground">{draft.explanation}</p>
              {draft.errors.length > 0 && (
                <ul
                  className="space-y-1 rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm"
                  role="alert"
                >
                  {draft.errors.map((e) => (
                    <li key={e} className="flex gap-2">
                      <AlertTriangle className="mt-0.5 size-4 shrink-0 text-destructive" />
                      {e}
                    </li>
                  ))}
                </ul>
              )}
              <PlanGraph plan={draft.plan} />
              <div className="flex items-center justify-between">
                <span className="text-xs text-muted-foreground">
                  {selected.length} camera(s) will be assigned this plan
                </span>
                <div className="flex gap-2">
                  <Button variant="ghost" onClick={() => setDraft(null)}>
                    Discard
                  </Button>
                  <Button
                    disabled={!draft.valid || selected.length === 0 || activate.isPending}
                    onClick={() =>
                      activate.mutate(
                        { ...draft.plan, camera_ids: selected },
                        {
                          onSuccess: (p) => {
                            toast.success('Pipeline activated', p.name)
                            setDraft(null)
                            onActivated(p.plan_id)
                          },
                          onError: (e) => toast.error('Activation failed', String(e)),
                        },
                      )
                    }
                  >
                    {activate.isPending ? <Loader2 className="animate-spin" /> : <Play />} Activate
                  </Button>
                </div>
              </div>
              {draft.valid && selected.length === 0 && (
                <p className="text-xs text-warning">Select at least one camera to activate.</p>
              )}
              {activate.isError && (
                <p className="text-sm text-destructive">
                  Activation failed: {String(activate.error)}
                </p>
              )}
            </>
          )}
        </CardBody>
      </Card>
    </div>
  )
}

function Active({
  selectedId,
  onSelect,
}: {
  selectedId: string | null
  onSelect: (id: string | null) => void
}) {
  const { data: plans = [], isLoading } = usePlans()
  const selected = plans.find((p) => p.plan_id === selectedId) ?? null
  return (
    <div className="grid gap-4 xl:grid-cols-[320px_minmax(0,1fr)]">
      <Card className="h-fit">
        <CardHeader>
          <CardTitle>Active plans</CardTitle>
          <span className="text-xs text-muted-foreground">{plans.length}</span>
        </CardHeader>
        <ul className="divide-y">
          {isLoading && <li className="p-4 text-sm text-muted-foreground">Loading…</li>}
          {plans.map((p) => (
            <li key={p.plan_id}>
              <button
                onClick={() => onSelect(p.plan_id)}
                className={cn(
                  'flex w-full flex-col gap-0.5 px-4 py-3 text-left hover:bg-accent',
                  p.plan_id === selectedId && 'bg-accent',
                )}
              >
                <span className="flex items-center gap-2 text-sm font-medium">
                  {p.name}
                  <Badge tone={p.status === 'active' ? 'success' : 'neutral'}>{p.status}</Badge>
                </span>
                <span className="text-xs text-muted-foreground">
                  {p.steps.length} steps · {p.camera_ids.length} cameras · v{p.version}
                </span>
              </button>
            </li>
          ))}
          {!isLoading && plans.length === 0 && (
            <li>
              <EmptyState
                title="No active plans"
                hint="Cameras use the default detect_objects plan."
                className="py-8"
              />
            </li>
          )}
        </ul>
      </Card>
      {selected ? (
        <PipelineWatch key={selected.plan_id} plan={selected} onDeleted={() => onSelect(null)} />
      ) : (
        <Card>
          <EmptyState
            icon={<CheckCircle2 className="size-8" />}
            title="Select a plan to watch"
            hint="Live per-step throughput, skips, errors and latency."
          />
        </Card>
      )}
    </div>
  )
}

export default function PipelinesPage() {
  const [params, setParams] = useSearchParams()
  const qc = useQueryClient()
  const planId = params.get('plan')
  const [tab, setTab] = useState<'build' | 'active'>(planId ? 'active' : 'build')
  const { data: plans = [] } = usePlans()

  useEffect(
    () =>
      hub.on('planChanged', (p) => {
        if (isPlanDeleted(p))
          qc.setQueryData<Plan[]>(keys.plans, (old) => old?.filter((x) => x.plan_id !== p.plan_id))
        else void qc.invalidateQueries({ queryKey: keys.plans })
      }),
    [qc],
  )
  // default selection: first plan once the Active tab is shown
  useEffect(() => {
    if (tab === 'active' && !planId && plans.length)
      setParams({ plan: plans[0].plan_id }, { replace: true })
  }, [tab, planId, plans, setParams])

  const select = (id: string | null) => setParams(id ? { plan: id } : {}, { replace: true })

  return (
    <div>
      <PageHeader
        title="Pipelines"
        description="Describe a goal, review the generated graph, activate it and watch it run."
      />
      <div className="mb-4">
        <Tabs
          value={tab}
          onChange={setTab}
          options={[
            { value: 'build', label: 'New pipeline' },
            { value: 'active', label: `Active plans (${plans.length})` },
          ]}
        />
      </div>
      {tab === 'build' ? (
        <Builder
          onActivated={(id) => {
            select(id)
            setTab('active')
          }}
        />
      ) : (
        <Active selectedId={planId} onSelect={select} />
      )}
    </div>
  )
}
