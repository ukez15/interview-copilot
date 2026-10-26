import { useId, type ReactNode } from 'react';
import { Input, Label, Textarea } from '@/components/ui/form';
import type { JobDraft } from './job-link';

/** The name + text editor for a job description draft; the caller supplies the action buttons as children. */
export function JobDraftEditor({
  draft,
  onChange,
  children,
}: {
  draft: JobDraft;
  onChange: (draft: JobDraft) => void;
  children: ReactNode;
}) {
  const id = useId();
  return (
    <div className="grid gap-2">
      <div className="grid gap-1">
        <Label htmlFor={`${id}-name`}>Document name</Label>
        <Input id={`${id}-name`} value={draft.name} onChange={(event) => onChange({ ...draft, name: event.target.value })} />
      </div>
      <div className="grid gap-1">
        <Label htmlFor={`${id}-text`}>Job description</Label>
        <Textarea id={`${id}-text`} rows={10} value={draft.text} onChange={(event) => onChange({ ...draft, text: event.target.value })} />
      </div>
      <div className="flex gap-2">{children}</div>
    </div>
  );
}
