import { useCallback, useState, type FormEvent } from 'react';
import { Pause, Play } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/form';
import { useApp } from '@/lib/app-context';
import { AnswerCard } from './AnswerCard';
import { useAnswerStream } from './useAnswerStream';
import { useSessionEvents, type SessionEvent } from './useSessionEvents';

/**
 * The live answer panel. Questions the interviewer asks are detected on the backend and answered
 * automatically (#22); typing or pasting a question here still works, and so do Regenerate/Shorter/Longer.
 */
export function LiveAssistant({
  sessionId,
  compact = false,
  autoAnswer: initialAutoAnswer = true,
}: {
  sessionId: string;
  compact?: boolean;
  /** Current auto-answer setting of the session; kept in sync afterwards through `autoanswer.state` events. */
  autoAnswer?: boolean;
}) {
  const { api } = useApp();
  const { answer, ask, beginAuto, applyEvent } = useAnswerStream(sessionId);
  const [question, setQuestion] = useState('');
  const [autoAnswer, setAutoAnswer] = useState(initialAutoAnswer);
  const [pausedQuestion, setPausedQuestion] = useState<string | null>(null);
  const [toggleError, setToggleError] = useState<string | null>(null);

  const onEvent = useCallback(
    (event: SessionEvent) => {
      const data = (event.data ?? {}) as { text?: string; enabled?: boolean };
      if (event.type === 'autoanswer.state') {
        setAutoAnswer(Boolean(data.enabled));
      } else if (event.type === 'question.detected' && data.text) {
        if (autoAnswer) {
          setPausedQuestion(null);
          beginAuto(data.text);
        } else {
          setPausedQuestion(data.text);
        }
      } else {
        applyEvent(event);
      }
    },
    [autoAnswer, beginAuto, applyEvent],
  );
  useSessionEvents(sessionId, onEvent);

  function submit(event: FormEvent) {
    event.preventDefault();
    if (question.trim()) {
      setPausedQuestion(null);
      void ask(question.trim());
    }
  }

  async function toggleAutoAnswer() {
    setToggleError(null);
    try {
      const session = await api.setAutoAnswer(sessionId, !autoAnswer);
      setAutoAnswer(session.autoAnswer);
    } catch (err) {
      setToggleError(err instanceof Error ? err.message : 'Could not change auto-answer.');
    }
  }

  return (
    <section className={compact ? 'grid gap-3' : 'grid gap-4'}>
      <div className="flex items-center justify-between gap-2">
        <p className="text-sm text-muted-foreground">{autoAnswer ? 'Auto-answer is on.' : 'Auto-answer is paused.'}</p>
        <Button size="sm" variant="outline" onClick={() => void toggleAutoAnswer()}>
          {autoAnswer ? <Pause className="size-3.5" /> : <Play className="size-3.5" />}
          {autoAnswer ? 'Pause auto-answer' : 'Resume auto-answer'}
        </Button>
      </div>
      {toggleError && (
        <p role="alert" className="text-sm text-destructive">
          {toggleError}
        </p>
      )}
      <form onSubmit={submit} className="flex gap-2">
        <Input aria-label="Question" placeholder="Type or paste the question you were asked" value={question} onChange={(e) => setQuestion(e.target.value)} />
        <Button type="submit">Answer</Button>
      </form>
      {pausedQuestion && (
        <div className="grid gap-2 rounded-md border border-border p-3">
          <p className="text-sm text-muted-foreground">{pausedQuestion}</p>
          <Button
            size="sm"
            className="w-fit"
            onClick={() => {
              const detected = pausedQuestion;
              setPausedQuestion(null);
              void ask(detected);
            }}
          >
            Answer it
          </Button>
        </div>
      )}
      <AnswerCard answer={answer} onModify={(modifier) => void ask(answer.question, modifier, answer)} />
    </section>
  );
}
