import { memo, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { Loader2, ScanSearch } from 'lucide-react'
import { frameImageUrl, type FrameRecord } from '@/lib/api/frames'
import { cn } from '@/lib/utils'
import { classCounts, classColor } from './boxes'
import { formatTimeMs } from './format'
import { shouldLoadOlder, THUMB_W, visibleRange } from './timeline'

const ITEM_H = 132

const Thumb = memo(function Thumb({
  frame,
  selected,
  onSelect,
  left,
}: {
  frame: FrameRecord
  selected: boolean
  onSelect: (id: string) => void
  left: number
}) {
  const counts = classCounts(frame)
  const [failed, setFailed] = useState(false)
  return (
    <button
      type="button"
      onClick={() => onSelect(frame.frame_id)}
      aria-label={`Frame ${frame.seq} at ${formatTimeMs(frame.ts)}`}
      aria-current={selected ? 'true' : undefined}
      style={{ left, width: THUMB_W - 6 }}
      className={cn(
        'absolute top-1 flex flex-col gap-1 rounded-md border p-1 text-left transition-colors duration-[120ms]',
        selected ? 'border-primary bg-accent' : 'border-border bg-card hover:bg-muted',
      )}
    >
      <div className="relative aspect-video w-full overflow-hidden rounded-[4px] bg-[var(--video-bg)]">
        {failed ? (
          <span className="absolute inset-0 grid place-items-center text-[10px] text-faint">expired</span>
        ) : (
          <img
            src={frameImageUrl(frame)}
            loading="lazy"
            decoding="async"
            alt=""
            draggable={false}
            onError={() => setFailed(true)}
            className="size-full object-cover"
          />
        )}
      </div>
      <div className="num flex items-center justify-between font-mono text-[11px] leading-4">
        <span>{formatTimeMs(frame.ts).slice(3)}</span>
        <span className="text-faint">#{frame.seq}</span>
      </div>
      <div className="flex items-center justify-between gap-1 text-[11px] leading-4 text-muted-foreground">
        <span className="truncate font-mono">{frame.worker_id}</span>
        {frame.analyzed ? (
          <span className="inline-flex items-center gap-0.5 text-ok" title="Analyzed">
            <ScanSearch className="size-3" aria-hidden />
            <span className="sr-only">analyzed</span>
          </span>
        ) : (
          <span className="text-faint" title="Not analyzed">
            raw<span className="sr-only"> not analyzed</span>
          </span>
        )}
      </div>
      <div className="flex h-4 items-center gap-1 overflow-hidden">
        {counts.slice(0, 2).map(([cls, n]) => (
          <span
            key={cls}
            className="inline-flex items-center gap-1 rounded-full bg-muted px-1.5 text-[10px] leading-4"
          >
            <span className="size-1.5 rounded-full" style={{ background: classColor(cls) }} aria-hidden />
            {cls} {n}
          </span>
        ))}
        {counts.length > 2 && <span className="text-[10px] text-faint">+{counts.length - 2}</span>}
      </div>
    </button>
  )
})

/** Horizontal virtualized timeline: oldest left, newest right. Only visible thumbs are mounted. */
export function Filmstrip({
  frames,
  selectedId,
  follow,
  onSelect,
  hasMore,
  loading,
  onLoadOlder,
}: {
  frames: FrameRecord[]
  selectedId: string | null
  /** keep the newest frame in view as frames stream in */
  follow: boolean
  onSelect: (id: string) => void
  hasMore: boolean
  loading: boolean
  onLoadOlder: () => void
}) {
  const ref = useRef<HTMLDivElement>(null)
  const [scroll, setScroll] = useState(0)
  const [vw, setVw] = useState(0)
  const count = frames.length
  const range = visibleRange(scroll, vw, THUMB_W, count)
  const prev = useRef<{ count: number; oldest?: string; newest?: string }>({ count: 0 })

  useEffect(() => {
    const el = ref.current
    if (!el) return
    const measure = () => setVw(el.clientWidth)
    measure()
    if (typeof ResizeObserver === 'undefined') return
    const ro = new ResizeObserver(measure)
    ro.observe(el)
    return () => ro.disconnect()
  }, [])

  // keep the viewport anchored when frames are added on either side
  useLayoutEffect(() => {
    const el = ref.current
    if (!el) return
    const p = prev.current
    const oldest = frames[count - 1]?.frame_id
    const newest = frames[0]?.frame_id
    if (p.count === 0 && count > 0) el.scrollLeft = el.scrollWidth
    else if (oldest !== p.oldest && newest === p.newest) el.scrollLeft += (count - p.count) * THUMB_W
    else if (newest !== p.newest && follow) el.scrollLeft = el.scrollWidth
    prev.current = { count, oldest, newest }
    setScroll(el.scrollLeft)
  }, [frames, count, follow])

  // bring the selected frame into view (keyboard stepping)
  useEffect(() => {
    const el = ref.current
    if (!el || !selectedId) return
    const i = frames.findIndex((f) => f.frame_id === selectedId)
    if (i < 0) return
    const x = (count - 1 - i) * THUMB_W
    if (x < el.scrollLeft) el.scrollLeft = x - THUMB_W
    else if (x + THUMB_W > el.scrollLeft + el.clientWidth) el.scrollLeft = x - el.clientWidth + 2 * THUMB_W
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedId])

  useEffect(() => {
    if (shouldLoadOlder(range, count, hasMore, loading)) onLoadOlder()
  }, [range.to, count, hasMore, loading, onLoadOlder, range])

  const items = []
  for (let i = Math.max(0, range.from); i <= range.to; i++) {
    const f = frames[i]
    if (!f) continue
    items.push(
      <Thumb
        key={f.frame_id}
        frame={f}
        selected={f.frame_id === selectedId}
        onSelect={onSelect}
        left={(count - 1 - i) * THUMB_W}
      />,
    )
  }

  return (
    <div className="relative">
      <div
        ref={ref}
        role="list"
        aria-label="Frame timeline, oldest to newest"
        onScroll={(e) => setScroll(e.currentTarget.scrollLeft)}
        style={{ height: ITEM_H + 8 }}
        className="overflow-x-auto overflow-y-hidden rounded-lg border bg-card"
      >
        <div className="relative h-full" style={{ width: Math.max(count * THUMB_W, 1) }}>
          {items}
        </div>
      </div>
      {loading && (
        <span className="absolute top-2 left-2 inline-flex items-center gap-1 rounded-full bg-black/60 px-2 py-0.5 text-[11px] text-white">
          <Loader2 className="size-3 animate-spin" aria-hidden /> older frames
        </span>
      )}
      {!hasMore && count > 0 && (
        <span className="absolute top-2 left-2 rounded-full bg-black/60 px-2 py-0.5 text-[11px] text-white/80">
          start of retained frames
        </span>
      )}
    </div>
  )
}
