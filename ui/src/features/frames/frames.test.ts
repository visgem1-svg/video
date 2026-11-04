import { describe, expect, it } from 'vitest'
import type { FrameRecord } from '@/lib/api/frames'
import { classCounts, frameToBoxes } from './boxes'
import { mergeFrames, olderCursor, shouldLoadOlder, stepIndex, visibleRange } from './timeline'
import { formatTimeMs } from './format'

const mk = (seq: number, extra: Partial<FrameRecord> = {}): FrameRecord => ({
  frame_id: `cam-001-${seq}`,
  camera_id: 'cam-001',
  ts: 1_000_000 + seq * 100,
  seq,
  width: 1280,
  height: 720,
  worker_id: 'fx-1',
  analyzed: false,
  objects: {},
  plates: [],
  faces: [],
  texts: [],
  total_ms: null,
  ...extra,
})

describe('frameToBoxes', () => {
  it('maps objects, plates, faces and texts with palette colours and dashes', () => {
    const boxes = frameToBoxes({
      objects: [{ class: 'car', confidence: 0.94, box: [1, 2, 3, 4] }],
      plates: [{ plate: 'AB123', confidence: 1, box: [5, 6, 7, 8] }],
      faces: [
        { box: [1, 1, 2, 2], confidence: 0.9, person_id: 'p', person_name: 'Dana', score: 0.8 },
        { box: [1, 1, 2, 2], confidence: 0.9, person_id: null, person_name: null, score: 0.1 },
      ],
      texts: [{ text: 'EXIT', confidence: 0.7, box: [9, 9, 10, 10] }],
    })
    expect(boxes.map((b) => b.kind)).toEqual(['object', 'plate', 'face_known', 'face', 'text'])
    expect(boxes[0]).toMatchObject({ label: 'car 94%', color: '#A3E635', dashed: false })
    expect(boxes[1]).toMatchObject({ label: 'AB123', color: '#FACC15', dashed: true })
    expect(boxes[2].label).toContain('Dana')
    expect(boxes[3].label).toBe('unknown')
    expect(boxes[4].dashed).toBe(true)
  })
  it('draws nothing for the list shape (class counts only)', () => {
    expect(frameToBoxes({ objects: { car: 2 }, plates: [], faces: [], texts: [] })).toEqual([])
  })
  it('counts classes from either shape', () => {
    expect(classCounts({ objects: { car: 2, person: 1 }, plates: [], faces: [], texts: [] })).toEqual([
      ['car', 2],
      ['person', 1],
    ])
    expect(
      classCounts({
        objects: [{ class: 'car', confidence: 1, box: [0, 0, 1, 1] }],
        plates: [{ plate: 'x', confidence: 1, box: [0, 0, 1, 1] }],
        faces: [],
        texts: [],
      }),
    ).toEqual([
      ['car', 1],
      ['plate', 1],
    ])
  })
})

describe('filmstrip paging', () => {
  it('merges newest-first without duplicates and lets fresher records win', () => {
    const a = [mk(3), mk(2)]
    const merged = mergeFrames(a, [mk(4), mk(3, { analyzed: true })])
    expect(merged.map((f) => f.seq)).toEqual([4, 3, 2])
    expect(merged[1].analyzed).toBe(true)
    expect(mergeFrames(a, [])).toBe(a)
  })
  it('older cursor is the oldest ts held', () => {
    expect(olderCursor([])).toBeNull()
    expect(olderCursor([mk(3), mk(2)])).toBe(mk(2).ts)
  })
  it('computes the visible index range (oldest left, newest right)', () => {
    // 100 frames, 100px items, viewport 300 scrolled to the right end
    const r = visibleRange(9700, 300, 100, 100, 0)
    expect(r).toEqual({ from: 0, to: 2 })
    const left = visibleRange(0, 300, 100, 100, 0)
    expect(left.to).toBe(99)
    expect(visibleRange(0, 0, 100, 10).to).toBe(-1)
  })
  it('requests an older page near the oldest edge only when allowed', () => {
    const near = { from: 80, to: 95 }
    expect(shouldLoadOlder(near, 100, true, false)).toBe(true)
    expect(shouldLoadOlder(near, 100, false, false)).toBe(false)
    expect(shouldLoadOlder(near, 100, true, true)).toBe(false)
    expect(shouldLoadOlder({ from: 0, to: 10 }, 100, true, false)).toBe(false)
  })
  it('steps older/newer and clamps', () => {
    expect(stepIndex(2, -1, 10)).toBe(3)
    expect(stepIndex(2, 1, 10)).toBe(1)
    expect(stepIndex(0, 1, 10)).toBe(0)
    expect(stepIndex(9, -1, 10)).toBe(9)
    expect(stepIndex(0, -1, 0)).toBe(-1)
  })
  it('formats time with milliseconds', () => {
    expect(formatTimeMs(new Date(2026, 0, 1, 9, 5, 7, 42).getTime())).toBe('09:05:07.042')
  })
})
