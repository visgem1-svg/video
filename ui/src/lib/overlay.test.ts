import { describe, expect, it } from 'vitest'
import {
  ageChipText,
  isStale,
  MIN_ALPHA,
  buildDrawItems,
  containTransform,
  fadeAlpha,
  faceLabel,
  mergeDetection,
  scaleBox,
} from './overlay'
import { detectionAt } from './mock/world'
import type { CameraConfig, Detection } from './types'

describe('scaleBox', () => {
  it('scales proportionally when aspect matches', () => {
    expect(scaleBox([96, 54, 192, 108], 960, 540, 480, 270)).toEqual({ x: 48, y: 27, w: 48, h: 27 })
  })
  it('letterboxes (contain) when the surface is taller than the frame', () => {
    const r = scaleBox([0, 0, 960, 540], 960, 540, 480, 480)
    expect(r).toEqual({ x: 0, y: 105, w: 480, h: 270 })
  })
  it('stretch mode scales axes independently', () => {
    expect(scaleBox([0, 0, 100, 100], 100, 100, 200, 50, 'stretch')).toEqual({
      x: 0,
      y: 0,
      w: 200,
      h: 50,
    })
  })
  it('normalises inverted boxes and survives zero sizes', () => {
    expect(scaleBox([10, 10, 0, 0], 10, 10, 10, 10)).toEqual({ x: 0, y: 0, w: 10, h: 10 })
    expect(containTransform(0, 0, 10, 10).scale).toBe(0)
  })
})

describe('fadeAlpha', () => {
  it('holds, dims progressively, hides only after 5 s', () => {
    expect(fadeAlpha(0)).toBe(1)
    expect(fadeAlpha(500)).toBe(1)
    expect(fadeAlpha(1000)).toBeLessThan(1)
    expect(fadeAlpha(1000)).toBeGreaterThan(fadeAlpha(3000))
    expect(fadeAlpha(3000)).toBeGreaterThan(fadeAlpha(12000))
    expect(fadeAlpha(12000)).toBeCloseTo(MIN_ALPHA)
    expect(fadeAlpha(12001)).toBe(0)
    expect(isStale(12000)).toBe(false)
    expect(isStale(12001)).toBe(true)
  })
  it('age chip text', () => {
    expect(ageChipText(null)).toBeNull()
    expect(ageChipText(300)).toBe('updated now')
    expect(ageChipText(3400)).toBe('updated 3s ago')
  })
})

const cam: CameraConfig = {
  camera_id: 'cam-001',
  name: 'Gate',
  stream_url: 'rtsp://x',
  analysis_fps: 3,
  enabled: true,
  plan_id: null,
  tags: [],
}

describe('partial merge (contract 9.1)', () => {
  const main = detectionAt(cam, 1_000_000)
  it('merges a partial of the same frame without dropping boxes', () => {
    const partial: Detection = {
      ...main,
      objects: [],
      plates: [],
      faces: [],
      texts: [{ text: 'GATE', confidence: 0.9, box: [1, 1, 5, 5] }],
      partial: true,
      step_id: 's3',
    }
    const m = mergeDetection(main, partial)
    expect(m.objects).toHaveLength(main.objects.length)
    expect(m.texts).toHaveLength(main.texts.length + 1)
    expect(m.partial).toBe(false)
  })
  it('replaces when the frame differs, and handles partial-first order', () => {
    const other = { ...main, frame_id: 'other' }
    expect(mergeDetection(main, other)).toBe(other)
    const early: Detection = {
      ...main,
      objects: [],
      plates: [],
      faces: [],
      texts: [],
      partial: true,
      step_id: 's2',
    }
    const m = mergeDetection(early, main)
    expect(m.objects).toHaveLength(main.objects.length)
    expect(m.partial).toBe(false)
  })
})

describe('faces', () => {
  it('labels by identification state', () => {
    expect(faceLabel({ person_name: 'Dana', score: 0.7 })).toBe('Dana')
    expect(faceLabel({ person_name: null, score: 0.2 })).toBe('unknown')
    expect(faceLabel({ person_name: null, score: null })).toBe('face detected')
  })
  it('buildDrawItems emits one item per box and scales to the canvas', () => {
    const d = detectionAt(cam, 5_000)
    const items = buildDrawItems(d, 480, 270)
    expect(items.length).toBe(d.objects.length + d.plates.length + d.faces.length + d.texts.length)
    items.forEach((i) => expect(i.rect.w).toBeGreaterThan(0))
  })
})
