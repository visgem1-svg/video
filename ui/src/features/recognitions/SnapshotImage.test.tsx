import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SnapshotImage } from './SnapshotImage'

describe('SnapshotImage', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => vi.useRealTimers())

  it('retries 3 times (1s, 2s, 4s) then shows "no snapshot"', () => {
    render(<SnapshotImage src="/api/snapshots/plate/20261021/x.jpg" alt="p" />)
    expect(screen.getByTestId('snapshot-skeleton')).toBeInTheDocument()
    for (const ms of [1000, 2000, 4000]) {
      fireEvent.error(screen.getByAltText('p'))
      act(() => {
        vi.advanceTimersByTime(ms - 1)
      })
      act(() => {
        vi.advanceTimersByTime(1)
      })
    }
    expect(screen.getByAltText('p').getAttribute('src')).toMatch(/r=3$/)
    fireEvent.error(screen.getByAltText('p'))
    expect(screen.getByText('No snapshot')).toBeInTheDocument()
  })

  it('hides the skeleton once loaded', () => {
    render(<SnapshotImage src="/a.jpg" alt="p" />)
    fireEvent.load(screen.getByAltText('p'))
    expect(screen.queryByTestId('snapshot-skeleton')).toBeNull()
  })
})
