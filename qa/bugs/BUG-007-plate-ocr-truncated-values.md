# BUG-007: plate ocr truncated values

- Severity: minor
- Page: Recognitions
- Suspected component: engines (ocr/lpr)

## Steps to reproduce
`GET /api/recognitions?type=plate&limit=200`.

## Expected
The sim plate always reads 'PL8REC'.

## Actual
159 x 'PL8REC', 22 x 'PL8R', 19 x 'PL8RE' (20% truncated); they show in the list and plate search for 'PL8REC' misses them.

## Evidence
API output; card 'PL8R' in screenshots\t03-recog.png.

## Hint
OCR/LPR on partially cropped plates (plate at frame edge while the sim video pans). Consider min length / confidence filter.
