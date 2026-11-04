import { useEffect, useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import {
  Copy,
  Pencil,
  Plus,
  Search,
  Server,
  Trash2,
  TriangleAlert,
  Video,
  Workflow,
} from 'lucide-react'
import { StatusDot } from '@/components/StatusDot'
import { Dialog, DialogClose, DialogContent, DialogFooter, Switch } from '@/components/ui/dialog'
import {
  Badge,
  Button,
  Card,
  CardBody,
  CardHeader,
  CardTitle,
  EmptyState,
  ErrorState,
  Input,
  PageHeader,
  Pagination,
  Skeleton,
} from '@/components/ui/primitives'
import { useCameras, useDeleteCamera, usePlans, useSaveCamera, useWorkers } from '@/lib/api/queries'
import { useHealthMap } from '@/lib/realtime/stores'
import { useNow } from '@/lib/realtime/useNow'
import { toast } from '@/lib/toast'
import { toCameraInput, type CameraConfig, type Worker } from '@/lib/types'
import { cn, formatAge } from '@/lib/utils'
import { CameraDialog } from './CameraDialog'

const PAGE = 25
/** never show camera credentials in the table */
const CREDENTIALS = /\/\/[^/@\s]*@/
const maskUrl = (u: string) => u.replace(CREDENTIALS, '//***@')
const assignedId = (a: CameraConfig | string) => (typeof a === 'string' ? a : a.camera_id)

/** Load bar colour: ok below 70 %, warn to 90 %, err above. */
export const loadTone = (ratio: number) =>
  ratio > 0.9 ? 'bg-err' : ratio > 0.7 ? 'bg-warn' : 'bg-ok'

function WorkersPanel({ workers }: { workers: Worker[] }) {
  const now = useNow()
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Server className="size-4" aria-hidden /> Workers
        </CardTitle>
        <span className="num text-xs text-muted-foreground">{workers.length}</span>
      </CardHeader>
      <CardBody className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        {workers.length === 0 && (
          <div className="col-span-full text-[13px] text-muted-foreground">
            No workers have sent a heartbeat yet.
          </div>
        )}
        {workers.map((w) => {
          const used = w.assigned.length
          const ratio = Math.min(1, used / Math.max(1, w.capacity))
          const stale = now - w.last_seen > 15_000
          return (
            <div key={w.worker_id} className="rounded-lg border p-3">
              <div className="flex items-center justify-between">
                <span className="flex items-center gap-2 font-mono text-xs font-medium">
                  <StatusDot state={stale ? 'offline' : 'online'} /> {w.worker_id}
                </span>
                <span className="text-xs text-muted-foreground">{stale ? 'stale' : 'alive'}</span>
              </div>
              <div className="mt-2 h-1 overflow-hidden rounded bg-muted">
                <div
                  className={cn('h-full', loadTone(ratio))}
                  style={{ width: `${ratio * 100}%` }}
                />
              </div>
              <div className="num mt-1.5 flex justify-between text-xs text-muted-foreground">
                <span>
                  {used} / {w.capacity} cameras
                </span>
                <span>seen {formatAge(now - w.last_seen)} ago</span>
              </div>
            </div>
          )
        })}
      </CardBody>
    </Card>
  )
}

