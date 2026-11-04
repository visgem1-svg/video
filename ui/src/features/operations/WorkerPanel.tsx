import { Pin } from 'lucide-react'
import { StatusDot } from '@/components/StatusDot'
import { Sheet } from '@/components/ui/sheet'
import { Badge, Button, EmptyState } from '@/components/ui/primitives'
import { assignedOf, type OpsWorker } from '@/lib/api/operations'
import { useHealthMap } from '@/lib/realtime/stores'
import { useNow } from '@/lib/realtime/useNow'
import type { CameraConfig } from '@/lib/types'
import { cn, formatAge } from '@/lib/utils'
import { fmtLoad, utilTone } from './loadTone'
import type { OpsActions } from './useOpsActions'

const STATE_TONE = { live: 'success', draining: 'warning', dead: 'danger' } as const

/** Side panel for one worker: facts, drain / capacity actions, and its cameras. */
export function WorkerPanel({
  worker,
  cameras,
  ops,
  onSelectCamera,
  onClose,
}: {
  worker: OpsWorker | undefined
  cameras: CameraConfig[]
  ops: OpsActions
  onSelectCamera: (id: string) => void
  onClose: () => void
}) {
  const now = useNow()
  const health = useHealthMap()
  const names = new Map(cameras.map((c) => [c.camera_id, c.name]))
  const cams = worker ? assignedOf(worker) : []
  const load = worker?.load ?? cams.length
  const pct = worker && worker.capacity > 0 ? Math.round((load / worker.capacity) * 100) : 0
  return (
    <Sheet
      open
      onOpenChange={(o) => !o && onClose()}
      title={worker?.worker_id ?? 'Worker'}
      description="Frame-extractor worker"
      actions={
        worker && (
          <div className="mt-1 flex flex-wrap items-center gap-1.5">
            <Badge tone={STATE_TONE[worker.state]}>{worker.state}</Badge>
            <Badge>{worker.managed_by === 'controller' ? 'autoscaled' : 'static'}</Badge>
            {worker.capacity_override != null && <Badge tone="outline">capacity override</Badge>}
          </div>
        )
      }
    >
      {!worker ? (
        <EmptyState title="Worker not found" hint="It may have been removed." className="py-8" />
      ) : (
        <div className="min-h-0 flex-1 overflow-y-auto p-4">
          <div className="num mb-1 flex justify-between text-xs text-muted-foreground">
            <span>
              <b className="text-foreground">{fmtLoad(load)}</b> / {worker.capacity} load · {cams.length} cameras
            </span>
            <span>{pct}%</span>
          </div>
          <div className="h-1.5 overflow-hidden rounded-full bg-muted" aria-hidden>
            <div className={cn('h-full rounded-full', utilTone(pct))} style={{ width: `${Math.min(100, pct)}%` }} />
          </div>
          <div className="mt-2 text-xs text-muted-foreground">
            Last heartbeat {formatAge(now - worker.last_seen)} ago
          </div>

          <div className="mt-4 flex flex-wrap gap-2">
            <Button size="sm" variant="outline" onClick={() => ops.capacity(worker)}>
              Capacity
            </Button>
            {worker.state === 'draining' ? (
              <Button size="sm" variant="outline" onClick={() => ops.undrain(worker)}>
                Undrain
              </Button>
            ) : (
              <Button size="sm" variant="outline" disabled={worker.state === 'dead'} onClick={() => ops.drain(worker)}>
                Drain
              </Button>
            )}
          </div>

          <h3 className="mt-5 mb-1 text-[11px] font-semibold tracking-wide text-faint uppercase">
            Cameras ({cams.length})
          </h3>
          {cams.length === 0 ? (
            <p className="text-[13px] text-muted-foreground">No cameras assigned.</p>
          ) : (
            <ul className="rounded-lg border">
              {cams.map((a) => {
                const h = health.get(a.camera_id)
                return (
                  <li key={a.camera_id} className="border-t first:border-t-0">
                    <button
                      type="button"
                      onClick={() => onSelectCamera(a.camera_id)}
                      className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-[13px] hover:bg-muted"
                    >
                      <StatusDot state={h?.state} />
                      <span className="min-w-0 flex-1 truncate">{names.get(a.camera_id) ?? a.camera_id}</span>
                      {a.pinned && <Pin className="size-3 text-primary" aria-label="pinned" />}
                      <span className="num text-xs text-muted-foreground">
                        {h ? `${h.fps_actual.toFixed(1)} fps` : '-'}
                      </span>
                    </button>
                  </li>
                )
              })}
            </ul>
          )}
        </div>
      )}
    </Sheet>
  )
}
