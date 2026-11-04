import { useSyncExternalStore } from 'react'
import type { Detection } from '../types'
import type { ConnState, HubEvent, HubEventData, HubEventType, HubTransport } from './types'

type Handler<T extends HubEventType> = (data: HubEventData<T>) => void

/**
 * Facade over a HubTransport:
 *  - ref-counted camera/plan subscriptions (a camera shown in the wall AND the focus view is
 *    subscribed once on the server),
 *  - Subscribe/Unsubscribe calls are batched (one hub call per 100 ms, however many tiles
 *    scrolled in/out),
 *  - desired subscriptions are replayed after a reconnect,
 *  - per-camera detection dispatch so only the tiles of that camera run any code.
 */
export class Hub {
  private transport: HubTransport | null = null
  private state: ConnState = 'idle'
  private stateListeners = new Set<() => void>()
  private handlers = new Map<HubEventType, Set<(d: never) => void>>()
  private detHandlers = new Map<string, Set<(d: Detection) => void>>()

  private camRefs = new Map<string, number>()
  private planRefs = new Map<string, number>()
  private serverCams = new Set<string>()
  private serverPlans = new Set<string>()
  private frameRefs = new Map<string, number>()
  private serverFrames = new Set<string>()
  private framesWarned = false
  private flushTimer: ReturnType<typeof setTimeout> | null = null

  constructor(private readonly flushMs = 100) {}

  /** Attach a transport (call before start). Previous transport is stopped. */
  attach(t: HubTransport) {
    if (this.transport) {
      this.transport.onEvent(() => {})
      this.transport.onState(() => {})
      void this.transport.stop().catch(() => {})
    }
    if (this.retryTimer) clearTimeout(this.retryTimer)
    this.transport = t
    this.serverCams.clear()
    this.serverPlans.clear()
    this.serverFrames.clear()
    t.onEvent((e) => this.dispatch(e))
    t.onState((s) => {
      this.setState(s)
      if (s === 'connected') {
        this.serverCams.clear()
        this.serverPlans.clear()
        this.serverFrames.clear()
        this.scheduleFlush(0)
      }
    })
  }

  private stopped = false
  private retryTimer: ReturnType<typeof setTimeout> | null = null

  async start() {
    if (!this.transport) return
    this.stopped = false
    try {
      await this.transport.start()
    } catch {
      this.setState('disconnected')
      if (!this.stopped) this.retryTimer = setTimeout(() => void this.start(), 3000)
    }
  }

  async stop() {
    this.stopped = true
    if (this.retryTimer) clearTimeout(this.retryTimer)
    await this.transport?.stop()
    this.setState('idle')
  }

  // ---- state (for useSyncExternalStore)
  getState = () => this.state
  subscribeState = (cb: () => void) => {
    this.stateListeners.add(cb)
    return () => this.stateListeners.delete(cb)
  }
  private setState(s: ConnState) {
    if (s === this.state) return
    this.state = s
    this.stateListeners.forEach((l) => l())
  }

  // ---- events
  on<T extends HubEventType>(type: T, handler: Handler<T>): () => void {
    let set = this.handlers.get(type)
    if (!set) this.handlers.set(type, (set = new Set()))
    set.add(handler as (d: never) => void)
    return () => set.delete(handler as (d: never) => void)
  }

  onDetections(cameraId: string, handler: (d: Detection) => void): () => void {
    let set = this.detHandlers.get(cameraId)
    if (!set) this.detHandlers.set(cameraId, (set = new Set()))
    set.add(handler)
    return () => {
      set.delete(handler)
      if (set.size === 0) this.detHandlers.delete(cameraId)
    }
  }

  private dispatch(e: HubEvent) {
    if (e.type === 'detections') {
      this.detHandlers.get(e.data.camera_id)?.forEach((h) => h(e.data))
    }
    this.handlers.get(e.type)?.forEach((h) => (h as (d: unknown) => void)(e.data))
  }

  // ---- subscriptions
  subscribeCamera(id: string): () => void {
    this.camRefs.set(id, (this.camRefs.get(id) ?? 0) + 1)
    this.scheduleFlush()
    let done = false
    return () => {
      if (done) return
      done = true
      const n = (this.camRefs.get(id) ?? 1) - 1
      if (n <= 0) this.camRefs.delete(id)
      else this.camRefs.set(id, n)
      this.scheduleFlush()
    }
  }

  /**
   * Demand signal for the extracted-frames views (contract 9.8): ref-counted and batched like
   * subscribeCamera. A backend without WatchFrames is tolerated (see flush).
   */
  watchFrames(id: string): () => void {
    this.frameRefs.set(id, (this.frameRefs.get(id) ?? 0) + 1)
    this.scheduleFlush()
    let done = false
    return () => {
      if (done) return
      done = true
      const n = (this.frameRefs.get(id) ?? 1) - 1
      if (n <= 0) this.frameRefs.delete(id)
      else this.frameRefs.set(id, n)
      this.scheduleFlush()
    }
  }

  /** The contract has no UnsubscribePlans; plans stay subscribed until the page unloads. */
  subscribePlan(id: string): () => void {
    this.planRefs.set(id, (this.planRefs.get(id) ?? 0) + 1)
    this.scheduleFlush()
    return () => {
      const n = (this.planRefs.get(id) ?? 1) - 1
      if (n <= 0) this.planRefs.delete(id)
      else this.planRefs.set(id, n)
    }
  }

  private scheduleFlush(ms = this.flushMs) {
    if (this.flushTimer) return
    this.flushTimer = setTimeout(() => {
      this.flushTimer = null
      void this.flush()
    }, ms)
  }

  /** Visible for tests. */
  async flush() {
    const t = this.transport
    if (!t) return
    const wantCams = new Set(this.camRefs.keys())
    const add = [...wantCams].filter((c) => !this.serverCams.has(c))
    const del = [...this.serverCams].filter((c) => !wantCams.has(c))
    const addPlans = [...this.planRefs.keys()].filter((p) => !this.serverPlans.has(p))
    try {
      if (del.length) await t.invoke('UnsubscribeCameras', del)
      if (add.length) await t.invoke('SubscribeCameras', add)
      if (addPlans.length) await t.invoke('SubscribePlans', addPlans)
      del.forEach((c) => this.serverCams.delete(c))
      add.forEach((c) => this.serverCams.add(c))
      addPlans.forEach((p) => this.serverPlans.add(p))
    } catch {
      /* not connected yet: replayed on 'connected' */
    }
    await this.flushFrames(t)
  }

  /** Separate from the camera sync: an older gateway without WatchFrames must not break it. */
  private async flushFrames(t: HubTransport) {
    const want = new Set(this.frameRefs.keys())
    const add = [...want].filter((c) => !this.serverFrames.has(c))
    const del = [...this.serverFrames].filter((c) => !want.has(c))
    try {
      if (del.length) await t.invoke('UnwatchFrames', del)
      if (add.length) await t.invoke('WatchFrames', add)
      del.forEach((c) => this.serverFrames.delete(c))
      add.forEach((c) => this.serverFrames.add(c))
    } catch (e) {
      if (this.state === 'connected') {
        // connected but the call failed: gateway predates v1.8. Pretend it worked, log once.
        del.forEach((c) => this.serverFrames.delete(c))
        add.forEach((c) => this.serverFrames.add(c))
        if (!this.framesWarned) {
          this.framesWarned = true
          console.debug('WatchFrames not available on the gateway; running without demand signal', e)
        }
      }
    }
  }
}

export const hub = new Hub()

export function useHubState(): ConnState {
  return useSyncExternalStore(hub.subscribeState, hub.getState)
}
