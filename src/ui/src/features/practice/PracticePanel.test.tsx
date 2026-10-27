import { act, fireEvent, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createWebPlatform } from '@/platform';
import { FakeWebSocket } from '@/test/fakeWebSocket';
import { fakeApi, json, renderWithApp, sampleSession, sse } from '@/test/helpers';
import type { PracticeAttempt } from '@/types/api';
import { PRACTICE_SILENCE_MS, PracticePanel, PracticeSummaryCard } from './PracticePanel';

const attempt: PracticeAttempt = {
  id: 'a1',
  question: 'Why us?',
  answerText: 'Because.',
  score: 7,
  feedback: 'Add numbers.',
  createdAt: '2026-10-15T09:00:00Z',
};

function questionStream(question: string, answerFromMs = 0) {
  return sse(['delta', question], ['done', JSON.stringify({ question, answerFromMs })]);
}

/** A question stream the test writes to as it goes, to see the text arrive in pieces. */
function liveStream() {
  const encoder = new TextEncoder();
  let controller!: ReadableStreamDefaultController<Uint8Array>;
  const body = new ReadableStream<Uint8Array>({ start: (c) => void (controller = c) });
  return {
    response: () => new Response(body, { headers: { 'Content-Type': 'text/event-stream' } }),
    send: (event: string, data: string) => controller.enqueue(encoder.encode(`event: ${event}\ndata: ${data}\n\n`)),
    close: () => controller.close(),
  };
}

/** Lets the stream reader catch up: it hops through several promises per chunk. */
async function settle() {
  await act(async () => {
    for (let i = 0; i < 30; i++) {
      await Promise.resolve();
    }
  });
}

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
      return options.next ? options.next() : questionStream('Why us?');
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

  it('shows the question word by word as it is written, and opens it for the answer once complete (#78)', async () => {
    const stream = liveStream();
    const ctx = setup({ next: stream.response });
    await mount(ctx);

    fireEvent.click(screen.getByRole('button', { name: 'Next question' }));
    await settle();
    expect(screen.getByText('Thinking of a question…')).toBeInTheDocument();

    stream.send('delta', 'Why ');
    await settle();
    expect(screen.getByLabelText('Question')).toHaveTextContent('Why');
    expect(screen.queryByText('Thinking of a question…')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Done answering' })).toBeDisabled();

    stream.send('delta', 'us?');
    stream.send('done', JSON.stringify({ question: 'Why us?', answerFromMs: 0 }));
    stream.close();
    await settle();
    expect(screen.getByLabelText('Question')).toHaveTextContent('Why us?');
    expect(screen.getByText('Listening…')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Done answering' })).toBeEnabled();
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

  it('scores on "Done answering" click and shows the score card, leaving the history to the main window', async () => {
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
    expect(screen.queryByRole('list', { name: 'Previous attempts' })).not.toBeInTheDocument();

    // The 4 s timer armed by the segment must not score a second time.
    await act(async () => {
      await vi.advanceTimersByTimeAsync(PRACTICE_SILENCE_MS * 2);
    });
    expect(ctx.calls.finish).toBe(1);
  });

  it('ignores speech that started before the question was shown, even when it arrives late (#78)', async () => {
    const ctx = setup({ next: () => questionStream('Why us?', 42_000) });
    await mount(ctx);
    fireEvent.click(screen.getByRole('button', { name: 'Next question' }));
    await flush();

    act(() =>
      FakeWebSocket.last.emit({ id: 1, type: 'transcript.segment', data: { speaker: 'Candidate', text: 'said while waiting', startMs: 30_000 } }),
    );
    act(() => FakeWebSocket.last.emit({ id: 2, type: 'transcript.segment', data: { speaker: 'Candidate', text: 'my answer', startMs: 42_000 } }));

    expect(screen.queryByText('said while waiting')).not.toBeInTheDocument();
    expect(screen.getByText('my answer')).toBeInTheDocument();
  });

  it('shows the exact scored answer in place of the live lines once scored', async () => {
    const ctx = setup();
    await mount(ctx);
    fireEvent.click(screen.getByRole('button', { name: 'Next question' }));
    await flush();
    segment('Candidate', 'live words');
    expect(screen.getByText('live words')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Done answering' }));
    await flush();

    expect(screen.getByLabelText('Your answer')).toHaveTextContent(attempt.answerText);
    expect(screen.queryByText('live words')).not.toBeInTheDocument();
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

  it('shows the stream error and keeps "Done answering" off when the model fails part-way', async () => {
    const ctx = setup({ next: () => sse(['delta', 'Why'], ['error', "Can't reach Ollama."]) });
    await mount(ctx);

    fireEvent.click(screen.getByRole('button', { name: 'Next question' }));
    await settle();

    expect(screen.getByRole('alert')).toHaveTextContent("Can't reach Ollama.");
    expect(screen.queryByLabelText('Question')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Done answering' })).toBeDisabled();
  });

  it('shows an error when the next question fails', async () => {
    const ctx = setup({ next: () => json({ detail: 'Model down' }, 503) });
    await mount(ctx);

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
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  it('shows whether the mic is heard while live', async () => {
    const ctx = setup();
    const platform = createWebPlatform();
    vi.spyOn(platform, 'getCaptureStatus').mockResolvedValue({ state: 'live', interviewer: false, mic: true, message: null });
    await platform.setAuthTokens({ accessToken: 'tok', refreshToken: 'r' });
    renderWithApp(<PracticePanel sessionId="s1" live />, { api: ctx.api, platform });
    await flush();

    expect(screen.getByRole('status')).toHaveTextContent('Mic ✓ Listening');
  });
});

describe('PracticeSummaryCard', () => {
  function mountSummary(options: { live?: boolean; attempts?: () => Response } = {}) {
    let calls = 0;
    const { api } = fakeApi(() => {
      calls += 1;
      return options.attempts ? options.attempts() : json([attempt]);
    });
    renderWithApp(<PracticeSummaryCard sessionId="s1" live={options.live ?? true} />, { api });
    return { calls: () => calls };
  }

  it('points to the overlay while live and lists earlier attempts', async () => {
    mountSummary();

    expect(screen.getByText('Practising in the overlay.')).toBeInTheDocument();
    expect(await screen.findByRole('list', { name: 'Previous attempts' })).toHaveTextContent('Why us? — 7/10');
    expect(screen.queryByRole('button', { name: 'Next question' })).not.toBeInTheDocument();
  });

  it('asks to start the session when not live', () => {
    mountSummary({ live: false });

    expect(screen.getByText('Start the session to practice.')).toBeInTheDocument();
  });

  it('reloads the attempts when the window regains focus, so overlay scores show up', async () => {
    const view = mountSummary();
    await screen.findByRole('list', { name: 'Previous attempts' });
    expect(view.calls()).toBe(1);

    await act(async () => {
      window.dispatchEvent(new Event('focus'));
      await Promise.resolve();
    });

    expect(view.calls()).toBe(2);
  });

  it('shows an error when the attempts cannot be loaded', async () => {
    mountSummary({ attempts: () => json({ detail: 'Backend down' }, 500) });

    expect(await screen.findByRole('alert')).toHaveTextContent('Backend down');
  });
});
