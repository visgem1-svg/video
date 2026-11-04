# BUG-010: recognitions empty filter message

- Severity: minor
- Page: Recognitions (Live tab)
- Suspected component: ui

## Steps to reproduce
Type 'zzzz' in the plate search.

## Expected
'No matches' message.

## Actual
Shows 'Waiting for recognitions...' (the no-data message) although recognitions exist.

## Evidence
tests\t04_recog.py output.

## Hint
`RecognitionsPage.tsx` empty state does not distinguish filtered-empty from no data.
