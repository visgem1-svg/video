# Vision Console - Design System and Page Spec

Status: v1 spec for the UI developer. Scope: one developer, one pass. Do not edit behaviour, only presentation, plus the new `/people` page.
Audit screenshots: `ui/design/audit/`. Visual reference: `ui/design/mockup-live.html`.

## 0. Sources used

| Source | URL | Taken from it |
|---|---|---|
| Anthropic `frontend-design` skill | https://github.com/anthropics/skills/tree/main/skills/frontend-design | Pick a deliberate identity from the subject's own world (here: a camera control room), not a template. Draft a plan (colors, type roles, ASCII wireframes), review it, then build. One memorable element, everything else restrained. One purposeful motion moment over scattered effects. Borders/labels must carry information (no decorative eyebrows). Plain, specific, active copy; errors and empty states say what to do next. Quality floor: visible focus, reduced motion, accessible color. Self-review with screenshots. |
| UI UX Pro Max skill | https://github.com/nextlevelbuilder/ui-ux-pro-max-skill | Pre-delivery checklist: 4.5:1 text contrast, never color alone for status (always icon/text too), Lucide SVG icons only, labels on icon-only buttons, hide decorative icons from AT, chips wrap or "+n", check 375/768/1024/1440, reduced motion, motion must end in a correct state if interrupted. |
| Taste Skill | https://github.com/Leonxlnx/taste-skill | Quiet panels, aligned figures, spend color on data (not chrome), tables and charts match the numbers, fold nav on small screens, frame checks (clear title, equal margins, no random blank stretches), review own screenshots in a real browser and fix the top problems. |
| Dark-mode guidance (search results, secondary) | web search, no single canonical URL | Off-black not pure black, a dedicated dark palette rather than inversion, status colors must keep their meaning in dark, video needs a neutral frame so it does not glare. |

Patterns also drawn from knowledge of Linear (quiet chrome, 1px hairlines, tight type, command-palette density), Vercel/Geist (neutral ramps, status dots), Grafana/Datadog (tabular numbers, status-first tables, sparkline cells), and Verkada/Rhombus-style camera consoles (video is the hero; chrome recedes; per-tile status chip and "updated Xs ago"). Web search returned nothing citable for vendor consoles specifically, so those are from general familiarity, not fetched.

## 1. Direction

"Control room, quiet." The video and the data are the only bright things. Chrome is near-black blue-gray, 1px hairlines, almost no shadows in dark. One accent (signal blue) for selection, focus and primary actions only. Color is spent on status and detections. The memorable element is the camera tile: a 16:9 frame with a thin status rail, a glass header strip, and class-colored detection boxes with confidence pills.

Principles
1. Video first: tiles get the space; headers and filters shrink to one slim toolbar row.
2. Status is never color alone: dot plus text/icon, always.
3. Dense but calm: 13px base, 8px grid, tabular numbers, few borders (hairline between rows, not boxes around everything).
4. One accent. Primary buttons are the only filled-accent surfaces; do not use accent for chips, tabs, and badges at the same time (current app does, which dilutes it).
5. Every list has loading (skeleton), empty (icon + one sentence + next action), and error (message + Retry) states.
6. Motion explains change (new detection, status change, panel open); nothing loops except the live pulse.

## 2. Tokens

Implementation: replace the `:root`/`.dark` blocks in `src/index.css`. Keep the existing shadcn names (so `primitives.tsx` keeps working) and add the new ones. Dark is default. Values are hex for exactness; Tailwind v4 accepts them in `@theme inline`.

