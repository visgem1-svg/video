import type { FrameRecord } from '@/lib/api/frames'

export const THUMB_W = 132
export const PAGE_SIZE = 60

/** Merge incoming frames into a newest-first list, de-duplicated by frame_id. */
export function mergeFrames(existing: FrameRecord[], incoming: FrameRecord[]): FrameRecord[] {
  if (incoming.length === 0) return existing
  const map = new Map<string, FrameRecord>()
  for (const f of existing) map.set(f.frame_id, f)
  for (const f of incoming) map.set(f.frame_id, f) // newer data (analysis arrives later) wins
  return [...map.values()].sort((a, b) => b.ts - a.ts || b.seq - a.seq)
}

/** Cursor for the next "older" page: the oldest ts we hold (null = nothing loaded yet). */
export function olderCursor(frames: FrameRecord[]): number | null {
  return frames.length ? frames[frames.length - 1].ts : null
}

/**
 * Timeline is drawn oldest -> newest, left -> right; frames[] is newest-first, so index i sits at
 * slot (count - 1 - i). Returns the inclusive index range (into frames[]) that is on screen.
 */
export function visibleRange(
  scrollLeft: number,
  viewport: number,
  itemW: number,
  count: number,
  overscan = 3,
): { from: number; to: number } {
  if (count <= 0 || viewport <= 0) return { from: 0, to: -1 }
  const firstSlot = Math.max(0, Math.floor(scrollLeft / itemW) - overscan)
  const lastSlot = Math.min(count - 1, Math.ceil((scrollLeft + viewport) / itemW) + overscan)
  return { from: count - 1 - lastSlot, to: count - 1 - firstSlot }
}

/** Load an older page once the viewport gets within `threshold` items of the oldest frame. */
export function shouldLoadOlder(
  range: { from: number; to: number },
  count: number,
  hasMore: boolean,
  loading: boolean,
  threshold = 8,
): boolean {
  if (!hasMore || loading || count === 0) return false
  return range.to >= count - 1 - threshold
}

/** Neighbour index when stepping: dir -1 = older (left arrow), +1 = newer (right arrow). */
export function stepIndex(current: number, dir: -1 | 1, count: number): number {
  if (count === 0) return -1
  if (current < 0) return 0
  // frames[] is newest-first: older = higher index
  return Math.min(count - 1, Math.max(0, current - dir))
}
