import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router';
import { Button } from '@/components/ui/button';
import { Card, CardDescription, CardTitle } from '@/components/ui/card';
import { CaptureIndicator } from '@/features/audio/CaptureIndicator';
import { DocumentsPanel } from '@/features/documents/DocumentsPanel';
import { LiveAssistant } from '@/features/copilot/LiveAssistant';
import { useApp } from '@/lib/app-context';
import { QUESTION_TYPE_LABELS, type Session } from '@/types/api';
import { SessionSettingsCard } from './SessionSettingsCard';

export function SessionPage() {
  const { sessionId = '' } = useParams();
  const { api, platform } = useApp();
  const [session, setSession] = useState<Session | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.getSession(sessionId).then(setSession, (err: Error) => setError(err.message));
  }, [api, sessionId]);

  async function run(action: () => Promise<Session>) {
    try {
      setSession(await action());
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Something went wrong.');
    }
  }

  async function exportPdf() {
    try {
      const blob = await api.downloadTranscriptPdf(sessionId);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `${session?.title ?? 'transcript'}.pdf`;
      link.click();
      URL.revokeObjectURL(url);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Export failed.');
    }
  }

  if (error && !session) {
    return (
      <p role="alert" className="p-6 text-destructive">
        {error}
      </p>
    );
  }
  if (!session) {
    return <p className="p-6 text-muted-foreground">Loading…</p>;
  }

  return (
    <main className="mx-auto grid max-w-5xl gap-6 p-6 lg:grid-cols-[1fr_1.4fr]">
      <Card className="grid content-start gap-4">
        <div>
          <Link to="/" className="text-sm text-muted-foreground hover:underline">
            ← All sessions
          </Link>
          <CardTitle>{session.title}</CardTitle>
          <CardDescription>
            {session.answerLength} answers · {session.framework === 'None' ? 'no framework' : session.framework.toUpperCase()} ·{' '}
            {session.pinnedQuestionType ? `pinned: ${QUESTION_TYPE_LABELS[session.pinnedQuestionType]}` : 'auto-detect'}
          </CardDescription>
        </div>
        <CaptureIndicator active={session.status === 'Live'} />
        <DocumentsPanel sessionId={session.id} documents={session.documents} onChange={(documents) => setSession({ ...session, documents })} />
        <div className="flex flex-wrap gap-2">
          {session.status === 'Draft' && (
            <Button
              onClick={() =>
                void run(async () => {
                  const started = await api.startSession(session.id);
                  await platform.showOverlay(session.id);
                  await platform.startCapture(session.id);
                  return started;
                })
              }
            >
              Start session
            </Button>
          )}
          {session.status === 'Live' && (
            <Button
              variant="destructive"
              onClick={() =>
                void run(async () => {
                  await platform.stopCapture();
                  await platform.hideOverlay();
                  return api.endSession(session.id);
                })
              }
            >
              End session
            </Button>
          )}
          <Button asChild variant="outline">
            <Link to={`/sessions/${session.id}/transcript`}>View transcript</Link>
          </Button>
          <Button variant="outline" onClick={() => void exportPdf()}>
            Export PDF
          </Button>
        </div>
        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}
      </Card>
      <div className="grid content-start gap-6">
        <SessionSettingsCard session={session} onSaved={setSession} />
        <Card>
          <CardTitle className="mb-4">Live assistant</CardTitle>
          <LiveAssistant sessionId={session.id} autoAnswer={session.autoAnswer} />
        </Card>
      </div>
    </main>
  );
}
