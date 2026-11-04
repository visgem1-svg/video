# BUG-001: live wall layout 1 blank

- Severity: major
- Page: Live wall
- Suspected component: ui

## Steps to reproduce
Select layout `1` on the Live wall.

## Expected
One large camera tile (max-w-5xl, centered) with live video.

## Actual
Page is empty. The tile exists but is 4x2 px (video element 1.5x0 px), so nothing is visible at 1920x1080 and 1280x720. A `<video>` is still created and connected to WebRTC (wasted session).

## Evidence
screenshots\t09-layout-1.png, screenshots\t10-layout1-1920.png; tests\t10_layout1.py prints the grid chain `grid gap-3 grid-cols-1 max-w-5xl mx-auto` = 4x2 px.

## Hint
`ui/src/features/live/LiveWallPage.tsx` COLS[1] = 'grid-cols-1 max-w-5xl mx-auto': the grid is a child of a `flex flex-col` container, so `mx-auto` makes it shrink-to-fit and the `aspect-video` tile has no intrinsic width. Add `w-full`.
