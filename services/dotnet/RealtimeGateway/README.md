# RealtimeGateway (port 5005)

SignalR hub `/hubs/live` (JSON protocol, snake_case payloads = the Kafka JSON passed through untouched).
Client -> server: `SubscribeCameras(string[])`, `UnsubscribeCameras(string[])`, `SubscribePlans(string[])`.
Server -> client: `detections` (camera subscribers, <= 3/s per camera, newest wins), `cameraHealth` (all),
`recognition` (all), `pipelineMetrics` (plan subscribers), `planChanged` (all; deleted plan = `{"plan_id":..,"deleted":true}`).
On connect the client receives the current `cameraHealth` of every camera and `planChanged` for every plan.

* `camera.health` / `pipeline.plans` are read fully from the beginning at start (`/health` = 503 until loaded);
  `detections`, `recognitions`, `pipeline.metrics` start at the latest offsets with a unique group id
  `realtime-gateway-<instance>-<rand>` per process (every instance needs every event).
* Cheap fan-out: detections for cameras with no subscribers are dropped before JSON parsing; a message is parsed once and
  serialized once per group send. Throttle = sliding window (3 sends / 1000 ms per camera), a 50 ms flusher releases
  the newest pending message.
* Scaling out needs sticky sessions or a backplane only for the hub connection itself (each instance has all events, no backplane needed).

Env: `KAFKA_BOOTSTRAP` (`redpanda:9092`), `ASPNETCORE_URLS` (`http://+:5005`), `DETECTIONS_MAX_PER_SECOND` (3),
`REALTIME_INSTANCE_ID` (hostname). Metrics: standard prometheus-net/ASP.NET on `/metrics`.

Build/test: `dotnet build RealtimeGateway`, `dotnet test RealtimeGateway.Tests`.
