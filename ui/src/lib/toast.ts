import { useSyncExternalStore } from 'react'

export type ToastKind = 'success' | 'error' | 'info'
export interface ToastItem {
  id: number
  kind: ToastKind
  title: string
  body?: string
}

let items: ToastItem[] = []
let nextId = 1
const listeners = new Set<() => void>()
const emit = () => listeners.forEach((l) => l())

/** Auto-dismiss: 4 s, errors 8 s (DESIGN.md toasts). */
export const TOAST_MS: Record<ToastKind, number> = { success: 4000, info: 4000, error: 8000 }

export function dismissToast(id: number) {
  items = items.filter((t) => t.id !== id)
  emit()
}

function push(kind: ToastKind, title: string, body?: string) {
  const id = nextId++
  items = [...items.slice(-3), { id, kind, title, body }]
  emit()
  setTimeout(() => dismissToast(id), TOAST_MS[kind])
  return id
}

export const toast = {
  success: (title: string, body?: string) => push('success', title, body),
  error: (title: string, body?: string) => push('error', title, body),
  info: (title: string, body?: string) => push('info', title, body),
}

export const useToasts = () =>
  useSyncExternalStore(
    (cb) => {
      listeners.add(cb)
      return () => listeners.delete(cb)
    },
    () => items,
  )

/** test helper */
export function clearToasts() {
  items = []
  emit()
}
