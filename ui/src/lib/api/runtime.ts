import { useSyncExternalStore } from 'react'
import { MOCK_FORCED } from '../config'
import { hub } from '../realtime/hub'
import { SignalRTransport } from '../realtime/signalrTransport'
import { resetStores, seedHealth, wireStores } from '../realtime/stores'
import { createMockBackend, MockTransport } from '../mock/mockBackend'
import { MockWorld } from '../mock/world'
import { httpBackend, probeGateway } from './httpBackend'
import type { RestApi } from './types'

export type RuntimeMode = 'probing' | 'live' | 'mock'
export type MockReason = 'forced' | 'unreachable' | null

interface RuntimeState {
  mode: RuntimeMode
  reason: MockReason
}
let state: RuntimeState = { mode: 'probing', reason: null }
let impl: RestApi = httpBackend
const listeners = new Set<() => void>()
const setState = (s: RuntimeState) => {
  state = s
  listeners.forEach((l) => l())
}

export const runtime = {
  get: () => state,
  subscribe: (cb: () => void) => {
    listeners.add(cb)
    return () => listeners.delete(cb)
  },
}
export const useRuntime = () => useSyncExternalStore(runtime.subscribe, runtime.get)
export const isMock = () => state.mode === 'mock'

/** The single REST facade used by every feature; swaps between HTTP and mock at runtime. */
export const api: RestApi = new Proxy({} as RestApi, {
  get:
    (_t, key: keyof RestApi) =>
    (...args: unknown[]) =>
      (impl[key] as (...a: unknown[]) => unknown)(...args),
})

const REPROBE_MS = 10_000
let wired = false
let reprobeTimer: ReturnType<typeof setInterval> | null = null

function selectBackend(mock: boolean, reason: MockReason) {
  if (mock) {
    const world = new MockWorld()
    impl = createMockBackend(world)
    hub.attach(new MockTransport(world))
  } else {
    impl = httpBackend
    hub.attach(new SignalRTransport())
  }
  if (!wired) {
    wireStores()
    wired = true
  }
  setState({ mode: mock ? 'mock' : 'live', reason })
  void hub.start()
  void api
    .healthCameras()
    .then(seedHealth)
    .catch(() => {})
}

/**
 * While we fell back to mock because the gateway was unreachable, re-probe every 10 s and
 * switch to the real backend as soon as it answers (banner disappears, data is reloaded).
 */
export function startReprobe(probe: () => Promise<boolean> = probeGateway) {
  if (reprobeTimer) clearInterval(reprobeTimer)
  let busy = false
  reprobeTimer = setInterval(async () => {
    if (busy || state.mode !== 'mock' || state.reason !== 'unreachable') return
    busy = true
    try {
      if (await probe()) {
        resetStores()
        selectBackend(false, null)
        if (reprobeTimer) clearInterval(reprobeTimer)
        reprobeTimer = null
      }
    } finally {
      busy = false
    }
  }, REPROBE_MS)
}

export async function initRuntime(): Promise<RuntimeState> {
  let mock = MOCK_FORCED
  let reason: MockReason = MOCK_FORCED ? 'forced' : null
  if (!mock && !(await probeGateway())) {
    mock = true
    reason = 'unreachable'
  }
  selectBackend(mock, reason)
  if (mock && reason === 'unreachable') startReprobe()
  return state
}
