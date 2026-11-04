# BUG-006: live recognition snapshot 404 console errors

- Severity: minor
- Page: Recognitions (live), Focus
- Suspected component: ui / realtime-gateway

## Steps to reproduce
Open /recognitions (Live tab) for 25 s and check the console.

## Expected
No console errors / failed requests in normal use.

## Actual
30+ `404` on `/api/snapshots/...jpg` (and `?r=1`) per minute: the image is requested at event time, Results copies it 0.3-1.2 s later (median 0.3 s over 118 samples). UI retries so images appear, but console/network are flooded and cards briefly show broken-image alt text.

## Evidence
screenshots\t04-live-after25s.png, tests\t05_snapshot_delay.py (118/118 reach 200 within 1.2 s).

## Hint
Delay the first `<img>` request ~1.5 s for live events (SnapshotImage) or send snapshot_url only once it exists (backlog #12); hide alt text while loading.
