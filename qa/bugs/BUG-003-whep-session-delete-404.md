# BUG-003: whep session delete 404

- Severity: minor
- Page: Live wall / Focus (any video)
- Suspected component: ui (+ api-gateway)

## Steps to reproduce
Open the wall, change layout/page or leave the page. Watch the network log.

## Expected
WHEP session closed with `DELETE {Location}` through the gateway (`/media/<cam>/whep/<id>`), 2xx.

## Actual
Every teardown sends `DELETE http://localhost:8000/cam-002/whep/<uuid>` which returns 404 (missing `/media` prefix), logged as ERR_ABORTED/404. MediaMTX sessions are only freed by ICE timeout (1 session still listed right after the test closed).

## Evidence
Network log printed by t06/t09: `DELETE http://localhost:8000/cam-002/whep/9b924226-... 404`.

## Hint
`ui/src/lib/whep/whepClient.ts`: `new URL(loc, this.url)` with an absolute-path Location (`/cam-002/whep/<id>`) drops the `/media` prefix that the gateway strips. Resolve against the `/media` base or rewrite Location in YARP.