```css
:root {                      /* LIGHT */
  --background:#F6F7F9; --foreground:#12151C;
  --card:#FFFFFF;        --card-foreground:#12151C;
  --raised:#FFFFFF;      /* popovers, dialogs */
  --muted:#EEF0F4;       --muted-foreground:#525B6B;   /* 6.4:1 on bg */
  --faint:#6B7383;       /* 4.5:1: captions, placeholders only */
  --border:#E1E4EB;      --border-strong:#CBD0DA;
  --input:#CBD0DA;
  --primary:#2F5FE0;     --primary-foreground:#FFFFFF;  /* 5.5:1 */
  --accent:#E8EEFC;      --accent-foreground:#1D3FA6;   /* hover/selected rows */
  --ring:#2F5FE0;
  --sidebar:#FFFFFF;     --sidebar-border:#E1E4EB;
  --video-bg:#0B0D12;
  /* status (text-safe on card and background) */
  --ok:#0F7B55;   --ok-bg:#E3F5EC;
  --warn:#92580A; --warn-bg:#FDF1DC;
  --err:#C23030;  --err-bg:#FCE8E8;
  --off:#5E6676;  --off-bg:#EEF0F4;
  --unas:#6D4BD0; --unas-bg:#EFEAFB;
  --destructive:var(--err); --success:var(--ok); --warning:var(--warn);
}
.dark {                      /* DARK (default) */
  --background:#0B0D12; --foreground:#E6E9EF;   /* 16:1 */
  --card:#11141B;        --card-foreground:#E6E9EF;
  --raised:#171B24;
  --muted:#171B24;       --muted-foreground:#9AA3B2;   /* 7.6:1 */
  --faint:#7A8396;       /* captions only; ~5:1 on bg, 4.5 on card */
  --border:#232836;      --border-strong:#2F3546;
  --input:#2F3546;
  --primary:#6C9BFF;     --primary-foreground:#0B0D12;  /* 7.2:1 */
  --accent:#1A2236;      --accent-foreground:#BFD2FF;
  --ring:#6C9BFF;
  --sidebar:#0E1117;     --sidebar-border:#1B2030;
  --video-bg:#000000;
  --ok:#34D399;   --ok-bg:rgba(52,211,153,.12);    /* 10:1 */
  --warn:#FBBF24; --warn-bg:rgba(251,191,36,.12);  /* 11.6:1 */
  --err:#F87171;  --err-bg:rgba(248,113,113,.13);  /* 7:1 */
  --off:#8B93A4;  --off-bg:rgba(139,147,164,.14);  /* 6.3:1 */
  --unas:#A78BFA; --unas-bg:rgba(167,139,250,.14); /* 7.1:1 */
  --destructive:var(--err); --success:var(--ok); --warning:var(--warn);
}
@theme inline {
  --color-raised:var(--raised); --color-faint:var(--faint);
  --color-border-strong:var(--border-strong); --color-ring:var(--ring);
  --color-ok:var(--ok); --color-ok-bg:var(--ok-bg);
  --color-warn:var(--warn); --color-warn-bg:var(--warn-bg);
  --color-err:var(--err); --color-err-bg:var(--err-bg);
  --color-off:var(--off); --color-off-bg:var(--off-bg);
  --color-unas:var(--unas); --color-unas-bg:var(--unas-bg);
  --color-accent-foreground:var(--accent-foreground);
  --color-sidebar-border:var(--sidebar-border);
  --font-sans:'Inter Variable','Inter',ui-sans-serif,system-ui,'Segoe UI',sans-serif;
  --font-mono:'JetBrains Mono',ui-monospace,'Cascadia Mono',Consolas,monospace;
  --radius-sm:6px; --radius-md:8px; --radius-lg:12px; --radius-xl:16px;
}
```
Verified contrast ratios (computed, WCAG relative luminance): dark text 16.0, muted 7.6, accent 7.2, ok 10.1, warn 11.6, err 7.0, off 6.3, unassigned 7.1 on `#0B0D12`; light text 17.0, muted 6.4, primary 5.1 on bg / 5.5 white-on-primary, ok 4.9, warn 5.4, err 5.2, off 5.4, unassigned 5.5 on `#F6F7F9`. Status text on its own tinted bg is lighter-on-darker in dark and darker-on-lighter in light; both stay above 4.5 because the tints are low alpha.

Status semantics (single source: `StatusDot`, `StatusBadge`)
| State | Color | Icon (lucide) | Dot animation |
|---|---|---|---|
| online | ok | `CircleCheck` (badge) / solid dot | none (live pulse only on the "Live" connection pill) |
| degraded | warn | `TriangleAlert` | none |
| offline | err | `WifiOff` | none |
| unassigned | unas | `CircleDashed` | none |
| error (step/api) | err | `CircleX` | none |
| connecting/stale | off | `Loader2` spin / `Clock` | spin |

Offline is red (err), not gray: gray (`off`) is only for "disabled/stopped/not applicable".

