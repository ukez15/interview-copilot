import { useCallback, useEffect, useRef, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Card, CardDescription, CardTitle } from '@/components/ui/card';
import { useSessionEvents, type SessionEvent } from '@/features/copilot/useSessionEvents';
import { useApp } from '@/lib/app-context';
import type { PracticeAttempt } from '@/types/api';

/** Silence after the last Candidate segment that counts as "done answering". */
export const PRACTICE_SILENCE_MS = 4_000;

function ScoreCard({ attempt }: { attempt: PracticeAttempt }) {
  return (
    <div className="grid gap-1 rounded-md border p-3" aria-label="Score">
      <p className="text-lg font-semibold">{attempt.score}/10</p>
      <p className="whitespace-pre-wrap text-sm">{attempt.feedback}</p>
    </div>
  );
}

/**
 * Mock interview panel (#26): ask a question, show the candidate's live transcript, and score the answer when
 * "Done answering" is clicked or {@link PRACTICE_SILENCE_MS} pass without a new Candidate segment.
 */
export function PracticePanel({ sessionId, live }: { sessionId: string; live: boolean }) {
  const { api } = useApp();
  const [question, setQuestion] = useState<string | null>(null);
  const [segments, setSegments] = useState<string[]>([]);
  const [result, setResult] = useState<PracticeAttempt | null>(null);
  const [attempts, setAttempts] = useState<PracticeAttempt[]>([]);
  const [busy, setBusy] = useState<'asking' | 'scoring' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const open = useRef(false);
  const finishing = useRef(false);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  useEffect(() => {
    api.practiceAttempts(sessionId).then(setAttempts, (err: Error) => setError(err.message));
  }, [api, sessionId]);

  useEffect(() => () => clearTimeout(timer.current), []);

  // Whichever of click / silence comes first wins; the other is a no-op while scoring or once closed.
  const finish = useCallback(async () => {
    if (!open.current || finishing.current) {
      return;
    }
    finishing.current = true;
    clearTimeout(timer.current);
    setBusy('scoring');
    setError(null);
    try {
      const attempt = await api.practiceFinish(sessionId);
      open.current = false;
      setResult(attempt);
      setAttempts((previous) => [...previous, attempt]);
    } catch (err) {
      // The backend keeps the question and answer open after a failure, so the candidate can try again.
      setError(err instanceof Error ? err.message : 'Scoring failed.');
    } finally {
      finishing.current = false;
      setBusy(null);
    }
  }, [api, sessionId]);

  const onEvent = useCallback(
    (event: SessionEvent) => {
      const data = (event.data ?? {}) as { speaker?: string; text?: string };
      if (event.type !== 'transcript.segment' || data.speaker !== 'Candidate' || !data.text || !open.current || finishing.current) {
        return;
      }
      const text = data.text;
      setSegments((previous) => [...previous, text]);
      clearTimeout(timer.current);
      timer.current = setTimeout(() => void finish(), PRACTICE_SILENCE_MS);
    },
    [finish],
  );
  useSessionEvents(sessionId, onEvent);

  async function next() {
    clearTimeout(timer.current);
    open.current = false;
    setBusy('asking');
    setError(null);
    try {
      const asked = await api.practiceNext(sessionId);
      open.current = true;
      setQuestion(asked);
      setSegments([]);
      setResult(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not get a question.');
    } finally {
      setBusy(null);
    }
  }

  return (
    <section className="grid gap-4">
      {!live && <CardDescription>Start the session to practice.</CardDescription>}
      <div className="flex flex-wrap gap-2">
        <Button onClick={() => void next()} disabled={!live || busy !== null}>
          Next question
        </Button>
        <Button variant="outline" onClick={() => void finish()} disabled={!live || !question || result !== null || busy !== null}>
          Done answering
        </Button>
      </div>
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
      {busy === 'asking' && <p className="text-sm text-muted-foreground">Thinking of a question…</p>}
      {busy === 'scoring' && <p className="text-sm text-muted-foreground">Scoring your answer…</p>}
      {question && (
        <div className="grid gap-1">
          <CardTitle>Question</CardTitle>
          <p>{question}</p>
        </div>
      )}
      {question && (
        <div className="grid gap-1">
          <p className="text-sm font-medium">Your answer</p>
          {segments.length === 0 ? (
            <p className="text-sm text-muted-foreground">Listening…</p>
          ) : (
            <ul aria-label="Your answer" className="grid gap-1 text-sm">
              {segments.map((text, index) => (
                <li key={index}>{text}</li>
              ))}
            </ul>
          )}
        </div>
      )}
      {result && <ScoreCard attempt={result} />}
      {attempts.length > 0 && (
        <div className="grid gap-2">
          <CardTitle>Previous attempts</CardTitle>
          <ul aria-label="Previous attempts" className="grid gap-2">
            {attempts.map((attempt) => (
              <li key={attempt.id} className="text-sm">
                <span className="font-medium">{attempt.question}</span> — {attempt.score}/10
              </li>
            ))}
          </ul>
        </div>
      )}
    </section>
  );
}

export function PracticeCard({ sessionId, live }: { sessionId: string; live: boolean }) {
  return (
    <Card>
      <CardTitle className="mb-4">Practice</CardTitle>
      <PracticePanel sessionId={sessionId} live={live} />
    </Card>
  );
}
