# ApiGateway (port 8000, YARP)

| Path | Upstream |
|---|---|
| `/api/cameras/**`, `/api/workers/**` | `REGISTRY_URL` |
| `/api/health/**` | `STREAM_HEALTH_URL` |
| `/api/recognitions/**`, `/api/detections/**`, `/api/snapshots/**` | `RESULTS_URL` |
| `/api/plans/**`, `/api/tools` | `PLANNER_URL` |
| `/hubs/**` (WebSockets + negotiate) | `REALTIME_GATEWAY_URL` |
| `/media/**` (prefix `/media` removed) | `MEDIAMTX_WHEP_URL` |
| `/api/persons/**`, `/api/persons` (prefix `/api` removed) | `FACE_URL` |
| `/api/frames/**`, `/api/frames` (no path transform) | `FRAME_CATALOG_URL` |
| `/api/autoscaler/**`, `/api/autoscaler` (no path transform) | `WORKER_CONTROLLER_URL` |

WHEP: POST/PATCH/DELETE pass through; the `Location` header of the 201 answer is rewritten under `/media` so the browser
keeps using the gateway. Upstream CORS headers are replaced by the gateway policy (`CORS_ORIGINS`, comma list, default
`http://localhost:5173`; credentials allowed for SignalR; `Location`/`Link`/`ETag` exposed). `/health` reports only the
gateway itself. Defaults: `http://registry:5001`, `stream-health:5002`, `results:5004`, `planner:8010`,
`realtime-gateway:5005`, `mediamtx:8889`, `face:8104`, `frame-catalog:5006`, `worker-controller:5007`. `ASPNETCORE_URLS` default `http://+:8000`.

Build/test: `dotnet build ApiGateway`, `dotnet test ApiGateway.Tests`.