Detection class palette (categorical, legible on video; used for box stroke, label pill, legend chips). Stroke 2px, plus a 1px `rgba(0,0,0,.55)` outer stroke so it reads on white snow and dark night. Label pill: class color fill, text `#0B0D12` (all fills below give at least 7:1 with it).
| Class | Hex | Note |
|---|---|---|
| person | `#38BDF8` | sky |
| car | `#A3E635` | lime |
| truck | `#FB923C` | orange |
| bus | `#F472B6` | pink |
| motorcycle | `#22D3EE` | cyan |
| bicycle | `#2DD4BF` | teal |
| plate | `#FACC15` | yellow, dashed 3px stroke to mark "text read" |
| face (unknown) | `#E879F9` | fuchsia |
| face (known person) | `#4ADE80` | green, name in the label |
| text | `#C4B5FD` | lavender, dashed |
| other | hash hue fallback, `hsl(h 85% 62%)` | keep existing |
Use shape redundancy for color-blind users: plate/text dashed, known-face gets a check glyph in its pill. The same hexes are used in the Recognitions card chips and the focus-view detection list (replace the current blue "object" / amber "plate" chips).

## 3. Typography
- Font: Inter Variable (Google Fonts or `@fontsource-variable/inter`, self-host is better for an ops box that may be offline), JetBrains Mono for ids/timestamps. Fallbacks as above. Enable `font-feature-settings:'cv11','ss01'` on body; `tabular-nums` (`font-variant-numeric: tabular-nums`) on every number: fps, counts, percentages, timestamps, ages. Add utility `.num`.
- Base 13px/20px for tables and controls, 14px/22px for body paragraphs. Scale:
| Role | Size/line | Weight | Tracking |
|---|---|---|---|
| Page title | 20/28 | 600 | -0.01em |
| Section / card title | 14/20 | 600 | 0 |
| Body / table cell | 13/20 | 400 | 0 |
| Table header | 12/16 | 500, `muted-foreground`, sentence case (no ALL CAPS) | 0 |
| Caption / meta | 12/16 | 400, `faint`/`muted-foreground` | 0 |
| Stat value | 28/32 | 600 tabular | -0.02em |
| Mono id | 12/16 | 500 | 0 |
- Camera names use the sans, ids (`cam-005`) use mono in `muted-foreground`; never bold ids.

## 4. Space, radius, elevation, motion
- 4px base, 8px rhythm. Page padding 24px (16px under 1024). Gap between cards 16px; tile grid gap 8px (video walls want tight gaps). Table row height 44px (36px dense toggle optional P2).
- Radius: controls 8, cards 12, tiles 10, chips full. Dialogs 16.
- Dark elevation = surface lightness + hairline, not shadow: page `background` -> card `card` -> popover/dialog `raised` + `border-strong`. Dialog backdrop `rgba(5,7,10,.7)` + `backdrop-blur-sm`. Light mode: `shadow-[0_1px_2px_rgba(16,24,40,.06)]` on cards, `0 8px 24px rgba(16,24,40,.12)` on popovers.
- Focus: `outline: 2px solid var(--ring); outline-offset: 2px` on `:focus-visible` globally (currently missing on many controls).
- Motion: 120ms hover/press, 180ms panels/popovers (`cubic-bezier(.2,.8,.2,1)`), 260ms page content fade+4px rise on route change (one orchestrated moment). Detection box fade stays on the canvas loop. Status change: 600ms background flash of the status color at 25% alpha on the chip. Skeleton shimmer 1.4s linear. Live dot pulse 2s. `@media (prefers-reduced-motion: reduce)`: disable all of the above except opacity transitions at 0ms; stop shimmer and pulse.

## 5. Components

App shell
- Sidebar 232px (collapses to 56px icon rail below 1100px, existing breakpoint logic can stay at `lg`). Background `sidebar`, right hairline. Top: logo mark 28px (accent square only here) + "Vision Console". Nav items 36px high, radius 8, icon 16px stroke 1.75; active = `accent` bg + `accent-foreground` text + 2px accent bar at left edge; hover = `muted`. Nav groups with 12px muted group labels are NOT needed (5-6 items). Order: Live wall, Recognitions, People, Pipelines, Sources, Health (icon for People: `Users`). Bottom of sidebar: system status block (see below) and theme toggle, replacing the empty 56px top bar entirely (reclaims 56px of vertical space for video).
- Bottom status block: "Realtime: Live" with pulsing ok dot; "Mock data" warning chip when `mode==='mock'` (replaces the full-width yellow banner, which should become a 28px slim bar only when mock).
- Top bar removed; each page owns its header.

