# Vision Console (UI)

React 19 + TypeScript (strict) + Vite + Tailwind v4 + Radix/shadcn-style components, TanStack Query,
SignalR, React Flow, Recharts. Talks only to the API gateway.

## Commands
    npm install
    npm run dev          # http://localhost:5173
    npm run build        # tsc -b + vite build -> dist/
    npm run lint         # eslint
    npm run format       # prettier
    npm test             # vitest (unit tests)

## Environment
| Var | Default | Meaning |
|---|---|---|
| `VITE_API_BASE` | `http://localhost:8000` | gateway: `/api/*`, `/hubs/live`, `/media/{id}/whep` |
| `VITE_MOCK` | `0` | `1` = simulated data (20 cameras, 3 det/s, recognitions, metrics) |

Mock mode also starts automatically (with a banner) when the gateway is unreachable at load; `?mock=1` forces it.

## Docker
    docker build -t vp-ui --build-arg VITE_API_BASE=http://localhost:8000 .
    docker run -p 80:80 vp-ui

## Layout
`src/features/{live,recognitions,pipelines,sources,health}`, `src/components/ui`,
`src/lib/{api,realtime,whep,mock}`. Scale: paged wall (max 16 tiles), IntersectionObserver gates
WHEP + hub subscription per tile, WebRTC session cap (16), ref-counted batched hub subscriptions,
canvas overlays driven by rAF outside React, health updates coalesced to 2 Hz, shared 1 Hz clock.
