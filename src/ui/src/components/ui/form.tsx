import type { ComponentProps } from 'react';
import { cn } from '@/lib/utils';

const field =
  'w-full rounded-[var(--radius-lg)] border border-border bg-transparent px-3 py-2 text-sm placeholder:text-muted-foreground focus-visible:outline-2 focus-visible:outline-primary disabled:opacity-50';

export function Input({ className, ...props }: ComponentProps<'input'>) {
  return <input className={cn(field, 'h-9', className)} {...props} />;
}

export function Textarea({ className, ...props }: ComponentProps<'textarea'>) {
  return <textarea className={cn(field, 'min-h-24', className)} {...props} />;
}

export function Label({ className, ...props }: ComponentProps<'label'>) {
  return <label className={cn('text-sm font-medium', className)} {...props} />;
}
