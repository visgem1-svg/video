import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createRateGate, throttle } from './throttle'

describe('throttle', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => vi.useRealTimers())
  it('fires leading, coalesces bursts, then fires trailing with the latest args', () => {
    const fn = vi.fn()
    const t = throttle(fn, 100)
    t(1)
    t(2)
    t(3)
    expect(fn).toHaveBeenCalledTimes(1)
    expect(fn).toHaveBeenLastCalledWith(1)
    vi.advanceTimersByTime(100)
    expect(fn).toHaveBeenCalledTimes(2)
    expect(fn).toHaveBeenLastCalledWith(3)
  })
  it('cancel drops the pending call', () => {
    const fn = vi.fn()
    const t = throttle(fn, 100)
    t(1)
    t(2)
    t.cancel()
    vi.advanceTimersByTime(500)
    expect(fn).toHaveBeenCalledTimes(1)
  })
})

describe('rate gate', () => {
  it('allows at most 3 per second', () => {
    let now = 0
    const g = createRateGate(333, () => now)
    let allowed = 0
    for (now = 0; now < 1000; now += 50) if (g.allow()) allowed++
    expect(allowed).toBe(3)
  })
})
