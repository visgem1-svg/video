import type { DetObject, PlanWhen } from './types'

export const classList = (cls: string): string[] =>
  cls
    .split(',')
    .map((c) => c.trim())
    .filter(Boolean)

/** Evaluates a step condition (9.2 any-of classes) against what the detector found. */
export function evalWhen(
  when: PlanWhen | undefined,
  ctx: { objects: DetObject[]; failed?: ReadonlySet<string> },
): boolean {
  if (!when || when.type === 'always') return true
  if (when.type === 'failed') return ctx.failed?.has(when.step) ?? false
  const classes = classList(when.class)
  const hits = ctx.objects.filter((o) => classes.includes(o.class))
  if (when.type === 'found') return hits.some((o) => o.confidence >= (when.min_confidence ?? 0))
  return hits.length > when.gt
}
