# BUG-009: health last frame age misleading

- Severity: minor
- Page: Health, Focus
- Suspected component: stream-health / ui

## Steps to reproduce
Open /health, compare 'Last frame' with 'FPS'.

## Expected
Last frame <= ~1 s for cameras at ~3 fps.

## Actual
Online cameras with fps 2.8-3.1 show 'Last frame 8s/9s' (cam-003, 005, 006, 009); API last_frame_ts is up to 3.7 s old. Value only refreshes with the 5 s camera.status.

## Evidence
screenshots\t03-health.png.

## Hint
Extractor could report real last-frame time more often, or UI should show the granularity.
