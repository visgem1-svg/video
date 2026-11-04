import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PLANNER_TIMEOUT_MS, httpBackend } from './httpBackend'
import { runtime, startReprobe, initRuntime } from './runtime'
import { snapshotSrc } from './queries'
import { describeWhen } from '@/features/pipelines/dag'
import { CAMERA_ID_RE, isValidStreamUrl } from '@/features/sources/validation'
import type { RecognitionRecord } from '../types'

const rec: RecognitionRecord = {
  recognition_id: 'abc',
  type: 'plate',
  camera_id: 'cam-001',
  frame_id: 'f',
  ts: Date.UTC(2026, 9, 21, 23, 59),
  value: '123',
  person_id: null,
  confidence: 0.9,
  box: [0, 0, 1, 1],
  frame_key: 'k',
  plan_id: 'p',
}

describe('snapshotSrc', () => {
  it('prefers snapshot_url from the API (made absolute)', () => {
    expect(snapshotSrc({ ...rec, snapshot_url: '/api/snapshots/plate/20261021/abc.jpg' })).toBe(
      'http://localhost:8000/api/snapshots/plate/20261021/abc.jpg',
    )
  })
  it('derives the UTC-dated URL for live events', () => {
    expect(snapshotSrc(rec)).toBe('http://localhost:8000/api/snapshots/plate/20261021/abc.jpg')
  })
})

describe('any-of class labels (9.2)', () => {
  it('renders lists with |', () => {
    expect(describeWhen({ type: 'found', class: 'car,truck,bus' })).toBe('found car | truck | bus')
    expect(describeWhen({ type: 'count', class: 'car,truck', gt: 2 })).toBe(
      'count(car | truck) > 2',
    )
    expect(describeWhen({ type: 'found', class: 'cell phone', min_confidence: 0.5 })).toBe(
      'found cell phone ≥ 50%',
    )
  })
})

describe('camera id / stream url (9.3)', () => {
  it('validates', () => {
    expect(CAMERA_ID_RE.test('cam-021')).toBe(true)
    expect(CAMERA_ID_RE.test('ab')).toBe(false)
    expect(CAMERA_ID_RE.test('Cam_1')).toBe(false)
    for (const u of [
      'rtsp://h/a',
      'rtsps://h/a',
      'rtmp://h/live',
      'srt://h:9000',
      'https://h/x/index.m3u8',
      'publisher',
    ])
      expect(isValidStreamUrl(u)).toBe(true)
    for (const u of ['http://h/video.mp4', 'ftp://h', 'foo', ''])
      expect(isValidStreamUrl(u)).toBe(false)
  })
})

describe('planner requests', () => {
  afterEach(() => {
    vi.useRealTimers()
    vi.unstubAllGlobals()
  })
  it('can be cancelled with an AbortSignal', async () => {
    vi.stubGlobal(
      'fetch',
      (_u: string, init: RequestInit) =>
        new Promise((_r, rej) =>
          init.signal!.addEventListener('abort', () => rej(new DOMException('x', 'AbortError'))),
        ),
    )
    const c = new AbortController()
    const p = httpBackend.draftPlan({ query: 'q', camera_ids: [] }, c.signal)
    c.abort()
    await expect(p).rejects.toMatchObject({ name: 'AbortError' })
  })
  it('times out after 6 minutes', async () => {
    vi.useFakeTimers()
    vi.stubGlobal(
      'fetch',
      (_u: string, init: RequestInit) =>
        new Promise((_r, rej) =>
          init.signal!.addEventListener('abort', () => rej(new DOMException('x', 'AbortError'))),
        ),
    )
    const p = httpBackend.draftPlan({ query: 'q', camera_ids: [] })
    const assertion = expect(p).rejects.toThrow(/Timed out after 6 min/)
    await vi.advanceTimersByTimeAsync(PLANNER_TIMEOUT_MS + 1)
    await assertion
  })
})

describe('mock -> live re-probe', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => {
    vi.useRealTimers()
    vi.unstubAllGlobals()
  })
  it('falls back to mock when unreachable and returns to live once the gateway answers', async () => {
    let up = false
    vi.stubGlobal('fetch', async () => {
      if (!up) throw new TypeError('network')
      return new Response('{}', { status: 200 })
    })
    const init = initRuntime()
    await vi.advanceTimersByTimeAsync(10)
    await init
    expect(runtime.get()).toMatchObject({ mode: 'mock', reason: 'unreachable' })
    startReprobe()
    await vi.advanceTimersByTimeAsync(10_000)
    expect(runtime.get().mode).toBe('mock')
    up = true
    await vi.advanceTimersByTimeAsync(10_000)
    expect(runtime.get()).toMatchObject({ mode: 'live', reason: null })
  })
})
