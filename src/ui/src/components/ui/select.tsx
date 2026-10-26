import * as SelectPrimitive from '@radix-ui/react-select';
import { Check, ChevronDown } from 'lucide-react';
import { cn } from '@/lib/utils';

// Radix forbids an empty-string item value, so '' (none / default / system) travels as this sentinel.
export const NONE = '__none__';

const toRadix = (value: string) => (value === '' ? NONE : value);
const fromRadix = (value: string) => (value === NONE ? '' : value);

export interface SelectOption {
  value: string;
  label: string;
}

interface SelectProps {
  id?: string;
  value: string;
  onValueChange: (value: string) => void;
  options: SelectOption[];
  disabled?: boolean;
  className?: string;
}

/**
 * App-styled dropdown (shadcn/ui Select on Radix). The list renders inside the page, so it follows
 * the theme, unlike the native popup. `value` and `onValueChange` use '' for "none"; the sentinel
 * stays inside this component.
 */
export function Select({ id, value, onValueChange, options, disabled, className }: SelectProps) {
  return (
    <SelectPrimitive.Root value={toRadix(value)} onValueChange={(v) => onValueChange(fromRadix(v))} disabled={disabled}>
      <SelectPrimitive.Trigger
        id={id}
        className={cn(
          'flex h-9 w-full items-center justify-between gap-2 rounded-[var(--radius-lg)] border border-border bg-transparent px-3 py-2 text-left text-sm focus-visible:outline-2 focus-visible:outline-primary disabled:cursor-not-allowed disabled:opacity-50',
          className,
        )}
      >
        <span className="truncate">
          <SelectPrimitive.Value />
        </span>
        <SelectPrimitive.Icon asChild>
          <ChevronDown className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
        </SelectPrimitive.Icon>
      </SelectPrimitive.Trigger>
      <SelectPrimitive.Portal>
        <SelectPrimitive.Content
          position="popper"
          sideOffset={4}
          className="z-50 max-h-72 min-w-[var(--radix-select-trigger-width)] overflow-hidden rounded-[var(--radius-lg)] border border-border bg-background text-foreground shadow-lg"
        >
          <SelectPrimitive.Viewport className="max-h-72 p-1">
            {options.map((option) => (
              <SelectPrimitive.Item
                key={option.value}
                value={toRadix(option.value)}
                className="relative flex cursor-default select-none items-center rounded-[var(--radius-lg)] py-1.5 pr-3 pl-8 text-sm outline-none data-[highlighted]:bg-muted"
              >
                <span className="absolute left-2 flex size-4 items-center justify-center">
                  <SelectPrimitive.ItemIndicator>
                    <Check className="size-4" aria-hidden="true" />
                  </SelectPrimitive.ItemIndicator>
                </span>
                <SelectPrimitive.ItemText>{option.label}</SelectPrimitive.ItemText>
              </SelectPrimitive.Item>
            ))}
          </SelectPrimitive.Viewport>
        </SelectPrimitive.Content>
      </SelectPrimitive.Portal>
    </SelectPrimitive.Root>
  );
}