Page header (shared `<PageHeader title description actions>`): title 20/600 left, one-line muted description under it (hide description under 1100px), actions right, 24px bottom margin; toolbar (search, filters) is a separate row of 36px controls below only when needed. Fix: on Live/Recognitions the header currently packs title + 5 controls on one line and wraps awkwardly at 1280.

Controls: height 36 (28 for inline table actions). Replace native `<select>` (looks off in dark) with the Radix Select styled as input (`border-strong`, radius 8, chevron). Segmented control (view toggles 1/2x2/3x3/4x4, All/Plates/Faces, Live/History): container `muted` radius 8 padding 2; active segment = `card` bg + 1px border + foreground text (NOT filled accent). Primary button = accent fill; secondary = `card` + border; ghost; destructive = `err-bg` text `err`. Checkboxes: use styled Radix checkbox (accent when checked); current default browser checkboxes in the Pipelines camera picker look unstyled.

Badge/chip: height 20, radius full, 12px text, 1px border of same color at 30% alpha, bg `*-bg`. Variants: ok/warn/err/off/unas/neutral/accent(sparingly). `StatusBadge` = dot + label. Plan chip in Sources ("Vehicles plates + faces") becomes neutral, not accent blue.

Camera tile (`LiveTile`)
- Container: radius 10, 1px border `border` (status-tinted 1px ring only for degraded/offline: warn/err at 60%), background `--video-bg`, aspect 16/9 fixed (no letterboxing jump), `overflow:hidden`.
- Top strip: gradient `linear-gradient(to bottom, rgba(0,0,0,.65), transparent)` 40px, always present so text is legible on any frame. Left: status dot + camera name (13/600 white, truncate). Right: mono id 11px `rgba(255,255,255,.7)` (currently low-contrast gray on video: fix).
- Bottom strip (appears on hover/focus, always for degraded/offline): "updated 3s ago" (`AgeChip`, tabular) left; actions right (open focus `Maximize2`, pipeline `Workflow`). Age chip colors: <5s neutral white 70%, 5-15s warn, >15s err.
- Detection overlay: boxes per section 2, labels 11px/600 pill with class + confidence (`car 94%`), hide label under 140px box width.
- Empty/no-video state (currently a pure black rectangle, the worst problem on the wall): centered `VideoOff` icon 20px in `faint`, line "No signal" (offline/unassigned) or "Connecting" with spinner (loading), sub "Last frame 3m ago". Background a subtle diagonal-stripe or `#0B0D12`, never pure unlabelled black.
- Offline: desaturate the last frame (`grayscale(1) brightness(.5)`) and overlay `WifiOff` + "Offline".

Stat tile (Health): card, no giant icon; label 12 muted top-left, value 28 tabular, small status dot beside the label in the state color, optional delta/sparkline right. Selected (filter) state = 1px accent ring, not a thick bright outline. Order: Total, Online, Degraded, Offline, Unassigned; zero values render muted (`faint`), non-zero Degraded/Offline get colored value.

Data table (Sources, Health, detections)
- Sticky header on `card` bg with bottom hairline; header text 12 muted; rows 44px, hairline between rows only, hover `muted/60`, selected `accent`. No outer thick border; container radius 12 border 1px. Numeric columns right-aligned tabular. Mono for ids. First column = status dot + name over id (keep). Action icons at 50% opacity until row hover; delete icon uses `muted-foreground` (turns `err` on hover) instead of permanent red (sources page currently has 21 red trash icons shouting).
- Pagination: bottom bar inside the card "1-25 of 21, Prev/Next" instead of top toolbar.
- Toggle switch: ok-colored when enabled? No: accent. Keep.
- Worker column: neutral mono chip. FPS column: `3.2 / 3` with the target in `faint`; when actual < 0.8*target color actual `warn`.

Skeleton: `muted` rounded blocks with shimmer; match final layout (tile = 16:9 block, table = 8 rows). Empty state component `<EmptyState icon title hint action>`: 40px icon in a 56px `muted` circle, title 14/600, hint 13 muted max 48ch, optional button. Error state: `err-bg` panel, `CircleX`, message, `Retry` button (use on every query). Never show raw "undefined"/alt text (Recognitions currently shows a broken-image alt "unknown" overlapping the face badge: show a `UserRound` placeholder instead on image error).

