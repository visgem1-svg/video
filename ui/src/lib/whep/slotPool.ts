/**
 * Caps simultaneous WebRTC sessions (browsers + MediaMTX suffer beyond ~16-25).
 * acquire() resolves with a release function; waiters are served FIFO.
 */
export class SlotPool {
  private active = 0
  private queue: Array<() => void> = []
  constructor(private readonly max: number) {}

  acquire(signal?: AbortSignal): Promise<() => void> {
    return new Promise((resolve, reject) => {
      const grant = () => {
        this.active++
        let released = false
        resolve(() => {
          if (released) return
          released = true
          this.active--
          this.queue.shift()?.()
        })
      }
      if (signal?.aborted) return reject(new DOMException('aborted', 'AbortError'))
      if (this.active < this.max) return grant()
      const waiter = () => grant()
      this.queue.push(waiter)
      signal?.addEventListener('abort', () => {
        const i = this.queue.indexOf(waiter)
        if (i >= 0) {
          this.queue.splice(i, 1)
          reject(new DOMException('aborted', 'AbortError'))
        }
      })
    })
  }

  get inUse() {
    return this.active
  }
}
