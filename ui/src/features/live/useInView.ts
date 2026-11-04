import { useEffect, useState, type RefObject } from 'react'

/**
 * True while the element intersects the viewport AND the browser tab is visible.
 * Drives "only play / subscribe what you can see".
 */
export function useInView(ref: RefObject<Element | null>, threshold = 0.15): boolean {
  const [intersecting, setIntersecting] = useState(false)
  const [tabVisible, setTabVisible] = useState(
    typeof document === 'undefined' || document.visibilityState !== 'hidden',
  )

  useEffect(() => {
    const el = ref.current
    if (!el) return
    if (typeof IntersectionObserver === 'undefined') {
      setIntersecting(true)
      return
    }
    const io = new IntersectionObserver(
      (entries) => setIntersecting(entries[entries.length - 1].isIntersecting),
      { threshold },
    )
    io.observe(el)
    return () => io.disconnect()
  }, [ref, threshold])

  useEffect(() => {
    const onVis = () => setTabVisible(document.visibilityState !== 'hidden')
    document.addEventListener('visibilitychange', onVis)
    return () => document.removeEventListener('visibilitychange', onVis)
  }, [])

  return intersecting && tabVisible
}