Dialogs/forms: Radix dialog, radius 16, width 480 (640 for camera form), header title 16/600 + muted description, hairline above footer, footer right-aligned (Cancel ghost, primary right). Fields: label 12/500 above, 36px input, helper 12 faint, error text 12 `err` with icon, error border `err`. Group the camera form in sections (Source, Pipeline, Advanced) separated by hairlines. Destructive confirm: dialog with `err` button and the entity name in the copy.

Toasts: bottom-right, 360px, `raised` bg, border-strong, left 3px status bar in color, icon, title 13/600 + 12 muted body, auto-dismiss 4s (errors 8s, persistent with action). Use for enroll/rename/delete/activate results.

Tabs / segmented for page-level views (New pipeline / Active plans): underline tabs (2px accent underline, 36px) rather than filled segment.

React Flow DAG (Pipelines, Pipeline watch)
- Canvas background `background` with dot grid `border` color 1px at 20px gap; no outer border thicker than hairline; controls styled as small `card` buttons (current white-on-black default is harsh).
- Node: 220x72, radius 10, `card` bg, 1px `border-strong`, left icon tile 28px (`muted` bg, step-type icon), title 13/600 (`detect_objects`), mono step id 11 faint, right: execution-mode chip ("inline"/"async" neutral, not accent blue). Camera source node: pill, `muted` bg, `Camera` icon.
- Handles 6px dots `border-strong`; edges 1.5px `border-strong`, selected/active edge `primary` with animated dash (stroke-dasharray 4 4, 1s linear, only when the source step ran in the last 5s; disabled under reduced motion). Edge labels ("found car") = small pill `muted` bg 11px (currently unreadable tiny yellow-on-dark text).
- Live states (apply to node border + a 6px status dot + metrics line):
  | State | Node style |
  |---|---|
  | idle/waiting | default, dot `off`, "Waiting for metrics" |
  | ran (recent) | border `ok` 40%, dot ok, 600ms ok glow `box-shadow:0 0 0 3px ok-bg` fading out, shows "12 runs - 38 ms" |
  | skipped | opacity .6, dashed border, dot off, chip "skipped (condition false)" |
  | failed | border `err`, bg `err-bg`, `CircleX`, last error truncated in a tooltip |
  | running | border `primary`, spinner icon |
  Always icon + text with the color (not color alone). Metrics under the node: `runs`, `p50 ms`, `err %` in tabular 11px.
- Step metric cards under the graph: same stat-tile style, 3 columns, with a 24px sparkline.

## 6. Page specs

Global: content max width none (video walls need full width); page padding 24; on /live reduce to 12.

### /live (wall)
```
+--------+--------------------------------------------------------------+
| logo   | Live wall                [search...] [All states v] [1|2x2|3x3|4x4] [<][1/3][>] |
| Live   | 21 cameras - 19 online - 2 degraded                              |
| Recog  | +-----------+ +-----------+ +-----------+                       |
| People | |o Camera 001  cam-001 | |o Camera 002 | |o Camera 003 |     |
| Pipel  | |           | |  [video]  | | No signal |                       |
| Source | |      3s ago  [x] | ...                                         |
| Health | +-----------+ +-----------+ +-----------+                       |
|--------| (3x3 fills viewport height: tile height = (100vh - toolbar)/rows)|
| o Live |                                                                  |
+--------+--------------------------------------------------------------+
```
- Single toolbar row; title 20px left with a live summary line (counts, colored dots) replacing the static sentence "Only the cameras on screen are streamed..." (move that to a tooltip `Info` icon).
- Grid fills viewport: `grid-template-rows: repeat(n, minmax(0,1fr))` with `h-[calc(100vh-header)]`, tiles 16:9 `object-fit: cover`; no empty 90px band at the bottom (current wall leaves dead space and 3x3 tiles are not 16:9).
- Pagination moves to the toolbar right as compact icon buttons; show "1-9 of 21".
- Tile per section 5. Keyboard: `f` opens focus on hovered tile (P2).

