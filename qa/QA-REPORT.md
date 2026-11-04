# QA report

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
| BUG-001 | major | Live wall | live wall layout 1 blank | ui |
| BUG-002 | major | Live wall / Focus | detection latency and update rate | executor (+ realtime-gateway, CPU saturation) |
| BUG-003 | minor | Live wall / Focus (any video) | whep session delete 404 | ui (+ api-gateway) |
| BUG-004 | major | Pipelines > Active plans > Watch | plan delete without confirmation | ui |
| BUG-005 | minor | Pipelines (Active list, plan header) | plan camera ids stale after camera assignment | registry / planner |
| BUG-006 | minor | Recognitions (live), Focus | live recognition snapshot 404 console errors | ui / realtime-gateway |
| BUG-007 | minor | Recognitions | plate ocr truncated values | engines (ocr/lpr) |
| BUG-008 | minor | Sources > Add/Edit camera | camera dialog labels not associated | ui |
| BUG-009 | minor | Health, Focus | health last frame age misleading | stream-health / ui |
| BUG-010 | minor | Recognitions (Live tab) | recognitions empty filter message | ui |

## Not tested
provision_error badge; hub reconnect after a real gateway restart (CDP offline does not drop an established WebSocket); mock fallback when backend is down (cannot stop Docker); per-camera pipeline watch panel in /live/:id (not deployed when tested; only a 'Watch' link to Pipelines exists); invalid LLM drafts.

## Re-run
`qa\.venv\Scripts\python.exe qa\tests\tNN_*.py` (in Git Bash set `MSYS_NO_PATHCONV=1` when passing `/`). `tests\make_reports.py` regenerates this file.
