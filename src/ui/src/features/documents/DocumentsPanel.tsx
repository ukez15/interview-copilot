import { useState, type ChangeEvent } from 'react';
import { Button } from '@/components/ui/button';
import { CardDescription } from '@/components/ui/card';
import { useApp } from '@/lib/app-context';
import { MAX_DOCUMENTS_PER_SESSION, type DocumentSummary } from '@/types/api';
import { JobLinkImport } from './JobLinkImport';

/** FR-02: up to 10 grounding documents per session. */
export function DocumentsPanel({
  sessionId,
  documents,
  onChange,
}: {
  sessionId: string;
  documents: DocumentSummary[];
  onChange: (documents: DocumentSummary[]) => void;
}) {
  const { api } = useApp();
  const [error, setError] = useState<string | null>(null);
  const remaining = MAX_DOCUMENTS_PER_SESSION - documents.length;

  async function upload(event: ChangeEvent<HTMLInputElement>) {
    const files = Array.from(event.target.files ?? []);
    event.target.value = '';
    setError(null);
    if (files.length > remaining) {
      setError(`You can add ${remaining} more document${remaining === 1 ? '' : 's'} (max ${MAX_DOCUMENTS_PER_SESSION}).`);
      return;
    }
    let current = documents;
    for (const file of files) {
      try {
        current = [...current, await api.uploadDocument(sessionId, file)];
        onChange(current);
      } catch (err) {
        setError(err instanceof Error ? err.message : `Couldn't add ${file.name}.`);
        return;
      }
    }
  }

  async function remove(documentId: string) {
    try {
      await api.removeDocument(sessionId, documentId);
      onChange(documents.filter((d) => d.id !== documentId));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Couldn't remove the document.");
    }
  }

  return (
    <section className="grid gap-2">
      <h3 className="font-medium">
        Documents ({documents.length}/{MAX_DOCUMENTS_PER_SESSION})
      </h3>
      <CardDescription>Resume, job description, sales script, product docs. Plain text, Markdown, PDF and Word .docx files.</CardDescription>
      <ul className="grid gap-1">
        {documents.map((document) => (
          <li key={document.id} className="flex items-center justify-between rounded border border-border px-3 py-1.5 text-sm">
            <span>{document.fileName}</span>
            <Button variant="ghost" size="sm" onClick={() => remove(document.id)} aria-label={`Remove ${document.fileName}`}>
              Remove
            </Button>
          </li>
        ))}
      </ul>
      <label className="text-sm">
        <span className="sr-only">Add documents</span>
        <input type="file" multiple accept=".txt,.md,.markdown,.pdf,.docx" disabled={remaining === 0} onChange={upload} aria-label="Add documents" />
      </label>
      <JobLinkImport sessionId={sessionId} documents={documents} disabled={remaining === 0} onChange={onChange} />
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
    </section>
  );
}