### /live/:id (focus + pipeline watch)
```
| < Wall  Camera 005 cam-005 [o online]            3s ago   [<] [>]  |
| +---------------------------------------------+ +------------------+ |
| |                                             | | Stream           | |
| |            video 16:9 (fills width)         | | 3.0/3 fps 3s     | |
| |   [car 94%]  [PL8REC 100%]                  | | fx-2   96.3%     | |
| |                                             | +------------------+ |
| +---------------------------------------------+ | Pipeline watch   | |
| [All|Objects|Plates|Faces]  Live detections    | | s1 detect  o 38ms| |
| 14:54:55 [car]   car            94% ####       | |  | found car      |
| 14:54:55 [plate] PL8REC        100% ####       | | s2 read_plate o  | |
|                                                | | s3 identify skip | |
|                                                | +------------------+ |
```
- Layout: grid `[minmax(0,1fr) 360px]`; video column width-first, below it the detections list capped to remaining height with its own scroll; right rail scrolls independently. Video gets a 12px radius frame, controls (fullscreen, snapshot, overlay on/off toggle) as an overlay at bottom on hover.
- Detections list: tabular time, class chip in class color (section 2), value in mono for plates, confidence as 48px bar + number, new row slides in 180ms and highlights 600ms. Group repeated identical reads ("PL8REC x4").
- Pipeline watch panel (`CameraPipelinePanel`): vertical stepper. Each step row: status dot + step name + chip (inline/async), metrics line `38 ms - 12 runs`, "last: 2s ago". Between steps a 1px vertical connector with the condition label ("found car") as a pill; row states use the DAG state table (ran/skipped/failed/idle) including icon + text. Failed step expands to show the error. Footer link "Open full graph" to /pipelines. Empty (no active plan): EmptyState "No pipeline on this camera" + button "Create pipeline". The existing "Active plan / Watch" card collapses into this panel (no duplicate).
- Prev/next camera arrows keep, add `j/k` shortcut hint tooltip.

### /recognitions
```
| Recognitions                [search plate/name] [cam v] [All|Plates|Faces] [Live|History] |
| +--------+ +--------+ +--------+ +--------+ +--------+ +--------+   (auto-fill minmax(200px,1fr))
| |  4:3   | |        | ...                                         |
| |snapshot| |        |                                             |
| |[face] 91%                                                        |
| +--------+                                                         |
| Unknown   Camera 007   14:52:50 (2m ago)                           |
```
- Cards: radius 12, snapshot 4:3 `object-cover`, class chip overlaid top-left (class color, glass bg), confidence top-right as a small pill with a 2px ring colored by confidence (>=90 ok, 70-89 warn, <70 err, plus the number). Title: plate text in mono 15/600 (plates) or name (known face, with `UserCheck` ok) / "Unknown person" muted italic-free (unknowns); meta line camera name + relative time ("2m ago", absolute on hover tooltip). Date format must be locale-stable (`21.10.2026, 14:52:54` is fine but prefer `14:52:54` + relative).
- Fix image error: placeholder icon tile, never the alt text. Click card opens a side sheet (480px) with large snapshot, all fields, "Show camera" link, and for unknown faces "Add to people" (P1, links to /people enroll).
- Live mode: new cards fade+slide in at the top-left (180ms); show a "N new" pill if scrolled. Empty: EmptyState `ScanFace` "No recognitions yet" + hint "Activate a pipeline with plate or face steps" + button -> /pipelines. Skeleton: 8 placeholder cards.

### /pipelines
```
| Pipelines                                  [ New pipeline | Active plans (2) ] (underline tabs) |
| +-------------------------+ +----------------------------------------------+
| | Describe what to detect | | Draft                      [Activate] [Discard] |
| | [ textarea 4 rows     ] | | "read plates of trucks ..."                   |
| | Try: (chip)(chip)(chip) | | +----------------------------------------+    |
| | Cameras  [filter][All][Clear] | |          React Flow canvas             |    |
| | [x] Camera 001 cam-001  | | +----------------------------------------+    |
| | ...  (scroll, 6 rows)   | | s1 detect_objects  s2 read_plate  s3 ...      |
| | 3 of 21 selected        | | step cards (metrics)                          |
| | [ Draft plan ]          | |                                                |
| +-------------------------+ +----------------------------------------------+
```
- Left column 400px, sticky; right min 0. Example prompts as quiet chips with `Sparkles` icon. Camera picker rows 36px with styled checkbox, name + mono id, tag chips right; "Draft plan" full-width primary, disabled until text and at least 1 camera, with helper text explaining why. While drafting: button spinner + the right panel shows a skeleton graph (3 ghost nodes pulsing).
- Right panel: Draft empty state per section 5. After draft: header with plan name, version, camera count chips, `Activate` primary + `Discard` ghost; the graph gets at least 360px height; a "Plan explanation" collapsible with the LLM's natural-language summary.
- Active plans: left list of plan cards (name, status badge, "3 steps - 20 cameras", relative time; selected = accent bg); right = same detail view with live node states + metrics, Delete as ghost-destructive in a menu (`MoreHorizontal`), confirm dialog. Metrics cards show placeholders "Waiting for metrics (5 s windows)" with skeleton bars, not plain text.

