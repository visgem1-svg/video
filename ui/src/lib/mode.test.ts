import { describe, expect, it } from 'vitest'
import { effectiveMode, framesAtFullRate, modeOf, normalizeHealth } from './mode'
import type { CameraHealth } from './types'

describe('mode mapping', () => {
  it('missing fields mean full', () => {
    expect(modeOf(undefined)).toBe('full')
    expect(effectiveMode(undefined, undefined)).toBe('full')
    expect(effectiveMode({}, {})).toBe('full')
  })
  it('live health wins over camera config', () => {
    expect(effectiveMode({ mode: 'full' }, { extract: { mode: 'idle' } })).toBe('full')
    expect(effectiveMode({}, { extract: { mode: 'idle' } })).toBe('idle')
    expect(effectiveMode({ mode: 'idle' }, undefined)).toBe('idle')
  })
  it('folds a state "idle" into online + mode idle', () => {
    const h = { state: 'idle', camera_id: 'c' } as unknown as CameraHealth
    expect(normalizeHealth(h)).toMatchObject({ state: 'online', mode: 'idle' })
    const ok = { state: 'online' } as CameraHealth
    expect(normalizeHealth(ok)).toBe(ok)
  })
  it('detects full-rate frames', () => {
    const now = 100_000
    expect(framesAtFullRate([], now)).toBe(false)
    expect(framesAtFullRate([now - 100, now - 5100], now)).toBe(false)
    expect(framesAtFullRate([now - 100, now - 400], now)).toBe(true)
    expect(framesAtFullRate([now - 9000, now - 9300], now)).toBe(false)
  })
})
