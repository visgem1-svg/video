import * as React from 'react'
import { Slot } from '@radix-ui/react-slot'
import { cva, type VariantProps } from 'class-variance-authority'
import { CircleX, Info, type LucideIcon } from 'lucide-react'
import { cn } from '@/lib/utils'
import { Tooltip } from './tooltip'

// ---------- Button ----------
const buttonVariants = cva(
  'inline-flex shrink-0 items-center justify-center gap-2 whitespace-nowrap rounded-md text-[13px] font-medium transition-colors duration-[120ms] disabled:pointer-events-none disabled:opacity-50 [&_svg]:size-4 [&_svg]:shrink-0',
  {
    variants: {
      variant: {
        default: 'bg-primary text-primary-foreground hover:bg-primary/90',
        secondary: 'border border-border-strong bg-card hover:bg-muted',
        outline: 'border border-border-strong bg-card hover:bg-muted',
        ghost: 'hover:bg-muted',
        destructive: 'bg-err-bg text-err hover:bg-err/20',
      },
      size: { default: 'h-9 px-3.5', sm: 'h-7 px-2.5 text-xs', icon: 'size-8', lg: 'h-10 px-6' },
    },
    defaultVariants: { variant: 'default', size: 'default' },
  },
)
export interface ButtonProps
  extends React.ButtonHTMLAttributes<HTMLButtonElement>, VariantProps<typeof buttonVariants> {
  asChild?: boolean
}
export const Button = React.forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, variant, size, asChild, ...props }, ref) => {
    const Comp = asChild ? Slot : 'button'
    return (
      <Comp ref={ref} className={cn(buttonVariants({ variant, size }), className)} {...props} />
    )
  },
)
Button.displayName = 'Button'

// ---------- Badge (neutral by default; colour only for status) ----------
const badgeVariants = cva(
  'inline-flex h-5 items-center gap-1 rounded-full border px-2 text-xs font-medium whitespace-nowrap',
  {
    variants: {
      tone: {
        neutral: 'border-border bg-muted text-muted-foreground',
        info: 'border-border bg-muted text-muted-foreground',
        outline: 'border-border-strong text-foreground',
        success: 'border-ok/30 bg-ok-bg text-ok',
        warning: 'border-warn/30 bg-warn-bg text-warn',
        danger: 'border-err/30 bg-err-bg text-err',
        unassigned: 'border-unas/30 bg-unas-bg text-unas',
        accent: 'border-primary/30 bg-accent text-accent-foreground',
      },
    },
    defaultVariants: { tone: 'neutral' },
  },
)
export function Badge({
  className,
  tone,
  ...props
}: React.HTMLAttributes<HTMLSpanElement> & VariantProps<typeof badgeVariants>) {
  return <span className={cn(badgeVariants({ tone }), className)} {...props} />
}

// ---------- Inputs ----------
const field =
  'w-full rounded-md border border-border-strong bg-card px-3 text-[13px] transition-colors placeholder:text-faint disabled:opacity-50 aria-[invalid=true]:border-err'
export const Input = React.forwardRef<
  HTMLInputElement,
  React.InputHTMLAttributes<HTMLInputElement>
>(({ className, ...p }, ref) => <input ref={ref} className={cn(field, 'h-9', className)} {...p} />)
Input.displayName = 'Input'
export const Textarea = React.forwardRef<
  HTMLTextAreaElement,
  React.TextareaHTMLAttributes<HTMLTextAreaElement>
>(({ className, ...p }, ref) => (
  <textarea ref={ref} className={cn(field, 'min-h-24 py-2', className)} {...p} />
))
Textarea.displayName = 'Textarea'
export function Label({ className, ...p }: React.LabelHTMLAttributes<HTMLLabelElement>) {
  return <label className={cn('text-xs font-medium text-muted-foreground', className)} {...p} />
}

// ---------- Card ----------
export function Card({ className, ...p }: React.HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn(
        'rounded-xl border bg-card shadow-[0_1px_2px_rgba(16,24,40,.06)] dark:shadow-none',
        className,
      )}
      {...p}
    />
  )
}
export function CardHeader({ className, ...p }: React.HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn('flex items-center justify-between gap-2 border-b px-4 py-3', className)}
      {...p}
    />
  )
}
export function CardTitle({ className, ...p }: React.HTMLAttributes<HTMLHeadingElement>) {
  return <h3 className={cn('text-sm font-semibold', className)} {...p} />
}
export function CardBody({ className, ...p }: React.HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('p-4', className)} {...p} />
}

// ---------- Loading / empty / error ----------
export function Skeleton({ className, ...p }: React.HTMLAttributes<HTMLDivElement>) {
  return <div aria-hidden className={cn('skeleton rounded-md', className)} {...p} />
}

