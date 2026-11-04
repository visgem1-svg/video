# BUG-002: detection latency and update rate

- Severity: major
- Page: Live wall / Focus
- Suspected component: executor (+ realtime-gateway, CPU saturation)

## Steps to reproduce
Open the live wall (3x3) or /live/cam-005 for 40 s. Run `tests\t02_detection_latency.py / 40` (hooks WebSocket, compares detection `ts` with browser receive time) and `tests\t13_focus_boxes.py cam-005`.

## Expected
Boxes <= ~1 s behind the live video (contract 9.3); updates up to 3/s per camera.

## Actual
Latency (browser receive minus detection.ts): min 1.8 s, median 2.9 s, p90 3.9 s, max 3.9 s (host/container clock skew < 1 s). Only 11 detection messages in 40 s for 9 visible cameras; in Focus a box appears every ~6 s (gaps 5.7/6.1/6.3/18.7 s). The video pans/zooms, so drawn boxes sit on a position several seconds old (car box misaligned). Prometheus at the same time: executor processed ~2.8 frames/s in total for 20 cameras, skipped ~43/s `superseded` and ~12/s `stale`; avg step time detect_objects 1.0 s, read_plate 0.7 s, identify_faces 0.8 s (detector container ~370% CPU, extractors ~190%).

## Evidence
screenshots\t13-focus-box-cam-005.png; output of t02/t13. The manager already knows (b) hub delivers ~20% and (c) overlay fade; this is about end-to-end age and per-camera rate.

## Hint
Inline steps run sequentially (detect 1.0 s + plate 0.7 s + face 0.8 s = ~2.5-3 s) and the main `detections` message is published only after the whole inline chain (FramePipeline/PlanRunner). Publish the detect_objects result immediately and dependent steps as `partial` messages (UI already merges them). Laptop CPU is saturated, so also reduce analysis_fps / camera count for local runs.
