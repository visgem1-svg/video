import { useEffect, useMemo, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { Workflow } from 'lucide-react'
import { Badge, Button, Card, CardBody, CardHeader, CardTitle, EmptyState } from '@/components/ui/primitives'
import { PlanGraph } from '@/features/pipelines/PlanGraph'
import { hub } from '@/lib/realtime/hub'
import { useNow } from '@/lib/realtime/useNow'
import {
  emptyState,
  reduceStepStatus,
  selectStepLive,
  type CameraStepState,
} from '@/lib/stepStatus'
import type { Plan } from '@/lib/types'
import { formatAge } from '@/lib/utils'

/** Folds this camera's `detections` into per-step status; React state is refreshed at ~2 Hz. */
export function useCameraStepState(cameraId: string, plan: Plan): CameraStepState {
  const ref = useRef<CameraStepState>(emptyState(plan))
  const [state, setState] = useState<CameraStepState>(ref.current)
  useEffect(() => {
    ref.current = emptyState(plan)
    setState(ref.current)
    const off = hub.onDetections(cameraId, (d) => {
      ref.current = reduceStepStatus(ref.current, d, plan, Date.now())
    })
    const id = setInterval(
      () => setState((prev) => (prev === ref.current ? prev : ref.current)),
      500,
    )
    return () => {
      off()
      clearInterval(id)
    }
  }, [cameraId, plan])
  return state
}

export function CameraPipelinePanel({
  cameraId,
  plan,
}: {
  cameraId: string
  plan: Plan | undefined
}) {
  if (!plan)
    return (
      <Card id="pipeline">
        <EmptyState
          icon={<Workflow />}
          title="No pipeline on this camera"
          hint="Frames are extracted but not analyzed. Assign a plan to run detection."
          action={
            <Button asChild variant="outline" size="sm">
              <Link to="/pipelines">Open pipelines</Link>
            </Button>
          }
          className="py-8"
        />
      </Card>
    )
  return <PlanPanel cameraId={cameraId} plan={plan} />
}

function PlanPanel({ cameraId, plan }: { cameraId: string; plan: Plan }) {
  const effective = plan
  const state = useCameraStepState(cameraId, effective)
  const now = useNow()
  const live = useMemo(() => selectStepLive(state, now), [state, now])
  const frame = state.frame
  return (
    <Card id="pipeline">
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Workflow className="size-4" /> Pipeline
          <span className="font-normal text-muted-foreground">{effective.name}</span>
        </CardTitle>
        <div className="flex items-center gap-2 text-xs">
          {frame ? (
            <>
              <Badge tone="info">total {frame.total_ms.toFixed(0)} ms</Badge>
              <Badge tone={now - frame.arrivedAt > 5000 ? 'warning' : 'success'}>
                last frame {formatAge(now - frame.arrivedAt)} ago
              </Badge>
            </>
          ) : (
            <Badge>waiting for frames…</Badge>
          )}
        </div>
      </CardHeader>
      <CardBody>
        <PlanGraph plan={effective} live={live} className="h-[400px]" />
      </CardBody>
    </Card>
  )
}
