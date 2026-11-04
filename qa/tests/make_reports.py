"""Generates qa/bugs/*.md and qa/QA-REPORT.md (documentation helper)."""
import os
os.chdir(os.path.join(os.path.dirname(__file__), ".."))
os.makedirs("bugs", exist_ok=True)
bugs = [
("001","live-wall-layout-1-blank","major","Live wall","ui",
 "Select layout `1` on the Live wall.",
 "One large camera tile (max-w-5xl, centered) with live video.",
 "Page is empty. The tile exists but is 4x2 px (video element 1.5x0 px), so nothing is visible at 1920x1080 and 1280x720. A `<video>` is still created and connected to WebRTC (wasted session).",
 "screenshots\\t09-layout-1.png, screenshots\\t10-layout1-1920.png; tests\\t10_layout1.py prints the grid chain `grid gap-3 grid-cols-1 max-w-5xl mx-auto` = 4x2 px.",
 "`ui/src/features/live/LiveWallPage.tsx` COLS[1] = 'grid-cols-1 max-w-5xl mx-auto': the grid is a child of a `flex flex-col` container, so `mx-auto` makes it shrink-to-fit and the `aspect-video` tile has no intrinsic width. Add `w-full`."),
("002","detection-latency-and-update-rate","major","Live wall / Focus","executor (+ realtime-gateway, CPU saturation)",
 "Open the live wall (3x3) or /live/cam-005 for 40 s. Run `tests\\t02_detection_latency.py / 40` (hooks WebSocket, compares detection `ts` with browser receive time) and `tests\\t13_focus_boxes.py cam-005`.",
 "Boxes <= ~1 s behind the live video (contract 9.3); updates up to 3/s per camera.",
 "Latency (browser receive minus detection.ts): min 1.8 s, median 2.9 s, p90 3.9 s, max 3.9 s (host/container clock skew < 1 s). Only 11 detection messages in 40 s for 9 visible cameras; in Focus a box appears every ~6 s (gaps 5.7/6.1/6.3/18.7 s). The video pans/zooms, so drawn boxes sit on a position several seconds old (car box misaligned). Prometheus at the same time: executor processed ~2.8 frames/s in total for 20 cameras, skipped ~43/s `superseded` and ~12/s `stale`; avg step time detect_objects 1.0 s, read_plate 0.7 s, identify_faces 0.8 s (detector container ~370% CPU, extractors ~190%).",
 "screenshots\\t13-focus-box-cam-005.png; output of t02/t13. The manager already knows (b) hub delivers ~20% and (c) overlay fade; this is about end-to-end age and per-camera rate.",
 "Inline steps run sequentially (detect 1.0 s + plate 0.7 s + face 0.8 s = ~2.5-3 s) and the main `detections` message is published only after the whole inline chain (FramePipeline/PlanRunner). Publish the detect_objects result immediately and dependent steps as `partial` messages (UI already merges them). Laptop CPU is saturated, so also reduce analysis_fps / camera count for local runs."),
("003","whep-session-delete-404","minor","Live wall / Focus (any video)","ui (+ api-gateway)",
 "Open the wall, change layout/page or leave the page. Watch the network log.",
 "WHEP session closed with `DELETE {Location}` through the gateway (`/media/<cam>/whep/<id>`), 2xx.",
 "Every teardown sends `DELETE http://localhost:8000/cam-002/whep/<uuid>` which returns 404 (missing `/media` prefix), logged as ERR_ABORTED/404. MediaMTX sessions are only freed by ICE timeout (1 session still listed right after the test closed).",
 "Network log printed by t06/t09: `DELETE http://localhost:8000/cam-002/whep/9b924226-... 404`.",
 "`ui/src/lib/whep/whepClient.ts`: `new URL(loc, this.url)` with an absolute-path Location (`/cam-002/whep/<id>`) drops the `/media` prefix that the gateway strips. Resolve against the `/media` base or rewrite Location in YARP."),
("004","plan-delete-without-confirmation","major","Pipelines > Active plans > Watch","ui",
 "Pipelines > Active plans, select a plan, click `Delete`.",
 "Confirmation dialog (Sources has one for cameras).",
 "The plan is deleted immediately (t08: 'NO CONFIRM DIALOG'). One mis-click removes the main plan and resets all its cameras to the default plan.",
 "tests\\t08_watch_delete.py output.",
 "`ui/src/features/pipelines/PipelineWatch.tsx` ~line 98: `onClick={() => del.mutate(...)}`. Reuse the confirm dialog from SourcesPage."),
("005","plan-camera-ids-stale-after-camera-assignment","minor","Pipelines (Active list, plan header)","registry / planner",
 "Create a camera with plan_id = the active plan (Sources dialog or `POST /api/cameras`), then open Pipelines.",
 "Plan shows 21 cameras / `camera_ids` includes the new camera.",
 "The camera runs that plan (executor uses camera.plan_id) but `GET /api/plans/{id}` `camera_ids` still lists 20 and the UI says '20 cameras'.",
 "Repro in t12 preamble (curl POST with plan_id, then GET plan).",
 "plan.camera_ids is only written by the planner on activation; registry plan_id changes do not update it. Derive from cameras in the UI or have the planner follow `camera.config`."),
("006","live-recognition-snapshot-404-console-errors","minor","Recognitions (live), Focus","ui / realtime-gateway",
 "Open /recognitions (Live tab) for 25 s and check the console.",
 "No console errors / failed requests in normal use.",
 "30+ `404` on `/api/snapshots/...jpg` (and `?r=1`) per minute: the image is requested at event time, Results copies it 0.3-1.2 s later (median 0.3 s over 118 samples). UI retries so images appear, but console/network are flooded and cards briefly show broken-image alt text.",
 "screenshots\\t04-live-after25s.png, tests\\t05_snapshot_delay.py (118/118 reach 200 within 1.2 s).",
 "Delay the first `<img>` request ~1.5 s for live events (SnapshotImage) or send snapshot_url only once it exists (backlog #12); hide alt text while loading."),
("007","plate-ocr-truncated-values","minor","Recognitions","engines (ocr/lpr)",
 "`GET /api/recognitions?type=plate&limit=200`.",
 "The sim plate always reads 'PL8REC'.",
 "159 x 'PL8REC', 22 x 'PL8R', 19 x 'PL8RE' (20% truncated); they show in the list and plate search for 'PL8REC' misses them.",
 "API output; card 'PL8R' in screenshots\\t03-recog.png.",
 "OCR/LPR on partially cropped plates (plate at frame edge while the sim video pans). Consider min length / confidence filter."),
("008","camera-dialog-labels-not-associated","minor","Sources > Add/Edit camera","ui",
 "Open Add camera; use the accessibility tree or `getByLabel('Camera id')`.",
 "Inputs are labelled (htmlFor/id).",
 "`<Label>` is not bound to its input, so screen readers and `getByLabel` find nothing (tests use placeholders).",
 "tests\\t06_sources.py (getByLabel timed out).",
 "`ui/src/features/sources/CameraDialog.tsx` Field(): generate an id with useId and set htmlFor/id."),
("009","health-last-frame-age-misleading","minor","Health, Focus","stream-health / ui",
 "Open /health, compare 'Last frame' with 'FPS'.",
 "Last frame <= ~1 s for cameras at ~3 fps.",
 "Online cameras with fps 2.8-3.1 show 'Last frame 8s/9s' (cam-003, 005, 006, 009); API last_frame_ts is up to 3.7 s old. Value only refreshes with the 5 s camera.status.",
 "screenshots\\t03-health.png.",
 "Extractor could report real last-frame time more often, or UI should show the granularity."),
("010","recognitions-empty-filter-message","minor","Recognitions (Live tab)","ui",
 "Type 'zzzz' in the plate search.",
 "'No matches' message.",
 "Shows 'Waiting for recognitions...' (the no-data message) although recognitions exist.",
 "tests\\t04_recog.py output.",
 "`RecognitionsPage.tsx` empty state does not distinguish filtered-empty from no data."),
]
for n, slug, sev, page, comp, steps, exp, act, ev, hint in bugs:
    with open(f"bugs/BUG-{n}-{slug}.md", "w", encoding="utf-8") as f:
        f.write(f"# BUG-{n}: {slug.replace('-', ' ')}\n\n- Severity: {sev}\n- Page: {page}\n- Suspected component: {comp}\n\n"
                f"## Steps to reproduce\n{steps}\n\n## Expected\n{exp}\n\n## Actual\n{act}\n\n## Evidence\n{ev}\n\n## Hint\n{hint}\n")
