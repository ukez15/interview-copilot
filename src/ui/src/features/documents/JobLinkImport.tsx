import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { Input, Label, Textarea } from '@/components/ui/form';
import { useApp } from '@/lib/app-context';
import type { DocumentSummary } from '@/types/api';

const INVALID_LINK = 'Enter a link that starts with http:// or https://.';
const UNREADABLE = "We couldn't read a job description from that page. Paste the text instead.";

interface Draft {
  name: string;
  text: string;
}

/** The link's http(s) URL, or null when it isn't one. */
function parseLink(value: string): URL | null {
  try {
    const url = new URL(value.trim());
    return url.protocol === 'http:' || url.protocol === 'https:' ? url : null;
  } catch {
    return null;
  }
}

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
  const [draft, setDraft] = useState<Draft | null>(null);
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
      const trimmed = draft.name.trim();
      const name = /\.txt$/i.test(trimmed) ? trimmed : `${trimmed}.txt`;
      const created = await api.uploadDocument(sessionId, new File([draft.text], name, { type: 'text/plain' }));
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
        <div className="grid gap-2">
          <div className="grid gap-1">
            <Label htmlFor="job-name">Document name</Label>
            <Input id="job-name" value={draft.name} onChange={(event) => setDraft({ ...draft, name: event.target.value })} />
          </div>
          <div className="grid gap-1">
            <Label htmlFor="job-text">Job description</Label>
            <Textarea id="job-text" rows={10} value={draft.text} onChange={(event) => setDraft({ ...draft, text: event.target.value })} />
          </div>
          <div className="flex gap-2">
            <Button disabled={saving || !draft.name.trim() || !draft.text.trim()} onClick={save}>
              Add to documents
            </Button>
            <Button variant="ghost" onClick={cancel}>
              Cancel
            </Button>
          </div>
        </div>
      )}
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
    </div>
  );
}
