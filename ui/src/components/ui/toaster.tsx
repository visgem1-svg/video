import { CircleCheck, CircleX, Info, X } from 'lucide-react'
import { dismissToast, useToasts, type ToastKind } from '@/lib/toast'
import { cn } from '@/lib/utils'

const STYLE: Record<ToastKind, { bar: string; icon: typeof Info; color: string }> = {
  success: { bar: 'bg-ok', icon: CircleCheck, color: 'text-ok' },
  error: { bar: 'bg-err', icon: CircleX, color: 'text-err' },
  info: { bar: 'bg-primary', icon: Info, color: 'text-primary' },
}

export function Toaster() {
  const toasts = useToasts()
  return (
    <div
      aria-live="polite"
      className="pointer-events-none fixed right-4 bottom-4 z-[70] flex w-[min(360px,calc(100vw-2rem))] flex-col gap-2"
    >
      {toasts.map((t) => {
        const s = STYLE[t.kind]
        const Icon = s.icon
        return (
          <div
            key={t.id}
            role={t.kind === 'error' ? 'alert' : 'status'}
            className="pointer-events-auto relative flex gap-3 overflow-hidden rounded-lg border border-border-strong bg-raised py-3 pr-9 pl-4 shadow-xl [animation:toast-in_180ms_ease-out]"
          >
            <span className={cn('absolute inset-y-0 left-0 w-[3px]', s.bar)} />
            <Icon className={cn('mt-0.5 size-4 shrink-0', s.color)} aria-hidden />
            <div className="min-w-0">
              <div className="text-[13px] font-semibold">{t.title}</div>
              {t.body && <div className="text-xs text-muted-foreground">{t.body}</div>}
            </div>
            <button
              aria-label="Dismiss"
              onClick={() => dismissToast(t.id)}
              className="absolute top-2 right-2 rounded p-1 text-faint hover:bg-muted"
            >
              <X className="size-3.5" />
            </button>
          </div>
        )
      })}
    </div>
  )
}