### /sources
```
| Sources     Camera registry...        [search] [Workers] [+ Add camera] |
| +-----------------------------------------------------------------+   |
| | o Camera        Source      FPS      Plan        Worker  Enabled  ...|
| | Camera 001      publisher   3.2 / 3  (Vehicles..) fx-1    (o)    [..]|
| ...                                                                    |
| | 1-21 of 21                                       [Prev] [Next]      |
+-----------------------------------------------------------------+
| Workers panel (collapsible): fx-1  11 cams  CPU bar  | fx-2 ...         |
```
- Table per section 5 (sticky header; 44px rows; actions on hover; row click opens the edit dialog; Source value as mono muted, truncated with tooltip for URLs). Add camera = primary at top right. Workers panel as a collapsible card below or a right drawer: each worker a row with status dot, camera count, load bar (4px, ok/warn/err by 70/90%).
- Delete confirmation dialog. Toast on save/delete. Empty (no cameras): EmptyState `Video` "No cameras yet" + Add camera. Validation per section 5 form rules.

### /health
```
| Health  Stream status from the extractors, worst first.     [search] |
| [Total 21] [Online 19] [Degraded 2] [Offline 0] [Unassigned 0]      |
| +-----------------------------------------------------------------+ |
| | Camera       State       FPS      Last frame  Reconn 1h Uptime 1h Worker Last error |
| | Camera 019   (!) degraded 3.0/3   3s          18        87.7% [bar] fx-1  ...        |
```
- Tiles are clickable filters (selected = accent ring); reduce their height to 88px. Table: State as `StatusBadge` (icon+text); Uptime as number + 48px bar colored by threshold (>=99 ok, 95-99 warn, <95 err); reconnects >5 colored warn; Last frame uses age color rule; Last error column gets remaining width, truncated with tooltip, mono. Row left 2px status rail for non-online rows. Empty filter result: EmptyState "No cameras in this state".
- Currently the wide empty "Last error" column wastes space and "online" green pills repeat 19 times; make online rows quieter (dot + muted text "online"), and let only degraded/offline use filled badges.

### /people (new: known-people library)
```
| People  Known faces the system can recognise          [search] [+ Enroll person] |
| +--------+ +--------+ +--------+ +--------+ +--------+                 (grid minmax(180px,1fr))
| | photo  | | photo  | | photo  |                                      |
| | 1:1    | |        | |        |                                      |
| | Alice K. | Bob    | ...                                              |
| | 14 sightings - last seen Camera 007, 3m ago                        |
| | [rename] [delete] on hover                                         |
+--------+ ...
Detail sheet (right, 480px): large photo(s), name (inline edit), enrolled date,
  Sightings list: [thumb] Camera 007  14:52:50  91%  [Show camera]   (tabular)
```
- Card: photo 1:1 `object-cover` radius 12 with `UserRound` placeholder, name 14/600, meta 12 muted, a green `known-face` dot. Overflow menu: Rename (inline input in the sheet or dialog), Delete (confirm dialog naming the person, err button, toast with Undo 5s is P2).
- Enroll dialog (480): drag-and-drop zone (dashed `border-strong`, `Upload` icon, "Drop a clear, front-facing photo or click to browse"), preview with crop square, name field (required), multiple photos allowed (thumb row, hint "3 to 5 photos improve accuracy"), inline error for "no face found" (err text under the zone) and size/type errors; submit "Enroll person" shows spinner then toast "Alice enrolled". Skeleton: 8 square cards. Empty: EmptyState `Users` "No known people yet. Enroll someone so recognitions show their name." + `Enroll person`.
- Sightings feed uses the same card/list row as Recognitions; "Unknown" faces from Recognitions link here via "Add to people".

