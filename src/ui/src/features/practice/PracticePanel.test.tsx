import { act, fireEvent, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createWebPlatform } from '@/platform';
import { FakeWebSocket } from '@/test/fakeWebSocket';
import { fakeApi, json, renderWithApp, sampleSession } from '@/test/helpers';
import type { PracticeAttempt } from '@/types/api';
import { PRACTICE_SILENCE_MS, PracticePanel } from './PracticePanel';

const attempt: PracticeAttempt = {
  id: 'a1',
  question: 'Why us?',
  answerText: 'Because.',
  score: 7,
  feedback: 'Add numbers.',
  createdAt: '2026-10-15T09:00:00Z',
};

async function flush() {
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
  });
}

interface Calls {
  next: number;
  finish: number;
}

function setup(options: { attempts?: PracticeAttempt[]; finish?: () => Response; next?: () => Response; live?: boolean } = {}) {
  const calls: Calls = { next: 0, finish: 0 };
  const { api } = fakeApi((url, init) => {
    if (url.endsWith('/practice/next')) {
      calls.next += 1;
      return options.next ? options.next() : json({ question: 'Why us?' });
    }
    if (url.endsWith('/practice/finish')) {
      calls.finish += 1;
      return options.finish ? options.finish() : json(attempt);
    }
    if (url.endsWith('/practice/attempts') && init.method !== 'POST') {
      return json(options.attempts ?? []);
    }
    return json(sampleSession);
  });
  return { api, calls, live: options.live ?? true };
}

async function mount(ctx: ReturnType<typeof setup>) {
  const platform = createWebPlatform();
  await platform.setAuthTokens({ accessToken: 'tok', refreshToken: 'r' });
  renderWithApp(<PracticePanel sessionId="s1" live={ctx.live} />, { api: ctx.api, platform });
  await flush();
  act(() => FakeWebSocket.last.open());
}

function segment(speaker: 'Candidate' | 'Interviewer', text: string) {
  act(() => FakeWebSocket.last.emit({ id: 1, type: 'transcript.segment', data: { speaker, text } }));
}

describe('PracticePanel', () => {
  beforeEach(() => {
    FakeWebSocket.reset();
    vi.stubGlobal('WebSocket', FakeWebSocket);
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('asks a question and shows only candidate segments as the live answer', async () => {
    const ctx = setup();
    await mount(ctx);
    segment('Candidate', 'before the question');

    fireEvent.click(screen.getByRole('button', { name: 'Next question' }));
    await flush();
    expect(screen.getByText('Why us?')).toBeInTheDocument();
    expect(screen.getByText('Listening…')).toBeInTheDocument();
    segment('Interviewer', 'ignored words');
    segment('Candidate', 'My first point.');
    segment('Candidate', 'My second point.');

    const items = screen.getAllByRole('listitem').map((li) => li.textContent);
    expect(items).toEqual(['My first point.', 'My second point.']);
    expect(screen.queryByText('ignored words')).not.toBeInTheDocument();
  });

  it('scores on "Done answering" click and shows the score card and attempts', async () => {
    const ctx = setup();
    await mount(ctx);
    fireEvent.click(screen.getByRole('button', { name: 'Next question' }));
    await flush();
    segment('Candidate', 'Because.');

    fireEvent.click(screen.getByRole('button', { name: 'Done answering' }));
    await flush();

    expect(ctx.calls.finish).toBe(1);
    expect(screen.getByLabelText('Score')).toHaveTextContent('7/10');
    expect(screen.getByLabelText('Score')).toHaveTextContent('Add numbers.');
    expect(screen.getByRole('list', { name: 'Previous attempts' })).toHaveTextContent('Why us? — 7/10');

    // The 4 s timer armed by the segment must not score a second time.
    await act(async () => {
      await vi.advanceTimersByTimeAsync(PRACTICE_SILENCE_MS * 2);
    });
    expect(ctx.calls.finish).toBe(1);
  });

  it('finishes once after 4 s of silence, counted from the last candidate segment', async () => {
    const ctx = setup();
    await mount(ctx);
    fireEvent.click(screen.getByRole('button', { name: 'Next question' }));
    await flush();
    segment('Candidate', 'One.');
    await act(async () => {
      await vi.advanceTimersByTimeAsync(PRACTICE_SILENCE_MS - 1_000);
    });
    segment('Candidate', 'Two.');
    await act(async () => {
      await vi.advanceTimersByTimeAsync(PRACTICE_SILENCE_MS - 1);
    });
    expect(ctx.calls.finish).toBe(0);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(1);
    });

    expect(ctx.calls.finish).toBe(1);
    expect(screen.getByLabelText('Score')).toHaveTextContent('7/10');

    // A click after the silence-triggered finish does nothing (the button is disabled).
    expect(screen.getByRole('button', { name: 'Done answering' })).toBeDisabled();
    expect(ctx.calls.finish).toBe(1);
  });

  it('does not auto-finish when no candidate segment arrived', async () => {
    const ctx = setup();
    await mount(ctx);
    fireEvent.click(screen.getByRole('button', { name: 'Next question' }));
    await flush();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(PRACTICE_SILENCE_MS * 3);
    });

    expect(ctx.calls.finish).toBe(0);
  });

  it('shows a readable error and allows trying again when scoring fails', async () => {
    let fail = true;
    const ctx = setup({ finish: () => (fail ? json({ detail: 'No answer heard.' }, 400) : json(attempt)) });
    await mount(ctx);
    fireEvent.click(screen.getByRole('button', { name: 'Next question' }));
    await flush();

    fireEvent.click(screen.getByRole('button', { name: 'Done answering' }));
    await flush();
    expect(screen.getByRole('alert')).toHaveTextContent('No answer heard.');

    fail = false;
    fireEvent.click(screen.getByRole('button', { name: 'Done answering' }));
    await flush();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Score')).toHaveTextContent('7/10');
  });

  it('shows an error when the next question fails and lists earlier attempts', async () => {
    const ctx = setup({ attempts: [attempt], next: () => json({ detail: 'Model down' }, 503) });
    await mount(ctx);

    expect(screen.getByRole('list', { name: 'Previous attempts' })).toHaveTextContent('Why us? — 7/10');
    fireEvent.click(screen.getByRole('button', { name: 'Next question' }));
    await flush();

    expect(screen.getByRole('alert')).toHaveTextContent('Model down');
    expect(screen.getByRole('button', { name: 'Done answering' })).toBeDisabled();
  });

  it('clears the previous result when a new question is asked', async () => {
    const ctx = setup();
    await mount(ctx);
    fireEvent.click(screen.getByRole('button', { name: 'Next question' }));
    await flush();
    segment('Candidate', 'Because.');
    fireEvent.click(screen.getByRole('button', { name: 'Done answering' }));
    await flush();

    fireEvent.click(screen.getByRole('button', { name: 'Next question' }));
    await flush();

    expect(screen.queryByLabelText('Score')).not.toBeInTheDocument();
    expect(screen.getByText('Listening…')).toBeInTheDocument();
  });

  it('disables practice until the session is live', async () => {
    const ctx = setup({ live: false });
    await mount(ctx);

    expect(screen.getByRole('button', { name: 'Next question' })).toBeDisabled();
    expect(screen.getByText('Start the session to practice.')).toBeInTheDocument();
  });
});
