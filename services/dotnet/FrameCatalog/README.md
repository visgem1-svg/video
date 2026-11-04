# frame-catalog (port 5006, 128 MB)

Powers the "Extracted frames" page. Consumes `frames.raw` and `detections` (group `frame-catalog`, **latest offsets**: the partition-assigned
handler seeks to the end; offsets are stored after each message is applied). Keeps per camera a ring of the last `FRAMES_PER_CAMERA` frame records.
Detections for a frame (main message + `partial:true` messages) are merged by `frame_id`, in any order: per category (objects/plates/faces/texts)
a non-empty incoming array replaces the old one, so redelivery is idempotent. A detection that arrives before its frame waits in a pending map
(`PENDING_TTL_MS`, cap `PENDING_MAX`) and is attached when the frame arrives; detections older than a full ring are dropped.

## API (gateway: `/api/frames/**`)
* `GET /api/frames?camera_id=&before=<ts>&limit=60` -> `{ items:[record], count, next_before }` newest first (`limit` <= 200; `next_before` = ts of the last item when the page is full, else null; pass it as `before`).
* `GET /api/frames/latest?camera_ids=a,b,c` -> `[record]` newest record per camera, O(cameras), unknown cameras omitted.
* `GET /api/frames/stats` -> `[ {camera_id, frames, analyzed, oldest_ts, newest_ts, fps} ]`
* `GET /api/frames/{camera_id}/{frame_id}` -> record plus boxes; 404 if not held.
* `GET /api/frames/{camera_id}/{frame_id}/image` -> JPEG, `Cache-Control: private, max-age=600`. Served from the per-camera LRU, else by fetching that single `frames.raw` message (dedicated consumer: assign + seek (partition, offset), 2 s timeout, fetches serialized; 503 if waiting > 3 s). 404 when unknown or offset below the log start.
* `/health` (503 until partitions are assigned), `/metrics`.

List record: `frame_id, camera_id, ts, seq, width, height, worker_id, analyzed, objects:{class:count}, plates:[{plate,confidence}],
faces:[{confidence,person_id,person_name,score}], texts:[{text,confidence}], total_ms` (`total_ms` null until analyzed).
Detail record: same, but `plates/faces/texts` carry `box` `[x1,y1,x2,y2]` (analysis-frame pixels), and `object_boxes:[{class,confidence,box}]` holds the object boxes (`objects` stays the count map).

## Environment variables
| Var | Default | Meaning |
|---|---|---|
| `KAFKA_BOOTSTRAP` | `redpanda:9092` | |
| `IMAGE_CACHE_PER_CAMERA` | `3` | newest JPEGs kept in memory per camera (0 = none) |
| `FETCH_TIMEOUT_MS` | `2000` | timeout of a single-message Kafka fetch |
| `FRAMES_PER_CAMERA` | `600` | ring size per camera (1..100000) |
| `ASPNETCORE_URLS` | `http://+:5006` | |
| `PENDING_TTL_MS` / `PENDING_MAX` | `30000` / `20000` | pending detections expiry / cap |

Frames come from `frames.raw` (value = JPEG, header `meta` = JSON without object_key). The main consumer never retains bytes except the LRU (100 cameras x 3 x ~40 KB = ~12 MB; 400 cameras ~48 MB: lower `IMAGE_CACHE_PER_CAMERA`); librdkafka prefetch is capped (`queued.max.messages.kbytes=1024`, `fetch.max.bytes=8 MB`). No S3 dependency.

## Memory (400 cameras x 600 = 240,000 records, heap hard limit 96 MB)
* Ring slot = 64-byte struct (Guid, ts, seq, w/h as ushort, interned worker id, Kafka partition + offset). Rings grow 32 -> cap by doubling.
* Detections: one `Dets` object (~48 B) + packed arrays; box = 4 x ushort, confidence = ushort x10000, class = interned ushort id (12 B per object). ~100-130 B for a 4-object frame, 0 B when not analyzed.
* Measured (test `Memory_for_400_cameras_x_600_frames_is_bounded`, 4 objects/frame, all analyzed): **~43 MB**; 10 objects/frame ~ 60 MB. Pending map is capped (20k entries ~ 4 MB). Lower `FRAMES_PER_CAMERA` to shrink linearly.
