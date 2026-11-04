import { useEffect, useState } from 'react'

/** Seconds elapsed since `active` became true (0 when inactive). */
export function useElapsed(active: boolean): number {
  const [sec, setSec] = useState(0)
  useEffect(() => {
    if (!active) {
      setSec(0)
      return
    }
    const start = Date.now()
    const id = setInterval(() => setSec(Math.floor((Date.now() - start) / 1000)), 1000)
    return () => clearInterval(id)
  }, [active])
  return sec
}

export const formatElapsed = (s: number) =>
  `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
