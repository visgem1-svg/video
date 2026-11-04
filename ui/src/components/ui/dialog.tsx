import * as React from 'react'
import * as D from '@radix-ui/react-dialog'
import * as S from '@radix-ui/react-switch'
import { X } from 'lucide-react'
import { cn } from '@/lib/utils'

export const Dialog = D.Root
export const DialogTrigger = D.Trigger
export const DialogClose = D.Close

export function DialogContent({
  className,
  title,
  description,
  children,
  ...props
}: React.ComponentPropsWithoutRef<typeof D.Content> & { title: string; description?: string }) {
  return (
    <D.Portal>
      <D.Overlay className="fixed inset-0 z-50 bg-[rgba(5,7,10,.7)] backdrop-blur-sm" />
      <D.Content
        className={cn(
          'fixed top-1/2 left-1/2 z-50 max-h-[90vh] w-[min(480px,calc(100vw-2rem))] -translate-x-1/2 -translate-y-1/2 overflow-auto rounded-2xl border border-border-strong bg-raised p-5 shadow-2xl outline-none',
          className,
        )}
        {...props}
      >
        <div className="mb-4 pr-6">
          <D.Title className="text-base font-semibold">{title}</D.Title>
          <D.Description
            className={cn('text-[13px] text-muted-foreground', !description && 'sr-only')}
          >
            {description ?? title}
          </D.Description>
        </div>
        {children}
        <D.Close
          className="absolute top-4 right-4 rounded p-1 text-muted-foreground hover:bg-muted"
          aria-label="Close"
        >
          <X className="size-4" />
        </D.Close>
      </D.Content>
    </D.Portal>
  )
}

/** Footer with a hairline above, actions right-aligned. */
export function DialogFooter({ className, ...p }: React.HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('mt-5 flex justify-end gap-2 border-t pt-4', className)} {...p} />
}

export function Switch({ className, ...props }: React.ComponentPropsWithoutRef<typeof S.Root>) {
  return (
    <S.Root
      className={cn(
        'peer inline-flex h-5 w-9 shrink-0 cursor-pointer items-center rounded-full border border-transparent bg-border-strong transition-colors data-[state=checked]:bg-primary disabled:opacity-50',
        className,
      )}
      {...props}
    >
      <S.Thumb className="block size-4 translate-x-0.5 rounded-full bg-white shadow transition-transform data-[state=checked]:translate-x-[18px]" />
    </S.Root>
  )
}
