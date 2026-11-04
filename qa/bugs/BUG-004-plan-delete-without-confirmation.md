# BUG-004: plan delete without confirmation

- Severity: major
- Page: Pipelines > Active plans > Watch
- Suspected component: ui

## Steps to reproduce
Pipelines > Active plans, select a plan, click `Delete`.

## Expected
Confirmation dialog (Sources has one for cameras).

## Actual
The plan is deleted immediately (t08: 'NO CONFIRM DIALOG'). One mis-click removes the main plan and resets all its cameras to the default plan.

## Evidence
tests\t08_watch_delete.py output.

## Hint
`ui/src/features/pipelines/PipelineWatch.tsx` ~line 98: `onClick={() => del.mutate(...)}`. Reuse the confirm dialog from SourcesPage.
