import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { API_BASE } from '../config'
import type { ConnState, HubEvent, HubMethod, HubTransport } from './types'

const EVENTS = [
  'detections',
  'cameraHealth',
  'recognition',
  'pipelineMetrics',
  'planChanged',
] as const

export class SignalRTransport implements HubTransport {
  private conn = new HubConnectionBuilder()
    .withUrl(`${API_BASE}/hubs/live`)
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: (ctx) =>
        Math.min(30_000, 1000 * 2 ** Math.min(ctx.previousRetryCount, 5)) + Math.random() * 500,
    })
    .configureLogging(LogLevel.Warning)
    .build()
  private eventHandler: (e: HubEvent) => void = () => {}
  private stateHandler: (s: ConnState) => void = () => {}

  constructor() {
    for (const type of EVENTS) {
      this.conn.on(type, (data) => this.eventHandler({ type, data } as HubEvent))
    }
    this.conn.onreconnecting(() => this.stateHandler('reconnecting'))
    this.conn.onreconnected(() => this.stateHandler('connected'))
    this.conn.onclose(() => this.stateHandler('disconnected'))
  }

  onEvent(h: (e: HubEvent) => void) {
    this.eventHandler = h
  }
  onState(h: (s: ConnState) => void) {
    this.stateHandler = h
  }

  async start() {
    this.stateHandler('connecting')
    await this.conn.start()
    this.stateHandler('connected')
  }
  async stop() {
    await this.conn.stop()
  }
  async invoke(method: HubMethod, ids: string[]) {
    if (this.conn.state !== HubConnectionState.Connected || ids.length === 0) return
    await this.conn.invoke(method, ids)
  }
}
