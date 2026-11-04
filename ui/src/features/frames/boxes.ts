import { CLASS_COLORS } from '@/lib/overlay'
import { hashHue } from '@/lib/utils'
import type { FrameRecord } from '@/lib/api/frames'
import type { Box } from '@/lib/types'

export interface FrameBox {
  box: Box
  label: string
  color: string
  dashed: boolean
  kind: 'object' | 'plate' | 'face' | 'face_known' | 'text'
}

type Dets = Pick<FrameRecord, 'objects' | 'plates' | 'faces' | 'texts'>

export const classColor = (cls: string) => CLASS_COLORS[cls] ?? `hsl(${hashHue(cls)} 85% 62%)`

/** Frame record (detail shape with boxes) -> drawable boxes in frame pixels. */
export function frameToBoxes(f: Dets): FrameBox[] {
  const out: FrameBox[] = []
  if (Array.isArray(f.objects))
    for (const o of f.objects)
      out.push({
        box: o.box,
        label: `${o.class} ${Math.round(o.confidence * 100)}%`,
        color: classColor(o.class),
        dashed: false,
        kind: 'object',
      })
  for (const p of f.plates ?? [])
    out.push({ box: p.box, label: p.plate, color: CLASS_COLORS.plate, dashed: true, kind: 'plate' })
  for (const fc of f.faces ?? []) {
    const known = !!fc.person_name
    out.push({
      box: fc.box,
      label: known ? `✓ ${fc.person_name}` : fc.score == null ? 'face' : 'unknown',
      color: known ? CLASS_COLORS.face_known : CLASS_COLORS.face,
      dashed: false,
      kind: known ? 'face_known' : 'face',
    })
  }
  for (const t of f.texts ?? [])
    out.push({ box: t.box, label: t.text, color: CLASS_COLORS.text, dashed: true, kind: 'text' })
  return out
}

/** Class counts for chips, from either the list shape ({class: n}) or the detail shape (boxes). */
export function classCounts(f: Dets): Array<[string, number]> {
  const m = new Map<string, number>()
  if (Array.isArray(f.objects)) for (const o of f.objects) m.set(o.class, (m.get(o.class) ?? 0) + 1)
  else for (const [k, v] of Object.entries(f.objects ?? {})) m.set(k, v)
  if (f.plates?.length) m.set('plate', f.plates.length)
  if (f.faces?.length) m.set('face', f.faces.length)
  if (f.texts?.length) m.set('text', f.texts.length)
  return [...m.entries()].sort((a, b) => b[1] - a[1])
}
