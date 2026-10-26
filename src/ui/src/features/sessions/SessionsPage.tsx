import { useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { Button } from '@/components/ui/button';
import { Card, CardDescription, CardTitle } from '@/components/ui/card';
import { useApp } from '@/lib/app-context';
import type { CreateSessionInput, Session } from '@/types/api';
import { SessionForm } from './SessionForm';
import { SpeechModelBanner } from './SpeechModelBanner';

export function SessionsPage() {
  const { api } = useApp();
  const navigate = useNavigate();
  const [sessions, setSessions] = useState<Session[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api.listSessions().then(setSessions, (err: Error) => setError(err.message));
  }, [api]);

  async function create(input: CreateSessionInput, jobDescription?: File) {
    setBusy(true);
    try {
      const session = await api.createSession(input);
      if (jobDescription) {
        try {
          await api.uploadDocument(session.id, jobDescription);
        } catch (err) {
          // The session exists, so stay here with the reason instead of opening it without its job description.
          const reason = err instanceof Error && err.message ? err.message : 'Upload failed.';
          setSessions((current) => [session, ...current]);
          setError(`The session was created, but the job description couldn't be saved: ${reason} Add it from the session's Documents panel.`);
          return;
        }
      }
      navigate(`/sessions/${session.id}`);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not create the session.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="mx-auto grid max-w-4xl gap-6 p-6 md:grid-cols-2">
      <Card>
        <CardTitle className="mb-4">New session</CardTitle>
        <SessionForm onSubmit={create} busy={busy} />
      </Card>
      <section className="grid content-start gap-3">
        <div className="flex items-center justify-between">
          <h2 className="text-lg font-semibold">History</h2>
          <Button asChild variant="outline" size="sm">
            <Link to="/settings">Settings</Link>
          </Button>
        </div>
        <SpeechModelBanner />
        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}
        {sessions.length === 0 && !error && <CardDescription>No sessions yet.</CardDescription>}
        {sessions.map((session) => (
          <div key={session.id} className="flex items-center justify-between gap-2 rounded-[var(--radius-lg)] border border-border hover:bg-muted">
            <Link to={`/sessions/${session.id}`} className="min-w-0 flex-1 p-3">
              <div className="font-medium">{session.title}</div>
              <div className="text-xs text-muted-foreground">
                {session.status} · {new Date(session.createdAt).toLocaleString()} · {session.documents.length} docs
              </div>
            </Link>
            {session.status === 'Ended' && (
              <Link to={`/sessions/${session.id}/transcript`} className="p-3 text-sm underline">
                View transcript
              </Link>
            )}
          </div>
        ))}
      </section>
    </main>
  );
}
