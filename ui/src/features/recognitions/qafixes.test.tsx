import { act, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { resolveLocation } from '@/lib/whep/whepClient'
import { confidenceTone } from './RecognitionsPage'
import { SnapshotImage } from './SnapshotImage'
import { loadTone } from '@/features/sources/SourcesPage'
import { reconnectTone, uptimeTone } from '@/features/health/health'
import { ageTone, colorFor, isDashed, pillText, showLabel } from '@/lib/overlay'
import { clearToasts, toast, TOAST_MS } from '@/lib/toast'

describe('BUG-003 WHEP Location', () => {
  it('re-adds the gateway /media prefix to an absolute-path Location', () => {
    expect(resolveLocation('/cam-002/whep/abc', 'http://localhost:8000/media/cam-002/whep')).toBe(
      'http://localhost:8000/media/cam-002/whep/abc',
    )
  })
  it('keeps already-prefixed and absolute locations', () => {
    expect(resolveLocation('/media/cam-002/whep/abc', 'http://gw/media/cam-002/whep')).toBe(
      'http://gw/media/cam-002/whep/abc',
    )
    expect(resolveLocation('http://x/y/z', 'http://gw/media/c/whep')).toBe('http://x/y/z')
  })
})

describe('BUG-006 delayed first snapshot request', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => vi.useRealTimers())
  it('renders no <img> (no request) until the initial delay passed', () => {
    render(
      <SnapshotImage src="/api/snapshots/plate/20261021/x.jpg" alt="p" initialDelayMs={1500} />,
    )
    expect(screen.queryByAltText('p')).toBeNull()
    expect(screen.getByTestId('snapshot-skeleton')).toBeInTheDocument()
    act(() => {
      vi.advanceTimersByTime(1500)
    })
    expect(screen.getByAltText('p')).toBeInTheDocument()
  })
  it('does not delay inline data URIs', () => {
    render(<SnapshotImage src="data:image/svg+xml;utf8,x" alt="p" initialDelayMs={1500} />)
    expect(screen.getByAltText('p')).toBeInTheDocument()
  })
})

describe('design-system helpers', () => {
  it('confidence / uptime / reconnect / load / age tones', () => {
    expect(confidenceTone(0.95)).toBe('ok')
    expect(confidenceTone(0.8)).toBe('warn')
    expect(confidenceTone(0.4)).toBe('err')
    expect(uptimeTone(99.5)).toBe('ok')
    expect(uptimeTone(97)).toBe('warn')
    expect(uptimeTone(90)).toBe('err')
    expect(reconnectTone(5)).toBe('ok')
    expect(reconnectTone(6)).toBe('warn')
    expect(loadTone(0.5)).toBe('bg-ok')
    expect(loadTone(0.8)).toBe('bg-warn')
    expect(loadTone(0.95)).toBe('bg-err')
    expect(ageTone(2000)).toBe('neutral')
    expect(ageTone(10_000)).toBe('warn')
    expect(ageTone(20_000)).toBe('err')
  })
  it('overlay palette: class colours, dashed read results, known-face check, label rule', () => {
    expect(colorFor({ kind: 'object', key: 'car' })).toBe('#A3E635')
    expect(colorFor({ kind: 'object', key: 'zebra' })).toMatch(/^hsl/)
    expect(colorFor({ kind: 'plate', key: 'plate' })).toBe('#FACC15')
    expect(isDashed({ kind: 'plate' })).toBe(true)
    expect(isDashed({ kind: 'object' })).toBe(false)
    expect(pillText({ kind: 'face_known', label: 'Dana' })).toBe('✓ Dana')
    const small = { x: 0, y: 0, w: 100, h: 50 }
    expect(showLabel({ kind: 'object', rect: small }, 800)).toBe(false)
    expect(showLabel({ kind: 'plate', rect: small }, 800)).toBe(true)
    expect(showLabel({ kind: 'plate', rect: small }, 120)).toBe(false)
  })
})

describe('toasts', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    clearToasts()
  })
  afterEach(() => vi.useRealTimers())
  it('exposes durations: errors live longer than successes', () => {
    expect(TOAST_MS.error).toBeGreaterThan(TOAST_MS.success)
    toast.success('x')
    expect(TOAST_MS.success).toBe(4000)
  })
})
