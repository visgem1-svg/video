import type { ReactElement } from 'react'

/** Lightweight native tooltip (title) so icon-only controls always have a text label. */
export function Tooltip({
  label,
  children,
}: {
  label: string
  children: ReactElement<{ title?: string }>
}) {
  return <span title={label}>{children}</span>
}