rows = "\n".join(f"| BUG-{n} | {sev} | {page} | {slug.replace('-', ' ')} | {comp} |" for n, slug, sev, page, comp, *_ in bugs)
report = f"""# QA report

Environment note: the stack was unreachable for ~4 min during the session (Docker port forwarding broke, restarted by the manager). All results below were taken after the restart.

## User bugs
| # | Bug | Status | Evidence |
|---|---|---|---|
| 1 | Boxes on live video / red square | PARTIAL. Red square gone. Boxes are drawn on a canvas over the video (Focus: car + plate boxes) but appear only every ~6 s per camera, last ~1 s and are misaligned with the moving video because of latency. Layout `1` is blank (BUG-001). | t13, BUG-001, BUG-002 |
| 2 | Query by prompt | FIXED. Draft (74 s) -> valid plan graph -> Activate -> listed in Active plans -> lobby-cam plan_id switched, other cameras untouched -> Watch shows live metrics -> Delete clears camera. Delete has no confirmation (BUG-004). | t07, t08 |
| 3 | Add sources | FIXED. UI add/edit/delete, id validation (client + API 400), duplicate id, stream_url validation, new camera has live video on wall/focus, health online, MediaMTX path created/removed, edit keeps plan_id, enable toggle works. Not testable: provision_error badge. | t06, t12 |
| 4 | Detection delay | NOT FIXED: median 2.9 s (1.8-3.9 s) at the browser, target 1 s. | BUG-002, t02 |
| 5 | Recognition snapshots | FIXED. snapshot_url on all rows, 118/118 live snapshots 200 within 1.2 s (median 0.3 s), images render in Live and History. Cosmetic 404 console noise (BUG-006). | t04, t05 |

## Checked OK
No mock banner with backend up; 0 pageerrors; one hub WebSocket; layouts 2x2/3x3/4x4 play all videos (readyState 4, 960x540, time advances); paging and state filter; JS heap flat (~10 MB) and DOM nodes constant over 2 min on 4x4; no horizontal scroll at 390 px; focus prev/next and unknown-camera message; Recognitions filters, plate search, history; Health and workers panel match the API; sources add/edit/delete.

## Bugs
| ID | Severity | Page | Title | Component |
|---|---|---|---|---|
{rows}

## Not tested
provision_error badge; hub reconnect after a real gateway restart (CDP offline does not drop an established WebSocket); mock fallback when backend is down (cannot stop Docker); per-camera pipeline watch panel in /live/:id (not deployed when tested; only a 'Watch' link to Pipelines exists); invalid LLM drafts.

## Re-run
`qa\\.venv\\Scripts\\python.exe qa\\tests\\tNN_*.py` (in Git Bash set `MSYS_NO_PATHCONV=1` when passing `/`). `tests\\make_reports.py` regenerates this file.
"""
open("QA-REPORT.md", "w", encoding="utf-8").write(report)
print("ok")
