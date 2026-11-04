import * as React from 'react'
import * as D from '@radix-ui/react-dialog'
import { X } from 'lucide-react'
import { cn } from '@/lib/utils'

/**
 * Right-hand drawer that does not block the page: no overlay, no focus trap, clicks outside keep
 * working (the caller decides what they do). Esc and the close button dismiss it.
 */
export function Sheet({
  open,
  onOpenChange,
  title,
  description,
  actions,
  className,
  wide,
  children,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  title: React.ReactNode
  description?: string
  /** extra header content under the title (badges, ids) */
  actions?: React.ReactNode
  className?: string
  /** wider drawer for content that needs room (plan graphs) */
  wide?: boolean
  children: React.ReactNode
}) {
  return (
    <D.Root open={open} onOpenChange={onOpenChange} modal={false}>
      <D.Portal>
        <D.Content
          onInteractOutside={(e) => e.preventDefault()}
          onOpenAutoFocus={(e) => e.preventDefault()}
          className={cn(
            'fixed inset-y-0 right-0 z-40 flex flex-col border-l border-border-strong bg-raised shadow-2xl outline-none',
            wide ? 'w-[min(760px,100vw)]' : 'w-[min(480px,100vw)]',
            className,
          )}
        >
          <div className="flex items-start gap-2 border-b px-4 py-3">
            <div className="min-w-0 flex-1">
              <D.Title className="truncate text-base font-semibold">{title}</D.Title>
              <D.Description className={cn('text-xs text-muted-foreground', !description && 'sr-only')}>
                {description ?? 'Details'}
              </D.Description>
              {actions}
            </div>
            <D.Close
              className="rounded p-1 text-muted-foreground hover:bg-muted"
              aria-label="Close panel"
            >
              <X className="size-4" />
            </D.Close>
          </div>
          {children}
        </D.Content>
      </D.Portal>
    </D.Root>
  )
}
