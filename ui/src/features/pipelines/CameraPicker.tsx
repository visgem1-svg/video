import { useMemo, useState } from 'react'
import { Search } from 'lucide-react'
import { Checkbox } from '@/components/ui/dropdown'
import { Button, Input } from '@/components/ui/primitives'
import type { CameraConfig } from '@/lib/types'

const RENDER_CAP = 100

/** Multi-select for hundreds of cameras: filter, select-all-filtered, capped render. */
export function CameraPicker({
  cameras,
  value,
  onChange,
}: {
  cameras: CameraConfig[]
  value: string[]
  onChange: (ids: string[]) => void
}) {
  const [q, setQ] = useState('')
  const selected = useMemo(() => new Set(value), [value])
  const filtered = useMemo(() => {
    const n = q.trim().toLowerCase()
    return n
      ? cameras.filter(
          (c) =>
            c.name.toLowerCase().includes(n) ||
            c.camera_id.includes(n) ||
            c.tags.some((t) => t.includes(n)),
        )
      : cameras
  }, [cameras, q])

  const toggle = (id: string) => {
    const next = new Set(selected)
    if (!next.delete(id)) next.add(id)
    onChange([...next])
  }
  return (
    <div className="rounded-md border">
      <div className="flex items-center gap-2 border-b p-2">
        <div className="relative flex-1">
          <Search className="absolute top-2.5 left-2.5 size-4 text-muted-foreground" />
          <Input
            className="pl-8"
            placeholder="Filter cameras / tags"
            value={q}
            onChange={(e) => setQ(e.target.value)}
            aria-label="Filter cameras"
          />
        </div>
        <Button
          variant="outline"
          size="sm"
          onClick={() => onChange([...new Set([...value, ...filtered.map((c) => c.camera_id)])])}
        >
          Select {q ? 'filtered' : 'all'}
        </Button>
        <Button variant="ghost" size="sm" onClick={() => onChange([])}>
          Clear
        </Button>
      </div>
      <ul className="grid max-h-44 grid-cols-1 gap-x-3 overflow-auto p-2 sm:grid-cols-2">
        {filtered.slice(0, RENDER_CAP).map((c) => (
          <li key={c.camera_id}>
            <label className="flex h-9 cursor-pointer items-center gap-2 rounded-md px-1.5 text-[13px] hover:bg-muted">
              <Checkbox
                aria-label={c.name}
                checked={selected.has(c.camera_id)}
                onCheckedChange={() => toggle(c.camera_id)}
              />
              <span className="truncate">{c.name}</span>
              <span className="ml-auto font-mono text-xs text-muted-foreground">{c.camera_id}</span>
            </label>
          </li>
        ))}
        {filtered.length > RENDER_CAP && (
          <li className="col-span-full p-1 text-xs text-muted-foreground">
            +{filtered.length - RENDER_CAP} more – refine the filter
          </li>
        )}
      </ul>
      <div className="border-t px-3 py-1.5 text-xs text-muted-foreground">
        {value.length} of {cameras.length} selected
      </div>
    </div>
  )
}