export function EmptyState({
  icon,
  title,
  hint,
  action,
  className,
}: {
  icon?: React.ReactNode
  title: string
  hint?: string
  action?: React.ReactNode
  className?: string
}) {
  return (
    <div
      className={cn('flex flex-col items-center justify-center gap-2 py-12 text-center', className)}
    >
      {icon && (
        <div
          className="grid size-14 place-items-center rounded-full bg-muted text-faint [&_svg]:size-6"
          aria-hidden
        >
          {icon}
        </div>
      )}
      <div className="text-sm font-semibold">{title}</div>
      {hint && <div className="max-w-[48ch] text-[13px] text-muted-foreground">{hint}</div>}
      {action && <div className="mt-1">{action}</div>}
    </div>
  )
}

export function ErrorState({
  message,
  onRetry,
  className,
}: {
  message: string
  onRetry?: () => void
  className?: string
}) {
  return (
    <div
      role="alert"
      className={cn(
        'flex items-center gap-3 rounded-lg border border-err/30 bg-err-bg p-3 text-[13px]',
        className,
      )}
    >
      <CircleX className="size-4 shrink-0 text-err" aria-hidden />
      <span className="min-w-0 flex-1">{message}</span>
      {onRetry && (
        <Button variant="outline" size="sm" onClick={onRetry}>
          Retry
        </Button>
      )}
    </div>
  )
}

// ---------- Page chrome ----------
export function PageHeader({
  title,
  description,
  summary,
  actions,
  hint,
}: {
  title: string
  description?: string
  summary?: React.ReactNode
  actions?: React.ReactNode
  /** long explanation moved into a tooltip so the header stays one slim row */
  hint?: string
}) {
  return (
    <div className="mb-4 flex flex-wrap items-center justify-between gap-x-4 gap-y-2">
      <div className="flex min-w-0 items-baseline gap-3">
        <h1 className="text-xl leading-7 font-semibold tracking-[-0.01em]">{title}</h1>
        {hint && (
          <Tooltip label={hint}>
            <Info className="size-3.5 self-center text-faint" aria-label={hint} />
          </Tooltip>
        )}
        {summary && (
          <div className="flex items-center gap-3 text-[13px] text-muted-foreground">{summary}</div>
        )}
        {description && !summary && (
          <p className="hidden truncate text-[13px] text-muted-foreground min-[1100px]:block">
            {description}
          </p>
        )}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </div>
  )
}

/** Segmented control: quiet container, the active segment is a raised card (not filled accent). */
export function Segmented<T extends string | number>({
  value,
  onChange,
  options,
  className,
}: {
  value: T
  onChange: (v: T) => void
  options: Array<{ value: T; label: React.ReactNode }>
  className?: string
}) {
  return (
    <div className={cn('inline-flex rounded-md bg-muted p-0.5', className)} role="tablist">
      {options.map((o) => (
        <button
          key={String(o.value)}
          type="button"
          role="tab"
          aria-selected={o.value === value}
          onClick={() => onChange(o.value)}
          className={cn(
            'h-8 rounded-[6px] border border-transparent px-2.5 text-xs font-medium transition-colors duration-[120ms]',
            o.value === value
              ? 'border-border bg-card text-foreground'
              : 'text-muted-foreground hover:text-foreground',
          )}
        >
          {o.label}
        </button>
      ))}
    </div>
  )
}

/** Page-level underline tabs. */
export function Tabs<T extends string>({
  value,
  onChange,
  options,
}: {
  value: T
  onChange: (v: T) => void
  options: Array<{ value: T; label: React.ReactNode }>
}) {
  return (
    <div className="flex gap-4 border-b" role="tablist">
      {options.map((o) => (
        <button
          key={o.value}
          role="tab"
          aria-selected={o.value === value}
          onClick={() => onChange(o.value)}
          className={cn(
            '-mb-px h-9 border-b-2 px-0.5 text-[13px] font-medium transition-colors',
            o.value === value
              ? 'border-primary text-foreground'
              : 'border-transparent text-muted-foreground hover:text-foreground',
          )}
        >
          {o.label}
        </button>
      ))}
    </div>
  )
}

/** Compact pager. `variant="footer"` renders "1-25 of 21" inside a table card. */
export function Pagination({
  page,
  pages,
  onChange,
  total,
  pageSize,
}: {
  page: number
  pages: number
  onChange: (p: number) => void
  total?: number
  pageSize?: number
}) {
  const from = total != null && pageSize ? Math.min(total, page * pageSize + 1) : null
  const to = total != null && pageSize ? Math.min(total, (page + 1) * pageSize) : null
  return (
    <div className="flex items-center gap-2 text-xs text-muted-foreground">
      {total != null && (
        <span className="num">
          {from != null && to != null
            ? `${total === 0 ? 0 : from}-${to} of ${total}`
            : `${total} items`}
        </span>
      )}
      <Button variant="outline" size="sm" disabled={page <= 0} onClick={() => onChange(page - 1)}>
        Prev
      </Button>
      <span className="num">
        {page + 1} / {Math.max(1, pages)}
      </span>
      <Button
        variant="outline"
        size="sm"
        disabled={page >= pages - 1}
        onClick={() => onChange(page + 1)}
      >
        Next
      </Button>
    </div>
  )
}

export type { LucideIcon }
