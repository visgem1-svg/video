import { useEffect, useMemo, useRef, useState } from 'react'
import { Check, ChevronDown, Search } from 'lucide-react'
import { Input } from '@/components/ui/primitives'
import type { CameraConfig } from '@/lib/types'
import { cn } from '@/lib/utils'

const RENDER_CAP = 100

/** Searchable single-camera picker (hundreds of cameras: filter + capped render). */
export function CameraCombobox({
  cameras,
  value,
  onChange,
  className,
}: {
  cameras: CameraConfig[]
  value: string | null
  onChange: (id: string) => void
  className?: string
}) {
  const [open, setOpen] = useState(false)
  const [q, setQ] = useState('')
  const root = useRef<HTMLDivElement>(null)
  const current = cameras.find((c) => c.camera_id === value)

  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => {
      if (!root.current?.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false)
    document.addEventListener('mousedown', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  const filtered = useMemo(() => {
    const n = q.trim().toLowerCase()
    const list = n
      ? cameras.filter((c) => c.name.toLowerCase().includes(n) || c.camera_id.includes(n))
      : cameras
    return list.slice(0, RENDER_CAP)
  }, [cameras, q])

  return (
    <div ref={root} className={cn('relative', className)}>
      <button
        type="button"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-label="Camera"
        onClick={() => setOpen((o) => !o)}
        className="inline-flex h-9 w-56 items-center justify-between gap-2 rounded-md border border-border-strong bg-card px-3 text-[13px] hover:bg-muted"
      >
        <span className="truncate">
          {current ? current.name : <span className="text-faint">Choose a camera</span>}
          {current && <span className="ml-2 font-mono text-xs text-muted-foreground">{current.camera_id}</span>}
        </span>
        <ChevronDown className="size-4 shrink-0 text-faint" aria-hidden />
      </button>
      {open && (
        <div className="absolute top-10 left-0 z-40 w-72 rounded-lg border border-border-strong bg-raised p-2 shadow-xl">
          <div className="relative mb-2">
            <Search className="absolute top-2.5 left-2.5 size-4 text-faint" aria-hidden />
            <Input
              autoFocus
              className="pl-8"
              placeholder="Search cameras"
              aria-label="Search cameras"
              value={q}
              onChange={(e) => setQ(e.target.value)}
            />
          </div>
          <ul role="listbox" aria-label="Cameras" className="max-h-64 overflow-auto">
            {filtered.map((c) => (
              <li key={c.camera_id} role="option" aria-selected={c.camera_id === value}>
                <button
                  type="button"
                  onClick={() => {
                    onChange(c.camera_id)
                    setOpen(false)
                    setQ('')
                  }}
                  className="flex h-8 w-full items-center gap-2 rounded-md px-2 text-left text-[13px] hover:bg-muted"
                >
                  <span className="min-w-0 flex-1 truncate">{c.name}</span>
                  <span className="font-mono text-xs text-muted-foreground">{c.camera_id}</span>
                  {c.camera_id === value && <Check className="size-4 text-primary" aria-hidden />}
                </button>
              </li>
            ))}
            {filtered.length === 0 && (
              <li className="px-2 py-3 text-[13px] text-muted-foreground">No camera matches "{q}".</li>
            )}
          </ul>
        </div>
      )}
    </div>
  )
}
