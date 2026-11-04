# BUG-008: camera dialog labels not associated

- Severity: minor
- Page: Sources > Add/Edit camera
- Suspected component: ui

## Steps to reproduce
Open Add camera; use the accessibility tree or `getByLabel('Camera id')`.

## Expected
Inputs are labelled (htmlFor/id).

## Actual
`<Label>` is not bound to its input, so screen readers and `getByLabel` find nothing (tests use placeholders).

## Evidence
tests\t06_sources.py (getByLabel timed out).

## Hint
`ui/src/features/sources/CameraDialog.tsx` Field(): generate an id with useId and set htmlFor/id.
