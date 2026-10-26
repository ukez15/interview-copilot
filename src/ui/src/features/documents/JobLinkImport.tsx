import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { Input, Label } from '@/components/ui/form';
import { useApp } from '@/lib/app-context';
import type { DocumentSummary } from '@/types/api';
import { JobDraftEditor } from './JobDraftEditor';
import { INVALID_LINK, UNREADABLE, normaliseTxtName, parseLink, type JobDraft } from './job-link';

/** Reads a job description from a link into an editable draft; nothing is saved until "Add to documents". */
export function JobLinkImport({
  sessionId,
  documents,
  disabled,
  onChange,
}: {
  sessionId: string;
  documents: DocumentSummary[];
  disabled: boolean;
  onChange: (documents: DocumentSummary[]) => void;
}) {
  const { api } = useApp();
  const [link, setLink] = useState('');
  const [draft, setDraft] = useState<JobDraft | null>(null);
  const [fetching, setFetching] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function fetchPreview() {
    setError(null);
    const url = parseLink(link);
    if (!url) {
      setError(INVALID_LINK);
      return;
    }
    setFetching(true);
    try {
      const preview = await api.previewJobPosting(sessionId, url.href);
      setDraft({ name: preview.suggestedName, text: preview.text });
    } catch (err) {
      // Same editor, empty: the user pastes the text instead.
      setError(err instanceof Error && err.message ? err.message : UNREADABLE);
      setDraft({ name: `${url.hostname}.txt`, text: '' });
    } finally {
      setFetching(false);
    }
  }

  async function save() {
    if (!draft) {
      return;
    }
    setError(null);
    setSaving(true);
    try {
      const created = await api.uploadDocument(sessionId, new File([draft.text], normaliseTxtName(draft.name), { type: 'text/plain' }));
      onChange([...documents, created]);
      setLink('');
      setDraft(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Couldn't add the job description.");
    } finally {
      setSaving(false);
    }
  }

  function cancel() {
    setDraft(null);
    setError(null);
  }

  return (
    <div className="grid gap-2">
      <div className="grid gap-1">
        <Label htmlFor="job-link">Job posting link</Label>
        <div className="flex gap-2">
          <Input
            id="job-link"
            type="url"
            value={link}
            disabled={disabled || fetching}
            onChange={(event) => setLink(event.target.value)}
            placeholder="https://"
          />
          <Button variant="outline" disabled={disabled || fetching || !link.trim()} onClick={fetchPreview}>
            Get job description
          </Button>
        </div>
      </div>
      {draft && (
        <JobDraftEditor draft={draft} onChange={setDraft}>
          <Button disabled={saving || !draft.name.trim() || !draft.text.trim()} onClick={save}>
            Add to documents
          </Button>
          <Button variant="ghost" onClick={cancel}>
            Cancel
          </Button>
        </JobDraftEditor>
      )}
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
    </div>
  );
}
