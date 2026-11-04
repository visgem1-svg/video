import { memo, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { Car, Radio, ScanFace, Search, UserCheck } from 'lucide-react'
import { Dropdown } from '@/components/ui/dropdown'
import {
  Button,
  EmptyState,
  ErrorState,
  Input,
  PageHeader,
  Segmented,
  Skeleton,
} from '@/components/ui/primitives'
import { snapshotSrc, useCameras, useRecognitionHistory } from '@/lib/api/queries'
import { CLASS_COLORS } from '@/lib/overlay'
import { useRecognitionFeed } from '@/lib/realtime/stores'
import type { Recognition, RecognitionRecord, RecognitionType } from '@/lib/types'
import { cn, formatAge, formatDateTime } from '@/lib/utils'
import { LIVE_FIRST_REQUEST_MS, SnapshotImage } from './SnapshotImage'

/** Confidence ring: >=90 ok, 70-89 warn, <70 err (plus the number, never colour alone). */
export const confidenceTone = (c: number) => (c >= 0.9 ? 'ok' : c >= 0.7 ? 'warn' : 'err')
const RING = { ok: 'ring-ok', warn: 'ring-warn', err: 'ring-err' } as const

export const RecognitionCard = memo(function RecognitionCard({
  rec,
  cameraName,
}: {
  rec: Recognition | RecognitionRecord
  cameraName?: string
}) {
  const isPlate = rec.type === 'plate'
  const known = !isPlate && !!rec.person_id && rec.value.toLowerCase() !== 'unknown'
  const color = isPlate ? CLASS_COLORS.plate : known ? CLASS_COLORS.face_known : CLASS_COLORS.face
  const tone = confidenceTone(rec.confidence)
  return (
    <article className="overflow-hidden rounded-xl border bg-card">
      <div className="relative aspect-[4/3] bg-muted">
        <SnapshotImage
          src={snapshotSrc(rec)}
          alt={`${rec.type} ${rec.value}`}
          initialDelayMs={
            (rec as RecognitionRecord).snapshot_url || Date.now() - rec.ts > 5000
              ? 0
              : LIVE_FIRST_REQUEST_MS
          }
        />
        <span className="absolute top-2 left-2 inline-flex h-5 items-center gap-1 rounded-full bg-black/55 px-2 text-xs font-medium text-white backdrop-blur-sm">
          <span className="size-2 rounded-full" style={{ background: color }} aria-hidden />
          {isPlate ? (
            <Car className="size-3" aria-hidden />
          ) : (
            <ScanFace className="size-3" aria-hidden />
          )}
          {rec.type}
        </span>
        <span
          className={cn(
            'num absolute top-2 right-2 rounded-full bg-black/55 px-1.5 text-xs leading-5 font-medium text-white ring-2 backdrop-blur-sm',
            RING[tone],
          )}
          title="Confidence"
        >
          {Math.round(rec.confidence * 100)}%
        </span>
      </div>
      <div className="space-y-0.5 p-3">
        {isPlate ? (
          <div className="font-mono text-[15px] leading-6 font-semibold tracking-wider">
            {rec.value}
          </div>
        ) : known ? (
          <div className="flex items-center gap-1.5 text-[15px] leading-6 font-semibold">
            <UserCheck className="size-4 shrink-0 text-ok" aria-hidden />
            <span className="truncate">{rec.value}</span>
          </div>
        ) : (
          <div className="text-[15px] leading-6 text-muted-foreground">Unknown person</div>
        )}
        <div className="flex items-center justify-between gap-2 text-xs text-muted-foreground">
          <span className="truncate">{cameraName ?? rec.camera_id}</span>
          <time className="num shrink-0" title={formatDateTime(rec.ts)}>
            {formatAge(Date.now() - rec.ts)} ago
          </time>
        </div>
      </div>
    </article>
  )
})

const Grid = ({ children }: { children: React.ReactNode }) => (
  <div className="grid grid-cols-[repeat(auto-fill,minmax(200px,1fr))] gap-3">{children}</div>
)

function matches(
  r: Recognition,
  type: 'all' | RecognitionType,
  camera: string,
  plate: string,
): boolean {
  if (type !== 'all' && r.type !== type) return false
  if (camera && r.camera_id !== camera) return false
  if (plate && !r.value.toLowerCase().includes(plate.trim().toLowerCase())) return false
  return true
}

export default function RecognitionsPage() {
  const [tab, setTab] = useState<'live' | 'history'>('live')
  const [type, setType] = useState<'all' | RecognitionType>('all')
  const [camera, setCamera] = useState('')
  const [plate, setPlate] = useState('')
  const [limit, setLimit] = useState(50)
  const { data: cameras } = useCameras()
  const names = useMemo(() => new Map((cameras ?? []).map((c) => [c.camera_id, c.name])), [cameras])
  const feed = useRecognitionFeed()
  const history = useRecognitionHistory({
    camera_id: camera || undefined,
    type: type === 'all' ? undefined : type,
    limit,
  })

  const live = useMemo(
    () => feed.filter((r) => matches(r, type, camera, plate)),
    [feed, type, camera, plate],
  )
  const hist = useMemo(
    () => (history.data ?? []).filter((r) => matches(r, type, camera, plate)),
    [history.data, type, camera, plate],
  )
  const rows = tab === 'live' ? live : hist
  const filtering = type !== 'all' || !!camera || !!plate.trim()
  const hadData = tab === 'live' ? feed.length > 0 : (history.data?.length ?? 0) > 0

  return (
    <div>
      <PageHeader
        title="Recognitions"
        description="License plates and faces read by pipelines."
        actions={
          <>
            <div className="relative">
              <Search className="absolute top-2.5 left-2.5 size-4 text-faint" aria-hidden />
              <Input
                className="w-48 pl-8"
                placeholder="Search plate / name"
                value={plate}
                onChange={(e) => setPlate(e.target.value)}
                aria-label="Plate search"
              />
            </div>
            <Dropdown
              ariaLabel="Camera filter"
              value={camera}
              onChange={setCamera}
              options={[
                { value: '', label: 'All cameras' },
                ...(cameras ?? []).map((c) => ({ value: c.camera_id, label: c.name })),
              ]}
            />
            <Segmented
              value={type}
              onChange={setType}
              options={[
                { value: 'all', label: 'All' },
                { value: 'plate', label: 'Plates' },
                { value: 'face', label: 'Faces' },
              ]}
            />
            <Segmented
              value={tab}
              onChange={setTab}
              options={[
                {
                  value: 'live',
                  label: (
                    <span className="inline-flex items-center gap-1">
                      <Radio className="size-3" aria-hidden /> Live
                    </span>
                  ),
                },
                { value: 'history', label: 'History' },
              ]}
            />
          </>
        }
      />
      {tab === 'history' && history.isError ? (
        <ErrorState
          message={`Could not load recognitions: ${String(history.error)}`}
          onRetry={() => void history.refetch()}
        />
      ) : tab === 'history' && history.isLoading ? (
        <Grid>
          {Array.from({ length: 8 }, (_, i) => (
            <Skeleton key={i} className="aspect-[4/3]" />
          ))}
        </Grid>
      ) : rows.length === 0 ? (
        <EmptyState
          icon={<ScanFace />}
          title={filtering && hadData ? 'No matches' : 'No recognitions yet'}
          hint={
            filtering && hadData
              ? 'Nothing matches the current search or filters. Clear them to see everything.'
              : tab === 'live'
                ? 'New plate and face reads appear here in real time. Activate a pipeline with plate or face steps to start.'
                : 'Nothing matches these filters. Try widening them.'
          }
          action={
            <Button asChild variant="outline" size="sm">
              <Link to="/pipelines">Open pipelines</Link>
            </Button>
          }
        />
      ) : (
        <>
          <Grid>
            {rows.map((r) => (
              <RecognitionCard key={r.recognition_id} rec={r} cameraName={names.get(r.camera_id)} />
            ))}
          </Grid>
          {tab === 'history' && (history.data?.length ?? 0) >= limit && (
            <div className="mt-4 text-center">
              <Button variant="outline" onClick={() => setLimit((l) => l + 50)}>
                Load more
              </Button>
            </div>
          )}
        </>
      )}
    </div>
  )
}
