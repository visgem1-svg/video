import { useMemo, useState } from 'react'
import { ArrowRightLeft, ChevronDown, Pin, PinOff, RotateCw, Server } from 'lucide-react'
import { StatusDot } from '@/components/StatusDot'
import { Badge, Button, Card, CardHeader } from '@/components/ui/primitives'
import { assignedOf, type AssignedCamera, type OpsWorker } from '@/lib/api/operations'
import type { CameraConfig, CameraHealth } from '@/lib/types'
import { cn } from '@/lib/utils'
import { fmtLoad, utilTone } from './loadTone'
import { useOpsActions } from './useOpsActions'

const STATE_TONE = { live: 'success', draining: 'warning', dead: 'danger' } as const

export function WorkersPanel({
  workers,
  cameras,
  health,
  selectedWorker,
  onSelectCamera,
}: {
  workers: OpsWorker[]
  cameras: CameraConfig[]
  health: ReadonlyMap<string, CameraHealth>
  selectedWorker: string | null
  onSelectCamera?: (cameraId: string) => void
}) {
  const camById = useMemo(() => new Map(cameras.map((c) => [c.camera_id, c])), [cameras])
  const [open, setOpen] = useState<Set<string>>(new Set())
  const ops = useOpsActions(workers)

  const isOpen = (id: string) => open.has(id) || id === selectedWorker || workers.length <= 3
  const toggle = (id: string) =>
    setOpen((s) => {
      const n = new Set(s)
      if (!n.delete(id)) n.add(id)
      return n
    })

  return (
    <div className="flex flex-col gap-3">
      {workers.map((w) => {
        const cams = assignedOf(w)
        const load = w.load ?? cams.length
        const pct = w.capacity > 0 ? Math.round((load / w.capacity) * 100) : 0
        return (
          <Card key={w.worker_id} className={cn(selectedWorker === w.worker_id && 'ring-2 ring-primary')}>
            <CardHeader className="flex-wrap">
              <div className="flex min-w-0 flex-wrap items-center gap-2">
                <Server className="size-4 text-faint" aria-hidden />
                <span className="font-mono text-[13px] font-semibold">{w.worker_id}</span>
                <Badge tone={STATE_TONE[w.state]}>{w.state}</Badge>
                <Badge>{w.managed_by === 'controller' ? 'autoscaled' : 'static'}</Badge>
                {w.capacity_override != null && <Badge tone="outline">capacity override</Badge>}
              </div>
              <div className="flex items-center gap-3">
                <div className="w-40">
                  <div className="num mb-1 flex justify-between text-[11px] text-muted-foreground">
                    <span>
                      {fmtLoad(load)} / {w.capacity} · {cams.length} cams
                    </span>
                    <span>{pct}%</span>
                  </div>
                  <div className="h-1.5 overflow-hidden rounded-full bg-muted" aria-hidden>
                    <div className={cn('h-full rounded-full', utilTone(pct))} style={{ width: `${Math.min(100, pct)}%` }} />
                  </div>
                </div>
                <Button size="sm" variant="outline" onClick={() => ops.capacity(w)}>
                  Capacity
                </Button>
                {w.state === 'draining' ? (
                  <Button size="sm" variant="outline" onClick={() => ops.undrain(w)}>
                    Undrain
                  </Button>
                ) : (
                  <Button size="sm" variant="outline" disabled={w.state === 'dead'} onClick={() => ops.drain(w)}>
                    Drain
                  </Button>
                )}
                <Button
                  size="sm"
                  variant="ghost"
                  aria-expanded={isOpen(w.worker_id)}
                  aria-label={`${isOpen(w.worker_id) ? 'Hide' : 'Show'} cameras of ${w.worker_id}`}
                  onClick={() => toggle(w.worker_id)}
                >
                  <ChevronDown className={cn('transition-transform', isOpen(w.worker_id) && 'rotate-180')} />
                </Button>
              </div>
            </CardHeader>
            {isOpen(w.worker_id) && (
              <ul aria-label={`Cameras on ${w.worker_id}`}>
                {cams.length === 0 && (
                  <li className="px-4 py-3 text-[13px] text-muted-foreground">No cameras assigned.</li>
                )}
                {cams.map((a) => (
                  <CameraRow
                    key={a.camera_id}
                    a={a}
                    cam={camById.get(a.camera_id)}
                    h={health.get(a.camera_id)}
                    onOpen={onSelectCamera}
                    onMove={(name) => ops.move(a.camera_id, name, w.worker_id)}
                    onUnpin={() => ops.unpin(a.camera_id)}
                    onRestart={(name) => ops.restart(a.camera_id, name)}
                  />
                ))}
              </ul>
            )}
          </Card>
        )
      })}
      {ops.dialogs}
    </div>
  )
}

function CameraRow({
  a,
  cam,
  h,
  onOpen,
  onMove,
  onUnpin,
  onRestart,
}: {
  a: AssignedCamera
  cam: CameraConfig | undefined
  h: CameraHealth | undefined
  onOpen?: (cameraId: string) => void
  onMove: (name: string) => void
  onUnpin: () => void
  onRestart: (name: string) => void
}) {
  const name = cam?.name ?? a.camera_id
  const label = (
    <>
      {name} <span className="ml-1 font-mono text-xs text-muted-foreground">{a.camera_id}</span>
    </>
  )
  return (
    <li className="flex items-center gap-3 border-t px-4 py-2 text-[13px] first:border-t-0">
      <StatusDot state={h?.state} />
      {onOpen ? (
        <button
          type="button"
          className="min-w-0 flex-1 truncate text-left hover:underline"
          onClick={() => onOpen(a.camera_id)}
        >
          {label}
        </button>
      ) : (
        <span className="min-w-0 flex-1 truncate">{label}</span>
      )}
      {a.pinned && (
        <Badge tone="accent">
          <Pin className="size-3" aria-hidden /> pinned
        </Badge>
      )}
      <span className="num w-20 text-right text-xs text-muted-foreground">
        {h ? `${h.fps_actual.toFixed(1)} fps` : '- fps'}
      </span>
      {a.generation != null && a.generation > 0 && (
        <span className="num text-[11px] text-faint" title="Stream generation">
          gen {a.generation}
        </span>
      )}
      <div className="flex gap-1">
        <Button size="sm" variant="ghost" onClick={() => onMove(name)} aria-label={`Move ${name} to worker`} title="Move to worker...">
          <ArrowRightLeft />
        </Button>
        <Button size="sm" variant="ghost" disabled={!a.pinned} onClick={onUnpin} aria-label={`Unpin ${name}`} title="Unpin">
          <PinOff />
        </Button>
        <Button size="sm" variant="ghost" onClick={() => onRestart(name)} aria-label={`Restart stream of ${name}`} title="Restart stream">
          <RotateCw />
        </Button>
      </div>
    </li>
  )
}
