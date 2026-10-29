import { useCallback, useEffect, useRef, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Card, CardDescription, CardTitle } from '@/components/ui/card';
import { MicStatus } from '@/features/audio/MicStatus';
import { useSessionEvents, type SessionEvent } from '@/features/copilot/useSessionEvents';
import { useApp } from '@/lib/app-context';
import type { PracticeAttempt } from '@/types/api';

/** Sent by the backend once the candidate has been quiet for 6 s after answering (#78). */
const PRACTICE_SILENCE_EVENT = 'practice.silence';

/** The score and feedback, saved or still being written (#78). */
function ScoreCard({ score, feedback }: { score: number | null; feedback: string }) {
  return (
    <div className="grid gap-1 rounded-md border p-3" aria-label="Score">
      <p className="text-lg font-semibold">{score === null ? '…' : `${score}/10`}</p>
      <p className="whitespace-pre-wrap text-sm">{feedback}</p>
    </div>
  );
}

const NOTHING_SCORED = { score: null, feedback: '' };

/**
 * Mock interview panel (#26), shown in the overlay window (#74): ask a question, show whether the mic is heard and
 * the candidate's live transcript, and score the answer when "Done answering" is clicked or the backend says the
 * candidate has been quiet for 6 s (#78). Silence is measured on the audio, not on when text arrives, so slow
 * transcription can't cut an answer short. The history lives in {@link PracticeSummaryCard}.
 */
export function PracticePanel({ sessionId, live }: { sessionId: string; live: boolean }) {
  const { api } = useApp();
  const [question, setQuestion] = useState<string | null>(null);
  // The question as the model writes it (#78); replaced by `question` once it's complete.
  const [draft, setDraft] = useState('');
  const [segments, setSegments] = useState<string[]>([]);
  const [result, setResult] = useState<PracticeAttempt | null>(null);
  // The score and feedback as the model writes them (#78); replaced by `result` once saved.
  const [scoring, setScoring] = useState<{ score: number | null; feedback: string }>(NOTHING_SCORED);
  const [busy, setBusy] = useState<'asking' | 'scoring' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const open = useRef(false);
  const finishing = useRef(false);
  // Where this answer starts in the mic stream; speech that started earlier but was transcribed late isn't it (#78).
  const answerFromMs = useRef(0);
  // Whichever of click / silence comes first wins; the other is a no-op while scoring or once closed.
  const finish = useCallback(async () => {
    if (!open.current || finishing.current) {
      return;
    }
    finishing.current = true;
    setBusy('scoring');
    setError(null);
    try {
      setScoring(NOTHING_SCORED);
      const attempt = await api.practiceFinish(sessionId, {
        onScore: (score) => setScoring((previous) => ({ ...previous, score })),
        onToken: (token) => setScoring((previous) => ({ ...previous, feedback: previous.feedback + token })),
        onRestart: () => setScoring(NOTHING_SCORED),
      });
      open.current = false;
      setResult(attempt);
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
      if (event.type === PRACTICE_SILENCE_EVENT) {
        void finish();
        return;
      }
      const data = (event.data ?? {}) as { speaker?: string; text?: string; startMs?: number };
      // Lines keep coming while scoring: the backend waits for the last words, and they're part of the answer.
      if (event.type !== 'transcript.segment' || data.speaker !== 'Candidate' || !data.text || !open.current) {
        return;
      }
      if ((data.startMs ?? 0) < answerFromMs.current) {
        return;
      }
      const text = data.text;
      setSegments((previous) => [...previous, text]);
    },
    [finish],
  );
  useSessionEvents(sessionId, onEvent);

  async function next() {
    open.current = false;
    setBusy('asking');
    setError(null);
    setQuestion(null);
    setDraft('');
    setSegments([]);
    setResult(null);
    try {
      const asked = await api.practiceNext(sessionId, (token) => setDraft((previous) => previous + token));
      answerFromMs.current = asked.answerFromMs;
      open.current = true;
      setQuestion(asked.question);
    } catch (err) {
      setDraft('');
      setError(err instanceof Error ? err.message : 'Could not get a question.');
    } finally {
      setBusy(null);
    }
  }

  return (
    <section className="grid gap-3 break-words">
      {!live && <CardDescription>Start the session to practice.</CardDescription>}
      <MicStatus active={live} paused={question === null} />
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
      {busy === 'asking' && !draft.trim() && <p className="text-sm text-muted-foreground">Thinking of a question…</p>}
      {busy === 'scoring' && scoring.score === null && !scoring.feedback && (
        <p className="text-sm text-muted-foreground">Scoring your answer…</p>
      )}
      {(question ?? draft.trim()) && (
        <div className="grid gap-1">
          <CardTitle>Question</CardTitle>
          <p aria-label="Question">{question ?? draft.trim()}</p>
        </div>
      )}
      {question && (
        <div className="grid gap-1">
          <p className="text-sm font-medium">Your answer</p>
          {result ? (
            // Once scored, show exactly what was marked rather than the live lines.
            <p aria-label="Your answer" className="whitespace-pre-wrap text-sm">
              {result.answerText}
            </p>
          ) : segments.length === 0 ? (
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
      {result ? (
        <ScoreCard score={result.score} feedback={result.feedback} />
      ) : (
        busy === 'scoring' && (scoring.score !== null || scoring.feedback) && <ScoreCard score={scoring.score} feedback={scoring.feedback.trimEnd()} />
      )}
    </section>
  );
}

/**
 * Main-window card for a mock interview (#74): the practice itself runs in the overlay, so this shows where to look
 * and the scored attempts. No event reaches this window when the overlay scores one, so the list reloads whenever
 * the window regains focus.
 */
export function PracticeSummaryCard({ sessionId, live }: { sessionId: string; live: boolean }) {
  const { api } = useApp();
  const [attempts, setAttempts] = useState<PracticeAttempt[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const load = () =>
      api.practiceAttempts(sessionId).then(
        (loaded) => {
          setAttempts(loaded);
          setError(null);
        },
        (err: Error) => setError(err.message),
      );
    void load();
    window.addEventListener('focus', load);
    return () => window.removeEventListener('focus', load);
  }, [api, sessionId]);

  return (
    <Card className="grid gap-4">
      <CardTitle>Practice</CardTitle>
      <CardDescription>{live ? 'Practising in the overlay.' : 'Start the session to practice.'}</CardDescription>
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
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
    </Card>
  );
}
