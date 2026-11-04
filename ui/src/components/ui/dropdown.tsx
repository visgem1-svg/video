import * as S from '@radix-ui/react-select'
import * as C from '@radix-ui/react-checkbox'
import { Check, ChevronDown } from 'lucide-react'
import { cn } from '@/lib/utils'

const EMPTY = '__all__' // Radix items cannot have an empty-string value

export interface Option {
  value: string
  label: string
}

/** Styled Radix select (replaces native <select>). `''` is a valid option value. */
export function Dropdown({
  value,
  onChange,
  options,
  className,
  ariaLabel,
  placeholder,
}: {
  value: string
  onChange: (v: string) => void
  options: Option[]
  className?: string
  ariaLabel: string
  placeholder?: string
}) {
  return (
    <S.Root
      value={value === '' ? EMPTY : value}
      onValueChange={(v) => onChange(v === EMPTY ? '' : v)}
    >
      <S.Trigger
        aria-label={ariaLabel}
        className={cn(
          'inline-flex h-9 w-44 items-center justify-between gap-2 rounded-md border border-border-strong bg-card px-3 text-[13px] hover:bg-muted data-[placeholder]:text-faint',
          className,
        )}
      >
        <S.Value placeholder={placeholder} />
        <S.Icon>
          <ChevronDown className="size-4 text-faint" />
        </S.Icon>
      </S.Trigger>
      <S.Portal>
        <S.Content
          position="popper"
          sideOffset={4}
          className="z-[60] max-h-72 min-w-[var(--radix-select-trigger-width)] overflow-hidden rounded-lg border border-border-strong bg-raised p-1 shadow-xl"
        >
          <S.Viewport>
            {options.map((o) => (
              <S.Item
                key={o.value || EMPTY}
                value={o.value === '' ? EMPTY : o.value}
                className="relative flex h-8 cursor-pointer items-center rounded-md pr-8 pl-2 text-[13px] outline-none select-none data-[highlighted]:bg-muted data-[state=checked]:font-medium"
              >
                <S.ItemText>{o.label}</S.ItemText>
                <S.ItemIndicator className="absolute right-2">
                  <Check className="size-4 text-primary" />
                </S.ItemIndicator>
              </S.Item>
            ))}
          </S.Viewport>
        </S.Content>
      </S.Portal>
    </S.Root>
  )
}

export function Checkbox({
  checked,
  onCheckedChange,
  ...props
}: {
  checked: boolean
  onCheckedChange: (v: boolean) => void
  'aria-label'?: string
}) {
  return (
    <C.Root
      checked={checked}
      onCheckedChange={(v) => onCheckedChange(v === true)}
      className="grid size-4 shrink-0 place-items-center rounded-[4px] border border-border-strong bg-card data-[state=checked]:border-primary data-[state=checked]:bg-primary"
      {...props}
    >
      <C.Indicator>
        <Check className="size-3 text-primary-foreground" strokeWidth={3} />
      </C.Indicator>
    </C.Root>
  )
}
