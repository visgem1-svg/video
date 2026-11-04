/** Leading + trailing throttle. Returns a function with `.cancel()` / `.flush()`. */
export function throttle<A extends unknown[]>(fn: (...args: A) => void, waitMs: number) {
  let last = -Infinity
  let timer: ReturnType<typeof setTimeout> | null = null
  let pending: A | null = null

  const run = (args: A) => {
    last = Date.now()
    pending = null
    fn(...args)
  }
  const throttled = (...args: A) => {
    const remaining = waitMs - (Date.now() - last)
    if (remaining <= 0) {
      if (timer) clearTimeout(timer)
      timer = null
      run(args)
    } else {
      pending = args
      timer ??= setTimeout(() => {
        timer = null
        if (pending) run(pending)
      }, remaining)
    }
  }
  throttled.cancel = () => {
    if (timer) clearTimeout(timer)
    timer = null
    pending = null
  }
  throttled.flush = () => {
    if (timer) clearTimeout(timer)
    timer = null
    if (pending) run(pending)
  }
  return throttled
}

/**
 * Rate gate: `allow()` is true at most once per `minIntervalMs` (drops the rest).
 * Used to cap UI work per camera to the contract's 3 events/s.
 */
export function createRateGate(minIntervalMs: number, now: () => number = () => performance.now()) {
  let last = -Infinity
  return {
    allow(): boolean {
      const t = now()
      if (t - last >= minIntervalMs) {
        last = t
        return true
      }
      return false
    },
    reset() {
      last = -Infinity
    },
  }
}
