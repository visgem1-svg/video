import type {
  CameraHealth,
  Detection,
  PipelineMetrics,
  Plan,
  PlanDeleted,
  Recognition,
} from '../types'

export type HubEvent =
  | { type: 'detections'; data: Detection }
  | { type: 'cameraHealth'; data: CameraHealth }
  | { type: 'recognition'; data: Recognition }
  | { type: 'pipelineMetrics'; data: PipelineMetrics }
  | { type: 'planChanged'; data: Plan | PlanDeleted }

export type HubEventType = HubEvent['type']
export type HubEventData<T extends HubEventType> = Extract<HubEvent, { type: T }>['data']

export type ConnState = 'idle' | 'connecting' | 'connected' | 'reconnecting' | 'disconnected'

export type HubMethod =
  | 'SubscribeCameras'
  | 'UnsubscribeCameras'
  | 'SubscribePlans'
  | 'WatchFrames'
  | 'UnwatchFrames'

/** What the facade needs from a transport (real SignalR or the mock world). */
export interface HubTransport {
  start(): Promise<void>
  stop(): Promise<void>
  invoke(method: HubMethod, ids: string[]): Promise<void>
  onEvent(handler: (e: HubEvent) => void): void
  onState(handler: (s: ConnState) => void): void
}
