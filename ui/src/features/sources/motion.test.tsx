import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { ErrorClassBadge } from '@/components/ErrorClassBadge'
import { MotionSummary } from '@/components/MotionSummary'
import { countByErrorClass, errorBadgeOf, errorHintOf } from '@/lib/errorClass'
import { DEFAULT_MOTION, motionOf, skippedLabel, skippedPct } from '@/lib/motion'
import { anyMotionSkipped, summarize } from '@/features/health/health'
import type { CameraConfig, CameraHealth, CameraInput } from '@/lib/types'
import { mapServerErrors, validateCamera } from './validation'

const base: CameraInput = {
  camera_id: 'cam-021',
  name: 'X',
  stream_url: 'rtsp://h:8554/a',
  analysis_fps: 3,
  enabled: true,
  plan_id: null,
  tags: [],
}
const health = (p: Partial<CameraHealth> = {}): CameraHealth => ({
  camera_id: 'cam-021',
  name: 'X',
  state: 'offline',
  worker_id: null,
  fps_actual: 0,
  fps_target: 3,
  last_frame_ts: null,
  reconnects_1h: 0,
  uptime_pct_1h: 0,
  last_error: null,
  ts: 1,
  ...p,
})
const motion = (heartbeat_s: number) => ({
  enabled: true,
  sensitivity: 'medium' as const,
  heartbeat_s,
})

describe('9.9 form validation', () => {
  it('accepts a missing substream and motion (older config)', () => {
    expect(validateCamera(base)).toEqual({})
  })
  it('validates substream URL and motion bounds like the server', () => {
    const e = validateCamera({
      ...base,
      analysis_stream_url: 'publisher',
      motion: motion(61),
    })
    expect(Object.keys(e).sort()).toEqual(['analysis_stream_url', 'motion_heartbeat_s'])
    expect(validateCamera({ ...base, motion: motion(0) }).motion_heartbeat_s).toBeTruthy()
    expect(validateCamera({ ...base, motion: motion(NaN) }).motion_heartbeat_s).toBeTruthy()
    expect(
      validateCamera({
        ...base,
        analysis_stream_url: 'rtsp://h/sub',
        motion: { enabled: true, sensitivity: 'high', heartbeat_s: 60 },
      }),
    ).toEqual({})
  })
})

describe('400 response field mapping', () => {
  it('maps FastAPI loc arrays', () => {
    const { errors } = mapServerErrors({
      detail: [
        { loc: ['body', 'motion', 'heartbeat_s'], msg: 'must be <= 60' },
        { loc: ['body', 'analysis_stream_url'], msg: 'bad scheme' },
      ],
    })
    expect(errors).toEqual({
      motion_heartbeat_s: 'must be <= 60',
      analysis_stream_url: 'bad scheme',
    })
  })
  it('maps {errors: {field: msg}} and dotted names', () => {
    expect(
      mapServerErrors({ errors: { 'motion.sensitivity': 'invalid', stream_url: 'nope' } }).errors,
    ).toEqual({ motion_sensitivity: 'invalid', stream_url: 'nope' })
  })
  it('maps plain strings that name a field, whole word only', () => {
    expect(mapServerErrors({ detail: 'analysis_stream_url must be rtsp' }).errors).toEqual({
      analysis_stream_url: 'analysis_stream_url must be rtsp',
    })
    expect(
      mapServerErrors({ detail: 'heartbeat_s out of range' }).errors.motion_heartbeat_s,
    ).toBeTruthy()
  })
  it('keeps unmappable text for a form-level message', () => {
    const r = mapServerErrors({ detail: 'database unavailable' })
    expect(r.errors).toEqual({})
    expect(r.rest).toBe('database unavailable')
    expect(mapServerErrors(undefined)).toEqual({ errors: {}, rest: undefined })
  })
})