export default function SourcesPage() {
  const { data: cameras = [], isLoading, error, refetch } = useCameras()
  const { data: plans = [] } = usePlans()
  const { data: workers = [] } = useWorkers()
  const health = useHealthMap()
  const save = useSaveCamera()
  const del = useDeleteCamera()
  const [q, setQ] = useState('')
  const [page, setPage] = useState(0)
  const [editing, setEditing] = useState<CameraConfig | null>(null)
  const [dialogOpen, setDialogOpen] = useState(false)
  const [deleting, setDeleting] = useState<CameraConfig | null>(null)

  const workerOf = useMemo(() => {
    const m = new Map<string, string>()
    workers.forEach((w) => w.assigned.forEach((a) => m.set(assignedId(a), w.worker_id)))
    return m
  }, [workers])
  const planName = useMemo(() => new Map(plans.map((p) => [p.plan_id, p.name])), [plans])

  const filtered = useMemo(() => {
    const n = q.trim().toLowerCase()
    return n
      ? cameras.filter(
          (c) =>
            c.name.toLowerCase().includes(n) ||
            c.camera_id.includes(n) ||
            c.stream_url.toLowerCase().includes(n) ||
            c.tags.some((t) => t.includes(n)),
        )
      : cameras
  }, [cameras, q])
  const pages = Math.ceil(filtered.length / PAGE)
  const cur = Math.min(page, Math.max(0, pages - 1))
  const rows = filtered.slice(cur * PAGE, cur * PAGE + PAGE)

  // Deep link from health / ops / focus: /sources?edit=<camera_id> opens that camera's form.
  const [params, setParams] = useSearchParams()
  const editId = params.get('edit')
  useEffect(() => {
    if (!editId) return
    const c = cameras.find((x) => x.camera_id === editId)
    if (!c) return
    setEditing(c)
    setDialogOpen(true)
    setParams({}, { replace: true })
  }, [editId, cameras, setParams])

  const openAdd = () => {
    setEditing(null)
    setDialogOpen(true)
  }
  const openEdit = (c: CameraConfig) => {
    setEditing(c)
    setDialogOpen(true)
  }
  const stop = (e: React.SyntheticEvent) => e.stopPropagation()

  return (
    <div className="space-y-4">
      <PageHeader
        title="Sources"
        description="Camera registry. Changes are published to all workers."
        actions={
          <>
            <div className="relative">
              <Search className="absolute top-2.5 left-2.5 size-4 text-faint" aria-hidden />
              <Input
                className="w-56 pl-8"
                placeholder="Search name, id, URL, tag"
                value={q}
                onChange={(e) => {
                  setQ(e.target.value)
                  setPage(0)
                }}
                aria-label="Search cameras"
              />
            </div>
            <Button onClick={openAdd}>
              <Plus /> Add camera
            </Button>
          </>
        }
      />
      {error && (
        <ErrorState
          message={`Could not load cameras: ${String(error)}`}
          onRetry={() => void refetch()}
        />
      )}
      <Card className="overflow-hidden">
        <div className="overflow-auto">
          <table className="w-full min-w-[900px] text-[13px]">
            <thead className="sticky top-0 z-10 bg-card text-left text-xs text-muted-foreground">
              <tr className="border-b">
                <th className="w-8 py-2 pl-4" />
                <th className="px-3 py-2 font-medium">Camera</th>
                <th className="px-3 py-2 font-medium">Source</th>
                <th className="px-3 py-2 text-right font-medium">FPS (actual / target)</th>
                <th className="px-3 py-2 font-medium">Plan</th>
                <th className="px-3 py-2 font-medium">Worker</th>
                <th className="px-3 py-2 font-medium">Enabled</th>
                <th className="px-3 py-2" />
              </tr>
            </thead>
            <tbody>
              {isLoading &&
                Array.from({ length: 8 }, (_, i) => (
                  <tr key={i} className="h-11 border-b last:border-0">
                    <td colSpan={8} className="px-4">
                      <Skeleton className="h-5" />
                    </td>
                  </tr>
                ))}
              {rows.map((c) => {
                const h = health.get(c.camera_id)
                const slow = h && h.fps_target > 0 && h.fps_actual < 0.8 * h.fps_target
                return (
                  <tr
                    key={c.camera_id}
                    onClick={() => openEdit(c)}
                    className="group h-11 cursor-pointer border-b last:border-0 hover:bg-muted/60"
                  >
                    <td className="w-8 pl-4">
                      <StatusDot state={h?.state} />
                    </td>
                    <td className="px-3 py-1.5">
                      <div className="font-medium">{c.name}</div>
                      <div className="font-mono text-xs text-muted-foreground">
                        {c.camera_id}
                        {c.tags.length > 0 && ` · ${c.tags.join(', ')}`}
                      </div>
                      {c.duplicate_of && c.duplicate_of.length > 0 && (
                        <Badge
                          tone="warning"
                          className="mt-1"
                          title="Another camera pulls the same stream"
                        >
                          <Copy className="size-3" aria-hidden />
                          duplicate of {c.duplicate_of.join(', ')}
                        </Badge>
                      )}
                    </td>
                    <td
                      className="max-w-64 truncate px-3 font-mono text-xs text-muted-foreground"
                      title={maskUrl(c.stream_url)}
                    >
                      {maskUrl(c.stream_url)}
                      {c.provision_error && (
                        <div title={c.provision_error}>
                          <Badge tone="warning" className="mt-1 max-w-full">
                            <TriangleAlert className="size-3" aria-hidden />
                            <span className="truncate">provisioning: {c.provision_error}</span>
                          </Badge>
                        </div>
                      )}
                    </td>
                    <td className="num px-3 text-right">
                      <span className={slow ? 'text-warn' : ''}>
                        {h ? h.fps_actual.toFixed(1) : '–'}
                      </span>{' '}
                      <span className="text-faint">/ {c.analysis_fps}</span>
                    </td>
                    <td className="px-3">
                      {c.plan_id ? (
                        <Badge>{planName.get(c.plan_id) ?? c.plan_id.slice(0, 8)}</Badge>
                      ) : (
                        <span className="text-muted-foreground">default</span>
                      )}
                    </td>
                    <td className="px-3">
                      {workerOf.get(c.camera_id) ? (
                        <Badge className="font-mono">{workerOf.get(c.camera_id)}</Badge>
                      ) : (
                        <span className="text-faint">–</span>
                      )}
                    </td>
                    <td className="px-3" onClick={stop}>
                      <Switch
                        checked={c.enabled}
                        aria-label={`Enable ${c.name}`}
                        disabled={save.isPending}
                        onCheckedChange={(v) =>
                          save.mutate(
                            { id: c.camera_id, camera: toCameraInput(c, { enabled: v }) },
                            {
                              onSuccess: () =>
                                toast.success(`${c.name} ${v ? 'enabled' : 'disabled'}`),
                              onError: (e) => toast.error('Could not update camera', String(e)),
                            },
                          )
                        }
                      />
                    </td>
                    <td
                      className="px-3 text-right opacity-50 transition-opacity group-hover:opacity-100 group-focus-within:opacity-100"
                      onClick={stop}
                    >
                      <Button variant="ghost" size="icon" asChild>
                        <Link
                          to={`/live/${c.camera_id}#pipeline`}
                          title="Watch pipeline"
                          aria-label={`Pipeline of ${c.name}`}
                        >
                          <Workflow />
                        </Link>
                      </Button>
                      <Button
                        variant="ghost"
                        size="icon"
                        aria-label={`Edit ${c.name}`}
                        onClick={() => openEdit(c)}
                      >
                        <Pencil />
                      </Button>
                      <Button
                        variant="ghost"
                        size="icon"
                        aria-label={`Delete ${c.name}`}
                        className="text-muted-foreground hover:text-err"
                        onClick={() => setDeleting(c)}
                      >
                        <Trash2 />
                      </Button>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
        {!isLoading && rows.length === 0 && (
          <EmptyState
            icon={<Video />}
            title="No cameras yet"
            hint="Add a camera to start streaming and analysing."
            action={
              <Button size="sm" onClick={openAdd}>
                <Plus /> Add camera
              </Button>
            }
          />
        )}
        <div className="flex justify-end border-t px-4 py-2">
          <Pagination
            page={cur}
            pages={pages}
            onChange={setPage}
            total={filtered.length}
            pageSize={PAGE}
          />
        </div>
      </Card>

      <WorkersPanel workers={workers} />

      <CameraDialog
        open={dialogOpen}
        onOpenChange={setDialogOpen}
        camera={editing}
        allCameras={cameras}
        plans={plans}
      />

      <Dialog open={!!deleting} onOpenChange={(o) => !o && setDeleting(null)}>
        {deleting && (
          <DialogContent
            title={`Delete ${deleting.name}?`}
            description="The camera is removed from the registry and its stream stops. This cannot be undone."
          >
            <DialogFooter>
              <DialogClose asChild>
                <Button variant="ghost">Cancel</Button>
              </DialogClose>
              <Button
                variant="destructive"
                disabled={del.isPending}
                onClick={() =>
                  del.mutate(deleting.camera_id, {
                    onSuccess: () => {
                      toast.success(`${deleting.name} deleted`)
                      setDeleting(null)
                    },
                    onError: (e) => toast.error('Could not delete camera', String(e)),
                  })
                }
              >
                Delete {deleting.name}
              </Button>
            </DialogFooter>
          </DialogContent>
        )}
      </Dialog>
    </div>
  )
}