## 7. Audit: concrete problems in the current app (screens in `ui/design/audit/`)
1. Live wall: tiles without video are pure black rectangles with no message ("no signal"/"connecting"); 8 of 9 tiles look broken.
2. Live wall: wasted vertical space (dead band under the 3x3 grid, 56px empty top bar), tiles not locked to 16:9, so the wall does not fill the viewport.
3. Live wall: toolbar is cramped; title/description and 4 controls compete; native `<select>` and filled-accent segment toggles look heavy; tile mono ids are low-contrast on video; no "updated Xs ago" or per-tile status text.
4. Accent blue is overused (active segments, plan chips, "inline" chips, "object" chips, links, toggles, primary buttons, active nav, focus outline), so it no longer signals "primary".
5. Recognitions: a broken snapshot shows alt text "unknown" overlapping the face badge; titles say "unknown" in bold as if a name; page is nearly empty with no empty-state guidance or relative times; confidence has no visual.
6. Sources: 21 permanent red trash icons and blue plan chips on every row create noise; table has no sticky header or row hover affordance; pagination at the top; FPS shown without health cue.
7. Health: five identical large green "online" pills drown the two degraded rows; a wide empty "Last error" column; stat tiles have oversized heavy selected outline and decorative icons; no uptime visualisation.
8. Pipelines: the left form uses unstyled native checkboxes and a 2-column list that is hard to scan; the Draft panel is a large empty card; segment tabs are filled accent; the DAG's edge labels (`found car`) are ~8px unreadable, the React Flow controls are default white; metric cards say "Waiting for metrics" as plain text.
9. Focus page: side panel duplicates info, "Active plan" card lists steps with identical blue "inline" chips (no live state), the detections list uses inconsistent class chips and the video does not use available width/height (large dead area below); boxes use 11px labels with little contrast.
10. Cross-cutting: no global `:focus-visible` style, no skeleton/empty/error component set, no toasts, numbers not consistently tabular, light theme not audited and the shell has no theme-aware status block (connection pill sits alone in an empty header). Status color semantics are inconsistent (offline icon red, degraded amber badges, online green) with no shared component.

## 8. Implementation checklist

P0 (one pass; the biggest visual win)
- [ ] Replace tokens in `index.css` (section 2), add `focus-visible` ring, `.num` tabular utility, `prefers-reduced-motion` block, load Inter Variable + JetBrains Mono.
- [ ] App shell: remove top bar, move connection pill + theme toggle to the sidebar bottom, slim mock banner, active nav style, add People nav item (route placeholder ok).
- [ ] Shared `PageHeader`, `StatusDot`/`StatusBadge` (single status mapping), `EmptyState`, `ErrorState`, `Skeleton` variants; segmented control restyle; Radix `Select` replacing native selects; neutral chip variants (remove accent chips).
- [ ] Camera tile: 16:9, gradient header, status dot, mono id contrast, `AgeChip` with color thresholds, "No signal"/"Connecting"/"Offline" states, hover action strip; wall grid fills the viewport; single toolbar row with live summary.
- [ ] Detection class palette applied to overlay boxes (outline stroke + label pill with confidence) and to all class chips (live list, recognitions).
- [ ] Tables (Sources, Health): sticky header, 44px rows, hover, quieter actions (delete grey -> red on hover), quieter "online" state, uptime bar, pagination to footer.
- [ ] Recognitions card redo with image-error placeholder, confidence pill, relative time, empty state.
- [ ] /people page with grid, enroll dialog (drop zone, error states), rename/delete, sightings sheet, toasts.

P1
- [ ] Focus page layout (video fills, independent scroll columns), pipeline watch stepper with ran/skipped/failed/running states, detection list grouping.
- [ ] DAG styling: node cards, edge label pills, dot-grid canvas, live node states and animated active edge, controls styled, step metric cards with sparkline.
- [ ] Toast system (`Toaster`, left status bar), dialog restyle (sections, footer hairline), destructive confirm.
- [ ] Health: threshold coloring (reconnects, uptime, last frame), stat tile cleanup and filter behaviour.
- [ ] Recognition detail side sheet, "Add to people".

P2
- [ ] Dense/comfortable table toggle, `f` / `j` `k` shortcuts, command palette (Ctrl+K) for camera jump.
- [ ] Undo toast for deletes, route transition fade, status-change flash, workers load bars.
- [ ] Light theme polish pass at 1280/1024/768 widths and a 375px sanity check (sidebar becomes a drawer).
- [ ] Sparklines in Health tiles; screenshot regression via the existing Playwright setup for 7 pages x 2 themes.