describe('error class badge mapping', () => {
  it('uses fallback labels and marks terminal classes', () => {
    const cases: Array<[string, string, boolean]> = [
      ['auth_failed', 'Wrong credentials', true],
      ['not_found', 'Stream path not found', true],
      ['codec_unsupported', 'Codec not supported', true],
      ['unreachable', 'Camera unreachable', false],
      ['stalled', 'No frames', false],
      ['decode_error', 'Decode errors', false],
      ['eof', 'Stream ended', false],
    ]
    for (const [cls, label, terminal] of cases) {
      const b = errorBadgeOf({ error_class: cls })
      expect(b).toMatchObject({ label, terminal, tone: terminal ? 'danger' : 'warning' })
      expect(errorHintOf({ error_class: cls })).toBe(label)
    }
  })
  it('prefers the server hint and tolerates unknown or missing classes', () => {
    expect(errorHintOf({ error_class: 'auth_failed', error_hint: 'Check the password' })).toBe(
      'Check the password',
    )
    expect(errorBadgeOf({ error_class: 'tls_error' })?.label).toBe('tls error')
    expect(errorBadgeOf({ error_class: null })).toBeNull()
    expect(errorBadgeOf(undefined)).toBeNull()
  })
  it('renders an edit link for terminal classes only', () => {
    const { rerender } = render(
      <MemoryRouter>
        <ErrorClassBadge health={health({ error_class: 'auth_failed' })} cameraId="cam-021" />
      </MemoryRouter>,
    )
    expect(screen.getByText('Wrong credentials')).toBeTruthy()
    expect(screen.getByRole('link', { name: /edit source/i }).getAttribute('href')).toBe(
      '/sources?edit=cam-021',
    )
    rerender(
      <MemoryRouter>
        <ErrorClassBadge health={health({ error_class: 'stalled' })} cameraId="cam-021" />
      </MemoryRouter>,
    )
    expect(screen.queryByRole('link')).toBeNull()
  })
  it('counts classes in the summary only when present', () => {
    const list = [
      health({ error_class: 'unreachable' }),
      health({ error_class: 'unreachable' }),
      health({ error_class: 'eof' }),
      health({ state: 'online' }),
    ]
    expect(countByErrorClass(list)).toEqual({ unreachable: 2, eof: 1 })
    expect(summarize(list).by_error_class).toEqual({ unreachable: 2, eof: 1 })
    expect(summarize([health()])).not.toHaveProperty('by_error_class')
  })
})

describe('skipped % rendering', () => {
  it('rounds, clamps and treats missing as null', () => {
    expect(skippedPct({ motion_skipped_pct: 86.6 })).toBe(87)
    expect(skippedPct({ motion_skipped_pct: 140 })).toBe(100)
    expect(skippedPct({ motion_skipped_pct: -3 })).toBe(0)
    expect(skippedPct({ motion_skipped_pct: null })).toBeNull()
    expect(skippedPct({})).toBeNull()
    expect(skippedPct(undefined)).toBeNull()
    expect(skippedLabel(87)).toBe('Skipped 87 % (unchanged frames)')
  })
  it('column appears only when some camera reports it', () => {
    expect(anyMotionSkipped([health(), health({ motion_skipped_pct: null })])).toBe(false)
    expect(anyMotionSkipped([health(), health({ motion_skipped_pct: 0 })])).toBe(true)
  })
  it('motionOf degrades to disabled defaults', () => {
    expect(motionOf(undefined)).toEqual(DEFAULT_MOTION)
    expect(
      motionOf({ motion: { enabled: true, sensitivity: 'weird' as never, heartbeat_s: 99 } }),
    ).toEqual({ enabled: true, sensitivity: 'medium', heartbeat_s: 5 })
  })
  const cam = (m?: CameraConfig['motion']) => ({ camera_id: 'cam-021', motion: m })
  const wrap = (ui: React.ReactElement) => render(<MemoryRouter>{ui}</MemoryRouter>)
  it('shows the figure and bar when enabled', () => {
    wrap(
      <MotionSummary
        camera={cam({ enabled: true, sensitivity: 'low', heartbeat_s: 10 })}
        health={{ motion_skipped_pct: 87.2 }}
      />,
    )
    expect(screen.getByText('Skipped 87 % (unchanged frames)')).toBeTruthy()
    expect(screen.getByRole('meter').getAttribute('aria-valuenow')).toBe('87')
    expect(screen.getByText(/low sensitivity, heartbeat 10 s/)).toBeTruthy()
  })
  it('shows no figure when disabled, and a placeholder when enabled without data', () => {
    const { unmount } = wrap(<MotionSummary camera={cam()} health={{ motion_skipped_pct: 50 }} />)
    expect(screen.getByText('Off')).toBeTruthy()
    expect(screen.queryByRole('meter')).toBeNull()
    unmount()
    wrap(<MotionSummary camera={cam({ enabled: true })} health={undefined} />)
    expect(screen.getByText(/measuring/)).toBeTruthy()
    expect(screen.queryByRole('meter')).toBeNull()
  })
})
