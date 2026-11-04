import { useState } from 'react'
import { ImageOff, Loader2 } from 'lucide-react'
import { frameImageUrl, FRAME_RETENTION_MS, type FrameRecord } from '@/lib/api/frames'
import { useNow } from '@/lib/realtime/useNow'
import { Badge, Card, CardBody, CardHeader, CardTitle } from '@/components/ui/primitives'
import { classColor, classCounts, frameToBoxes, type FrameBox } from './boxes'
import { formatAgeMs, formatTimeMs } from './format'

function BoxLayer({ boxes, w, h }: { boxes: FrameBox[]; w: number; h: number }) {
  const fs = Math.max(11, w / 64)
  return (
    <svg
      className="pointer-events-none absolute inset-0 size-full"
      viewBox={`0 0 ${w} ${h}`}
      preserveAspectRatio="xMidYMid meet"
      aria-label={`${boxes.length} detections`}
    >
      {boxes.map((b, i) => {
        const [x1, y1, x2, y2] = b.box
        const tw = b.label.length * fs * 0.58 + fs
        const ly = y1 - fs * 1.5 < 0 ? y1 : y1 - fs * 1.5
        return (
          <g key={i}>
            <rect x={x1} y={y1} width={x2 - x1} height={y2 - y1} fill="none" stroke="rgba(0,0,0,.55)" strokeWidth={4} vectorEffect="non-scaling-stroke" />
            <rect
              x={x1}
              y={y1}
              width={x2 - x1}
              height={y2 - y1}
              fill="none"
              stroke={b.color}
              strokeWidth={2}
              strokeDasharray={b.dashed ? '6 4' : undefined}
              vectorEffect="non-scaling-stroke"
            />
            <rect x={x1} y={ly} width={tw} height={fs * 1.4} fill={b.color} rx={3} />
            <text x={x1 + fs * 0.5} y={ly + fs * 1.05} fontSize={fs} fontWeight={600} fill="#0B0D12" fontFamily="Inter Variable, sans-serif">
              {b.label}
            </text>
          </g>
        )
      })}
    </svg>
  )
}

/** Large viewer: raw frame by default, detections only when `showAnalysis` is on. */
export function FrameViewer({
  frame,
  detail,
  showAnalysis,
  detailLoading,
}: {
  frame: FrameRecord | null
  detail: FrameRecord | undefined
  showAnalysis: boolean
  detailLoading: boolean
}) {
  const [failed, setFailed] = useState<string | null>(null)
  if (!frame)
    return (
      <div className="grid aspect-video w-full place-items-center rounded-xl border bg-[var(--video-bg)] text-[13px] text-faint">
        Waiting for the first frame
      </div>
    )
  const expired = failed === frame.frame_id
  const boxes = showAnalysis && frame.analyzed ? frameToBoxes(detail ?? frame) : []
  return (
    <div className="relative aspect-video w-full overflow-hidden rounded-xl border bg-[var(--video-bg)]">
      {expired ? (
        <div className="absolute inset-0 grid place-items-center">
          <div className="flex flex-col items-center gap-2 text-center">
            <ImageOff className="size-6 text-faint" aria-hidden />
            <div className="text-sm font-semibold text-white/90">Frame expired</div>
            <div className="max-w-[40ch] text-[13px] text-white/60">
              Images are kept for {FRAME_RETENTION_MS / 60_000} minutes. The record below is still available.
            </div>
          </div>
        </div>
      ) : (
        <img
          key={frame.frame_id}
          src={frameImageUrl(frame)}
          alt={`${frame.camera_id} frame ${frame.seq}`}
          onError={() => setFailed(frame.frame_id)}
          className="absolute inset-0 size-full object-contain"
        />
      )}
      {!expired && boxes.length > 0 && <BoxLayer boxes={boxes} w={frame.width} h={frame.height} />}
      <div className="absolute top-0 inset-x-0 flex items-center justify-between bg-gradient-to-b from-black/65 to-transparent p-2.5">
        <span className="num font-mono text-xs text-white/85">{formatTimeMs(frame.ts)}</span>
        <span className="flex items-center gap-2">
          {showAnalysis && !frame.analyzed && (
            <Badge tone="neutral">Not analyzed</Badge>
          )}
          {showAnalysis && frame.analyzed && detailLoading && (
            <Loader2 className="size-3.5 animate-spin text-white/80" aria-label="Loading detections" />
          )}
        </span>
      </div>
    </div>
  )
}

function Row({ k, children }: { k: string; children: React.ReactNode }) {
  return (
    <div className="flex items-baseline justify-between gap-3 border-b py-1.5 last:border-0">
      <dt className="text-xs text-muted-foreground">{k}</dt>
      <dd className="num min-w-0 truncate text-right font-mono text-xs" title={typeof children === 'string' ? children : undefined}>
        {children}
      </dd>
    </div>
  )
}

export function FrameMeta({ frame }: { frame: FrameRecord | null }) {
  const now = useNow()
  if (!frame)
    return (
      <Card>
        <CardHeader>
          <CardTitle>Frame details</CardTitle>
        </CardHeader>
        <CardBody className="text-[13px] text-muted-foreground">Select a frame in the timeline.</CardBody>
      </Card>
    )
  const counts = classCounts(frame)
  return (
    <Card>
      <CardHeader>
        <CardTitle>Frame details</CardTitle>
        {frame.analyzed ? <Badge tone="success">analyzed</Badge> : <Badge>raw, not analyzed</Badge>}
      </CardHeader>
      <CardBody>
        <dl>
          <Row k="frame_id">{frame.frame_id}</Row>
          <Row k="Captured">{`${formatTimeMs(frame.ts)} (${formatAgeMs(now - frame.ts)} ago)`}</Row>
          <Row k="Seq">{String(frame.seq)}</Row>
          <Row k="Size">{`${frame.width}×${frame.height}`}</Row>
          <Row k="Worker">{frame.worker_id}</Row>
          <Row k="Total time">{frame.total_ms != null ? `${frame.total_ms} ms` : '-'}</Row>
        </dl>
        {counts.length > 0 && (
          <div className="mt-3 flex flex-wrap gap-1.5">
            {counts.map(([cls, n]) => (
              <span key={cls} className="inline-flex h-5 items-center gap-1.5 rounded-full border bg-muted px-2 text-xs">
                <span className="size-2 rounded-full" style={{ background: classColor(cls) }} aria-hidden />
                {cls} <span className="num">{n}</span>
              </span>
            ))}
          </div>
        )}
      </CardBody>
    </Card>
  )
}
