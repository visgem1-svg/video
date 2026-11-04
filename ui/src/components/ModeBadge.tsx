import { Moon } from 'lucide-react'
import { Badge } from '@/components/ui/primitives'
import type { ExtractMode } from '@/lib/types'

/** Grey badge for idle extraction (1 frame / 5 s). Full mode is the norm and shows nothing. */
export function ModeBadge({ mode }: { mode: ExtractMode }) {
  if (mode !== 'idle') return null
  return (
    <Badge tone="neutral" title="Idle: keyframes only, 1 frame every 5 s">
      <Moon className="size-3" aria-hidden />
      idle
    </Badge>
  )
}
