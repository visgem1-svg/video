import type {
  CameraConfig,
  CameraHealth,
  CameraInput,
  DraftRequest,
  DraftResponse,
  HealthSummary,
  Person,
  Plan,
  RecognitionQuery,
  RecognitionRecord,
  ToolsDoc,
  Worker,
} from '../types'

/** Every REST call the UI makes. Implemented by the HTTP backend and the mock backend. */
export interface RestApi {
  listCameras(): Promise<CameraConfig[]>
  createCamera(c: CameraInput): Promise<CameraConfig>
  updateCamera(id: string, c: CameraInput): Promise<CameraConfig>
  deleteCamera(id: string): Promise<void>
  listWorkers(): Promise<Worker[]>

  healthCameras(): Promise<CameraHealth[]>
  healthSummary(): Promise<HealthSummary>

  listRecognitions(q: RecognitionQuery): Promise<RecognitionRecord[]>

  draftPlan(
    req: DraftRequest,
    signal?: AbortSignal,
    opts?: { refresh?: boolean },
  ): Promise<DraftResponse>
  recentDrafts(limit?: number): Promise<string[]>
  activatePlan(plan: Plan, signal?: AbortSignal): Promise<Plan>
  listPlans(): Promise<Plan[]>
  getPlan(id: string): Promise<Plan>
  deletePlan(id: string): Promise<void>
  listTools(): Promise<ToolsDoc>

  listPersons(): Promise<Person[]>
  enrollPerson(name: string, image: File): Promise<Person>
  addPersonPhoto(id: string, image: File): Promise<Person>
  renamePerson(id: string, name: string): Promise<Person>
  deletePerson(id: string): Promise<void>
}

export class ApiError extends Error {
  constructor(
    message: string,
    public status: number,
    public body?: unknown,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}
