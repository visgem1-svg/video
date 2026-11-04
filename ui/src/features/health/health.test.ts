import { describe, expect, it } from 'vitest'
import { sortWorstFirst, summarize } from './health'
import type { CameraHealth } from '@/lib/types'
import { validateCamera } from '../sources/validation'

const h = (id: string, state: CameraHealth['state'], fps = 3): CameraHealth => ({
  camera_id: id,
  name: id,
  state,
  worker_id: null,
  fps_actual: fps,
  fps_target: 3,
  last_frame_ts: 1,
  reconnects_1h: 0,
  uptime_pct_1h: 100,
  last_error: null,
  ts: 1,
})

describe('health', () => {
  it('sorts worst first and summarises', () => {
    const list = [
      h('a', 'online'),
      h('b', 'offline', 0),
      h('c', 'degraded', 1),
      h('d', 'unassigned', 0),
    ]
    expect(sortWorstFirst(list).map((x) => x.camera_id)).toEqual(['b', 'c', 'd', 'a'])
    expect(summarize(list)).toEqual({ total: 4, online: 1, degraded: 1, offline: 1, unassigned: 1, idle: 0 })
  })
})

describe('camera validation', () => {
  const ok = {
    camera_id: 'cam-021',
    name: 'X',
    stream_url: 'rtsp://h:8554/a',
    analysis_fps: 3,
    enabled: true,
    plan_id: null,
    tags: [],
  }
  it('accepts a valid camera', () => expect(validateCamera(ok)).toEqual({}))
  it('rejects bad input', () => {
    const e = validateCamera(
      { ...ok, camera_id: 'Bad Id', name: ' ', stream_url: 'foo', analysis_fps: 0 },
      [],
    )
    expect(Object.keys(e).sort()).toEqual(['analysis_fps', 'camera_id', 'name', 'stream_url'])
    expect(validateCamera(ok, ['cam-021']).camera_id).toMatch(/exists/)
    expect(validateCamera(ok, ['cam-021'], true)).toEqual({})
  })
})
