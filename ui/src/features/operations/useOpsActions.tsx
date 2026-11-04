import { useState, type ReactNode } from 'react'
import { Dialog, DialogContent, DialogFooter } from '@/components/ui/dialog'
import { Dropdown } from '@/components/ui/dropdown'
import { Button, Input, Label } from '@/components/ui/primitives'
import {
  useDrainWorker,
  usePinCamera,
  useRestartCamera,
  useSetCapacity,
  type OpsWorker,
} from '@/lib/api/operations'
import { toast } from '@/lib/toast'
import { ConfirmDialog } from './ConfirmDialog'

const errMsg = (e: unknown) => (e instanceof Error ? e.message : 'Request failed')

type Pending =
  | { type: 'drain'; worker: OpsWorker }
  | { type: 'restart'; cameraId: string; name: string }
  | { type: 'move'; cameraId: string; name: string; from: string | null }
  | { type: 'capacity'; worker: OpsWorker }
  | null

export interface OpsActions {
  /** ask to drain (confirm dialog) */
  drain: (w: OpsWorker) => void
  undrain: (w: OpsWorker) => void
  capacity: (w: OpsWorker) => void
  restart: (cameraId: string, name: string) => void
  /** open the "pin to worker" dialog */
  move: (cameraId: string, name: string, from: string | null) => void
  unpin: (cameraId: string) => void
  /** the dialogs; render once next to the buttons that call the actions */
  dialogs: ReactNode
}

/**
 * Every operator action on workers and cameras (drain, capacity, restart, pin) with its confirm
 * dialogs and toasts. Shared by the workers list and the Operations side panel.
 */
export function useOpsActions(workers: OpsWorker[]): OpsActions {
  const [pending, setPending] = useState<Pending>(null)
  const [target, setTarget] = useState('')
  const [capText, setCapText] = useState('')
  const drain = useDrainWorker()
  const pin = usePinCamera()
  const restart = useRestartCamera()
  const capacity = useSetCapacity()
  const close = () => setPending(null)

  const doDrain = (w: OpsWorker, on: boolean) =>
    drain.mutate(
      { id: w.worker_id, on },
      {
        onSuccess: () =>
          toast.success(on ? `Draining ${w.worker_id}` : `${w.worker_id} accepts cameras again`),
        onError: (e) => toast.error(`Could not ${on ? 'drain' : 'undrain'} ${w.worker_id}`, errMsg(e)),
      },
    )
  const liveTargets = (from: string | null) =>
    workers.filter((w) => w.state === 'live' && w.worker_id !== from)
  const capInvalid = capText !== '' && !(Number.isInteger(Number(capText)) && Number(capText) >= 1)

  const dialogs = (
    <>
      <ConfirmDialog
        open={pending?.type === 'drain'}
        onOpenChange={(o) => !o && close()}
        title={pending?.type === 'drain' ? `Drain ${pending.worker.worker_id}?` : 'Drain'}
        description="No new cameras are assigned and its cameras move to other live workers. Streams restart briefly."
        confirmLabel="Drain worker"
        destructive
        onConfirm={() => {
          if (pending?.type === 'drain') doDrain(pending.worker, true)
          close()
        }}
      />
      <ConfirmDialog
        open={pending?.type === 'restart'}
        onOpenChange={(o) => !o && close()}
        title={pending?.type === 'restart' ? `Restart stream of ${pending.name}?` : 'Restart'}
        description="The extractor restarts this camera's ffmpeg. Frames pause for a few seconds."
        confirmLabel="Restart stream"
        destructive
        onConfirm={() => {
          if (pending?.type === 'restart') {
            const { cameraId } = pending
            restart.mutate(cameraId, {
              onSuccess: () => toast.success(`Restarting ${cameraId}`),
              onError: (e) => toast.error(`Could not restart ${cameraId}`, errMsg(e)),
            })
          }
          close()
        }}
      />

      <Dialog open={pending?.type === 'move'} onOpenChange={(o) => !o && close()}>
        <DialogContent
          title={pending?.type === 'move' ? `Move ${pending.name}` : 'Move camera'}
          description="Pins the camera to a worker. The rebalancer keeps it there while that worker is live."
        >
          <Label>Target worker</Label>
          <Dropdown
            className="mt-1 w-full"
            ariaLabel="Target worker"
            value={target}
            onChange={setTarget}
            placeholder="Choose a worker"
            options={
              pending?.type === 'move'
                ? liveTargets(pending.from).map((w) => ({ value: w.worker_id, label: w.worker_id }))
                : []
            }
          />
          <DialogFooter>
            <Button variant="ghost" onClick={close}>
              Cancel
            </Button>
            <Button
              disabled={!target}
              onClick={() => {
                if (pending?.type === 'move') {
                  const { cameraId } = pending
                  pin.mutate(
                    { cameraId, workerId: target },
                    {
                      onSuccess: () => toast.success(`Pinned ${cameraId} to ${target}`),
                      onError: (e) => toast.error(`Could not move ${cameraId}`, errMsg(e)),
                    },
                  )
                }
                close()
              }}
            >
              Move and pin
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog open={pending?.type === 'capacity'} onOpenChange={(o) => !o && close()}>
        <DialogContent
          title={pending?.type === 'capacity' ? `Capacity of ${pending.worker.worker_id}` : 'Capacity'}
          description="Maximum cameras this worker takes. Leave empty to use the default."
        >
          <Label htmlFor="cap">Cameras</Label>
          <Input
            id="cap"
            className="mt-1"
            inputMode="numeric"
            value={capText}
            onChange={(e) => setCapText(e.target.value)}
            aria-invalid={capInvalid}
            placeholder="default"
          />
          <DialogFooter>
            <Button variant="ghost" onClick={close}>
              Cancel
            </Button>
            <Button
              disabled={capInvalid}
              onClick={() => {
                if (pending?.type === 'capacity') {
                  const id = pending.worker.worker_id
                  const value = capText === '' ? null : Number(capText)
                  capacity.mutate(
                    { id, capacity: value },
                    {
                      onSuccess: () =>
                        toast.success(value == null ? `${id} capacity reset` : `${id} capacity set to ${value}`),
                      onError: (e) => toast.error(`Could not change capacity of ${id}`, errMsg(e)),
                    },
                  )
                }
                close()
              }}
            >
              Save
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  )

  return {
    drain: (worker) => setPending({ type: 'drain', worker }),
    undrain: (w) => doDrain(w, false),
    capacity: (worker) => {
      setCapText(worker.capacity_override != null ? String(worker.capacity_override) : '')
      setPending({ type: 'capacity', worker })
    },
    restart: (cameraId, name) => setPending({ type: 'restart', cameraId, name }),
    move: (cameraId, name, from) => {
      setTarget(liveTargets(from)[0]?.worker_id ?? '')
      setPending({ type: 'move', cameraId, name, from })
    },
    unpin: (cameraId) =>
      pin.mutate(
        { cameraId, workerId: null },
        {
          onSuccess: () => toast.success(`Unpinned ${cameraId}`),
          onError: (e) => toast.error('Could not unpin', errMsg(e)),
        },
      ),
    dialogs,
  }
}
