import { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router';
import { Button } from '@/components/ui/button';
import { Card, CardDescription, CardTitle } from '@/components/ui/card';
import { useApp } from '@/lib/app-context';
import { QUESTION_TYPE_LABELS, type Transcript } from '@/types/api';

/** Formats an offset in milliseconds as hh:mm:ss. */
export function formatTimestamp(atMs: number): string {
  const totalSeconds = Math.floor(atMs / 1000);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(Math.floor(totalSeconds / 3600))}:${pad(Math.floor((totalSeconds % 3600) / 60))}:${pad(totalSeconds % 60)}`;
}

export function TranscriptPage() {
  const { sessionId = '' } = useParams();
  const { api } = useApp();
  const [transcript, setTranscript] = useState<Transcript | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [exportError, setExportError] = useState<string | null>(null);

  useEffect(() => {
    api.getTranscript(sessionId).then(setTranscript, (err: Error) => setError(err.message));
  }, [api, sessionId]);

  const entries = useMemo(() => [...(transcript?.entries ?? [])].sort((a, b) => a.atMs - b.atMs), [transcript]);
  const answers = useMemo(
    () => [...(transcript?.answers ?? [])].sort((a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt)),
    [transcript],
  );

  const attempts = useMemo(
    () => [...(transcript?.attempts ?? [])].sort((a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt)),
    [transcript],
  );

  async function exportPdf() {
    try {
      const blob = await api.downloadTranscriptPdf(sessionId);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `${transcript?.title ?? 'transcript'}.pdf`;
      link.click();
      URL.revokeObjectURL(url);
    } catch (err) {
      setExportError(err instanceof Error ? err.message : 'Export failed.');
    }
  }

  if (error) {
    return (
      <p role="alert" className="p-6 text-destructive">
        {error}
      </p>
    );
  }
  if (!transcript) {
    return <p className="p-6 text-muted-foreground">Loading…</p>;
  }

  return (
    <main className="mx-auto grid max-w-4xl gap-6 p-6">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <Link to={`/sessions/${sessionId}`} className="text-sm text-muted-foreground hover:underline">
            ← Back to session
          </Link>
          <h1 className="text-2xl font-semibold">{transcript.title}</h1>
          <CardDescription>{new Date(transcript.createdAt).toLocaleString()}</CardDescription>
        </div>
        <Button variant="outline" onClick={() => void exportPdf()}>
          Export PDF
        </Button>
      </div>
      {exportError && (
        <p role="alert" className="text-sm text-destructive">
          {exportError}
        </p>
      )}
      <Card className="grid gap-3">
        <CardTitle>Transcript</CardTitle>
        {entries.length === 0 ? (
          <CardDescription>No transcript was captured for this session.</CardDescription>
        ) : (
          <ul aria-label="Transcript" className="grid gap-2">
            {entries.map((entry, index) => (
              <li key={index} className="flex gap-3 text-sm">
                <span className="font-mono text-muted-foreground">{formatTimestamp(entry.atMs)}</span>
                <span>
                  <strong>{entry.speaker === 'Interviewer' ? 'Them' : 'Me'}:</strong> {entry.text}
                </span>
              </li>
            ))}
          </ul>
        )}
      </Card>
      <Card className="grid gap-3">
        <CardTitle>Suggested answers</CardTitle>
        {answers.length === 0 ? (
          <CardDescription>No suggested answers yet.</CardDescription>
        ) : (
          <ul aria-label="Suggested answers" className="grid gap-4">
            {answers.map((answer, index) => (
              <li key={index} className="grid gap-1">
                <div className="font-medium">{answer.question}</div>
                <div className="text-xs text-muted-foreground">
                  {QUESTION_TYPE_LABELS[answer.questionType]} · v{answer.version}
                </div>
                <p className="whitespace-pre-wrap text-sm">{answer.text}</p>
              </li>
            ))}
          </ul>
        )}
      </Card>
      {attempts.length > 0 && (
        <Card className="grid gap-3">
          <CardTitle>Practice</CardTitle>
          <ul aria-label="Practice" className="grid gap-4">
            {attempts.map((attempt) => (
              <li key={attempt.id} className="grid gap-1">
                <div className="font-medium">{attempt.question}</div>
                <div className="text-xs text-muted-foreground">Score {attempt.score}/10</div>
                <p className="whitespace-pre-wrap text-sm">
                  <strong>Answer:</strong> {attempt.answerText}
                </p>
                <p className="whitespace-pre-wrap text-sm">
                  <strong>Feedback:</strong> {attempt.feedback}
                </p>
              </li>
            ))}
          </ul>
        </Card>
      )}
    </main>
  );
}
