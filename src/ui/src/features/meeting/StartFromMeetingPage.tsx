import { useEffect, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { Button } from '@/components/ui/button';
import { Card, CardDescription, CardTitle } from '@/components/ui/card';
import { SessionForm } from '@/features/sessions/SessionForm';
import { startLiveSession } from '@/features/sessions/start-live';
import { useApp } from '@/lib/app-context';
import type { CreateSessionInput, Session } from '@/types/api';

/** #16: pick the session (or make a new one) to use for the meeting that was just detected. */
export function StartFromMeetingPage() {
  const { api, platform } = useApp();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const app = params.get('app');
  const [sessions, setSessions] = useState<Session[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api.listSessions().then(
      (all) => setSessions(all.filter((s) => s.status === 'Draft' || s.status === 'Live')),
      (err: Error) => setError(err.message),
    );
  }, [api]);

  async function run(action: () => Promise<string>) {
    setBusy(true);
    setError(null);
    try {
      navigate(`/sessions/${await action()}`);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not start the session.');
    } finally {
      setBusy(false);
    }
  }

  const startWith = (session: Session) =>
    run(async () => {
      if (session.status === 'Draft') {
        await startLiveSession(api, platform, session.id);
        await platform.showOverlay(session.id);
      } else {
        await platform.showOverlay(session.id);
        await platform.startCapture(session.id);
      }
      return session.id;
    });

  const createAndStart = (input: CreateSessionInput, jobDescription?: File) =>
    run(async () => {
      const created = await api.createSession(input);
      if (jobDescription) {
        try {
          await api.uploadDocument(created.id, jobDescription);
        } catch {
          // The meeting is already on, so a failed job description upload must not block the live start. The user
          // can add it from the session's Documents panel.
        }
      }
      await startLiveSession(api, platform, created.id);
      await platform.showOverlay(created.id);
      return created.id;
    });

  return (
    <main className="mx-auto grid max-w-4xl gap-6 p-6 md:grid-cols-2">
      <section className="grid content-start gap-3">
        <h1 className="text-xl font-semibold">{app ? `${app} meeting detected` : 'Meeting detected'}</h1>
        <CardDescription>Pick a session to listen with. Nothing starts until you choose one.</CardDescription>
        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}
        {sessions.map((session) => (
          <div key={session.id} className="flex items-center justify-between gap-2 rounded-[var(--radius-lg)] border border-border p-3">
            <div className="min-w-0">
              <div className="font-medium">{session.title}</div>
              <div className="text-xs text-muted-foreground">
                {session.status} · {session.documents.length} docs
              </div>
            </div>
            <Button size="sm" disabled={busy} onClick={() => void startWith(session)}>
              Use this
            </Button>
          </div>
        ))}
        <Link to="/" className="text-sm underline">
          Not now
        </Link>
      </section>
      <Card>
        <CardTitle className="mb-4">New session</CardTitle>
        <SessionForm onSubmit={(input, jobDescription) => void createAndStart(input, jobDescription)} busy={busy} />
      </Card>
    </main>
  );
}
