import { useCallback, useMemo, useState } from 'react'
import { Server } from 'lucide-react'
import { EmptyState, ErrorState, PageHeader, Skeleton } from '@/components/ui/primitives'
import { useOpsWorkers } from '@/lib/api/operations'
import { useCameras } from '@/lib/api/queries'
import { useHealthMap } from '@/lib/realtime/stores'
import { cn } from '@/lib/utils'
import { AutoscalerPanel } from './AutoscalerPanel'
import { CameraPanel } from './CameraPanel'
import { FlowDiagram, type FlowSelection } from './FlowDiagram'
import { useOpsActions } from './useOpsActions'
import { WorkerPanel } from './WorkerPanel'
import { WorkersPanel } from './WorkersPanel'

type Selected = { type: 'camera' | 'worker'; id: string } | null

export default function OperationsPage() {
  const workers = useOpsWorkers()
  const cams = useCameras()
  const health = useHealthMap()
  const [selected, setSelected] = useState<Selected>(null)
  const [wide, setWide] = useState(false)
  const list = useMemo(() => workers.data ?? [], [workers.data])
  const cameras = useMemo(() => cams.data ?? [], [cams.data])
  const ops = useOpsActions(list)
  const live = list.filter((w) => w.state === 'live').length
  const assigned = list.reduce((n, w) => n + w.assigned.length, 0)

  const selectCamera = useCallback((id: string) => setSelected({ type: 'camera', id }), [])
  const selectWorker = useCallback((id: string) => setSelected({ type: 'worker', id }), [])
  const close = useCallback(() => setSelected(null), [])
  const selection = useMemo<FlowSelection>(
    () => ({
      camera: selected?.type === 'camera' ? selected.id : null,
      worker: selected?.type === 'worker' ? selected.id : null,
    }),
    [selected],
  )

  return (
    // room for the 480px (760px on the pipeline tab) side panel so it never covers the flow
    <div className={cn('flex flex-col gap-4 transition-[padding] duration-150', selected && (wide ? 'min-[1400px]:pr-[776px]' : 'min-[1200px]:pr-[496px]'))}>
      <PageHeader
        title="Operations"
        hint="Where each camera runs, live. Click a camera or worker for details. Data refreshes every 5 seconds."
        summary={
          workers.data ? (
            <span className="num">
              {live} of {list.length} workers live · {assigned} of {cameras.filter((c) => c.enabled).length} cameras assigned
            </span>
          ) : undefined
        }
      />
      {workers.isError ? (
        <ErrorState message={`Could not load workers: ${workers.error.message}`} onRetry={() => void workers.refetch()} />
      ) : workers.isLoading || cams.isLoading ? (
        <Skeleton className="h-[460px] w-full" />
      ) : list.length === 0 ? (
        <EmptyState
          icon={<Server />}
          title="No workers registered"
          hint="Start a frame-extractor worker; it appears here after its first heartbeat."
        />
      ) : (
        <>
          <FlowDiagram
            workers={list}
            cameras={cameras}
            selection={selection}
            onSelectCamera={selectCamera}
            onSelectWorker={selectWorker}
          />
          <div
            className={cn(
              'grid items-start gap-4',
              // the open side panel takes ~500px: stack the lists until there is room for both
              selected ? 'min-[1900px]:grid-cols-[minmax(0,1fr)_400px]' : 'min-[1100px]:grid-cols-[minmax(0,1fr)_400px]',
            )}
          >
            <section aria-label="Workers" className="min-w-0">
              <h2 className="mb-2 text-sm font-semibold">Workers</h2>
              <WorkersPanel
                workers={list}
                cameras={cameras}
                health={health}
                selectedWorker={selection.worker}
                onSelectCamera={selectCamera}
              />
            </section>
            <section aria-label="Autoscaler">
              <AutoscalerPanel />
            </section>
          </div>
          {selected?.type === 'camera' && (
            <CameraPanel
              cameraId={selected.id}
              cameras={cameras}
              workers={list}
              ops={ops}
              onSelectWorker={selectWorker}
              onWideChange={setWide}
              onClose={close}
            />
          )}
          {selected?.type === 'worker' && (
            <WorkerPanel
              worker={list.find((w) => w.worker_id === selected.id)}
              cameras={cameras}
              ops={ops}
              onSelectCamera={selectCamera}
              onClose={close}
            />
          )}
          {ops.dialogs}
        </>
      )}
    </div>
  )
}
