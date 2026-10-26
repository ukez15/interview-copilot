import { useCallback, useRef, useState } from 'react';
import type { SessionEvent } from './useSessionEvents';
import { useApp } from '@/lib/app-context';
import type { AnswerModifier, QuestionType } from '@/types/api';

export interface AnswerState {
  question: string;
  text: string;
  questionType: QuestionType | null;
  version: number;
  status: 'idle' | 'streaming' | 'done' | 'error';
  error: string | null;
}

const IDLE: AnswerState = { question: '', text: '', questionType: null, version: 0, status: 'idle', error: null };

/** Streams answers for one session; a new question cancels the previous stream (atlas/copilot-pipeline.md). */
export function useAnswerStream(sessionId: string, fallbackProvider = 'ollama') {
  const { api, platform } = useApp();
  const [answer, setAnswer] = useState<AnswerState>(IDLE);
  const controller = useRef<AbortController | null>(null);
  /** True from an auto-answered `question.detected` until its `answer.done`; only then are answer events applied. */
  const autoActive = useRef(false);

  const effectiveProvider = useCallback(async () => {
    try {
      const prefs = await api.getPreferences();
      return prefs.llmProvider || prefs.defaultLlmProvider || fallbackProvider;
    } catch {
      return fallbackProvider;
    }
  }, [api, fallbackProvider]);

  const ask = useCallback(
    async (question: string, modifier: AnswerModifier = 'None', previous?: AnswerState) => {
      controller.current?.abort();
      autoActive.current = false;
      const abort = new AbortController();
      controller.current = abort;
      setAnswer({ ...IDLE, question, status: 'streaming', version: previous?.version ?? 0 });

      // Manual answers of a session without a live key hand-off carry the key of the account's chat provider (#30).
      const llmApiKey = (await platform.getApiKey(await effectiveProvider())) ?? undefined;
      try {
        await api.ask(
          sessionId,
          {
            question,
            modifier,
            previousAnswer: previous?.text,
            previousVersion: previous?.version ?? 0,
            llmApiKey,
          },
          {
            onHeader: (header) => setAnswer((s) => ({ ...s, questionType: header.questionType, version: header.version })),
            onToken: (token) => setAnswer((s) => ({ ...s, text: s.text + token })),
            onError: (message) => setAnswer((s) => ({ ...s, status: 'error', error: message })),
            onDone: () => setAnswer((s) => ({ ...s, status: 'done' })),
          },
          abort.signal,
        );
      } catch (err) {
        if (!abort.signal.aborted) {
          setAnswer((s) => ({ ...s, status: 'error', error: err instanceof Error ? err.message : 'The answer failed.' }));
        }
      }
    },
    [api, platform, sessionId, effectiveProvider],
  );

  /** Starts showing an automatic answer for a detected question (#22); a newer one replaces it. */
  const beginAuto = useCallback((question: string) => {
    controller.current?.abort();
    autoActive.current = true;
    setAnswer({ ...IDLE, question, status: 'streaming' });
  }, []);

  /**
   * Applies `answer.delta` / `answer.done` / `error` events of the automatic answer. Ignored unless one is in
   * progress, so the events a manual answer also publishes don't double up with its own stream.
   */
  const applyEvent = useCallback((event: SessionEvent) => {
    if (!autoActive.current) {
      return;
    }
    const data = (event.data ?? {}) as { token?: string; version?: number; questionType?: QuestionType; message?: string };
    switch (event.type) {
      case 'answer.delta':
        setAnswer((s) => ({
          ...s,
          text: s.text + (data.token ?? ''),
          version: data.version ?? s.version,
          questionType: data.questionType ?? s.questionType,
        }));
        break;
      case 'answer.done':
        autoActive.current = false;
        setAnswer((s) => ({ ...s, status: 'done' }));
        break;
      case 'error':
        autoActive.current = false;
        setAnswer((s) => ({ ...s, status: 'error', error: data.message ?? 'The answer failed.' }));
        break;
    }
  }, []);

  return { answer, ask, beginAuto, applyEvent };
}
