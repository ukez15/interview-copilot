import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createOverlay } from './content/overlay';
import { renderStatus } from './popup/render';
import { BackendError, claimPairing, liveSessions, streamAnswer, toAnswerEvent, UnauthorizedError, type AnswerEvent } from './shared/backend';
import { browserName } from './shared/browser';
import { ANSWER_PORT, type AnswerPortMessage } from './shared/messages';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });

function sse(...events: [string, string][]): Response {
  return new Response(events.map(([e, d]) => `event: ${e}\r\ndata: ${d}\r\n\r\n`).join(''));
}

describe('backend client', () => {
  it('claims a pairing code with the browser name, then sends only the pairing token header', async () => {
    const fetchImpl = vi
      .fn()
      .mockResolvedValueOnce(json({ token: 'paired-tok' }))
      .mockResolvedValueOnce(json([{ id: 'a', title: 'A', status: 'Live' }, { id: 'b', title: 'B', status: 'Ended' }]));

    const token = await claimPairing('123456', 'Edge', fetchImpl);
    const sessions = await liveSessions(token, fetchImpl);

    expect(token).toBe('paired-tok');
    expect(sessions.map((s) => s.id)).toEqual(['a']);
    expect(fetchImpl.mock.calls[0]![0]).toBe('http://127.0.0.1:5480/api/pairing/claim');
    expect(JSON.parse(fetchImpl.mock.calls[0]![1].body as string)).toEqual({ code: '123456', name: 'Edge' });
    expect(fetchImpl.mock.calls[0]![1].headers['X-Pairing-Token']).toBeUndefined();
    expect(fetchImpl.mock.calls[1]![1].headers['X-Pairing-Token']).toBe('paired-tok');
    expect(fetchImpl.mock.calls[1]![1].headers.Authorization).toBeUndefined();
  });

  it('explains a rejected or rate limited code', async () => {
    const rejected = () => Promise.resolve(new Response(null, { status: 400 }));
    const limited = () => Promise.resolve(new Response(null, { status: 429 }));

    await expect(claimPairing('000000', 'Chrome', rejected)).rejects.toThrow('That pairing code is invalid or expired.');
    await expect(claimPairing('000000', 'Chrome', limited)).rejects.toThrow('Too many tries');
  });

  it('streams answer events across chunk boundaries', async () => {
    const events: AnswerEvent[] = [];
    const fetchImpl = vi.fn().mockResolvedValue(sse(['meta', '{"questionType":"Coding","version":1}'], ['delta', ' Hi'], ['unknown', 'x'], ['done', '']));

    await streamAnswer('tok', 's 1', 'Q?', (e) => events.push(e), fetchImpl);

    expect(fetchImpl.mock.calls[0]![0]).toBe('http://127.0.0.1:5480/api/sessions/s%201/answers');
    expect(events).toEqual([{ type: 'meta', questionType: 'Coding', version: 1 }, { type: 'delta', token: ' Hi' }, { type: 'done' }]);
  });

  it('parses error events', () => {
    expect(toAnswerEvent('event: error\ndata: Ollama down')).toEqual({ type: 'error', message: 'Ollama down' });
  });

  it.each([
    [() => Promise.reject(new TypeError('offline')), 'Desktop app not running'],
    [() => Promise.resolve(new Response(null, { status: 401 })), 'Pair again with a new code from the desktop app.'],
    [() => Promise.resolve(new Response(null, { status: 500 })), '500'],
  ])('explains failures', async (fetchImpl, message) => {
    await expect(liveSessions('t', fetchImpl)).rejects.toThrow(message);
  });

  it('raises UnauthorizedError on a 401 so the token can be dropped', async () => {
    await expect(liveSessions('t', () => Promise.resolve(new Response(null, { status: 401 })))).rejects.toBeInstanceOf(UnauthorizedError);
  });

  it('fails on an empty stream', async () => {
    await expect(streamAnswer('t', 's', 'q', vi.fn(), () => Promise.resolve(new Response(null)))).rejects.toBeInstanceOf(BackendError);
  });
});

describe('browserName', () => {
  it.each([
    ['Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Edg/126.0.0.0', 'Edge'],
    ['Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36', 'Chrome'],
    ['Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0', 'Firefox'],
    ['SomethingElse/1.0', 'Browser'],
  ])('names %#', (userAgent, expected) => {
    expect(browserName(userAgent)).toBe(expected);
  });
});

describe('overlay', () => {
  it('lists sessions, asks, and renders streamed events', () => {
    const onAsk = vi.fn();
    const onClose = vi.fn();
    const overlay = createOverlay(document, { onAsk, onClose });
    document.body.append(overlay.host);
    const root = overlay.host.shadowRoot!;

    overlay.setSessions([{ id: 's1', title: 'Acme', status: 'Live' }]);
    (root.querySelector('[data-question]') as HTMLInputElement).value = 'Why us?';
    root.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    overlay.apply({ type: 'meta', questionType: 'Experience', version: 1 });
    overlay.apply({ type: 'delta', token: 'Because' });
    overlay.apply({ type: 'done' });
    (root.querySelector('[data-close]') as HTMLButtonElement).click();

    expect(onAsk).toHaveBeenCalledWith('s1', 'Why us?');
    expect(root.querySelector('[data-answer]')!.textContent).toBe('Because');
    expect(root.querySelector('[data-meta]')!.textContent).toBe('Experience · v1');
    expect((root.querySelector('.panel') as HTMLElement).style.opacity).toBe('0.85');
    expect(onClose).toHaveBeenCalled();
  });

  it('guides the user when there is no live session or question', () => {
    const onAsk = vi.fn();
    const overlay = createOverlay(document, { onAsk, onClose: vi.fn() }, 0.5);
    const root = overlay.host.shadowRoot!;
    const error = () => root.querySelector('[data-error]')!.textContent;

    overlay.setSessions([]);
    expect(error()).toContain('No live session');
    (root.querySelector('[data-question]') as HTMLInputElement).value = 'Q?';
    root.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    expect(error()).toContain('Start a session');
    overlay.setSessions([], 'Pair the extension first');
    overlay.apply({ type: 'error', message: 'Ollama down' });

    expect(error()).toBe('Ollama down');
    expect(onAsk).not.toHaveBeenCalled();
  });
});

describe('popup', () => {
  beforeEach(() => {
    document.body.innerHTML = '<form id="pair"></form><section id="paired"></section><p id="sessions"></p><p id="error"></p>';
  });

  it('shows the pairing form when not paired and live sessions when paired', () => {
    renderStatus(document, { paired: false, sessions: [], error: 'Desktop app not running' });
    expect(document.getElementById('pair')!.hidden).toBe(false);
    expect(document.getElementById('paired')!.hidden).toBe(true);
    expect(document.getElementById('error')!.textContent).toBe('Desktop app not running');

    renderStatus(document, { paired: true, sessions: [{ id: 'a', title: 'Acme', status: 'Live' }] });
    expect(document.getElementById('pair')!.hidden).toBe(true);
    expect(document.getElementById('paired')!.hidden).toBe(false);
    expect(document.getElementById('sessions')!.textContent).toContain('Live: Acme');

    renderStatus(document, { paired: true, sessions: [] });
    expect(document.getElementById('sessions')!.textContent).toContain('No live session');
  });
});

describe('background worker', () => {
  const store: Record<string, unknown> = {};
  let onMessage: (request: unknown, sender: unknown, respond: (value: unknown) => void) => boolean;
  const sessions = [{ id: 'a', title: 'A', status: 'Live' }];

  beforeEach(() => {
    vi.resetModules();
    for (const key of Object.keys(store)) delete store[key];
    vi.stubGlobal('navigator', { userAgent: 'Mozilla/5.0 (X11; rv:128.0) Gecko/20100101 Firefox/128.0' });
    vi.stubGlobal('chrome', {
      storage: {
        local: {
          get: vi.fn((key: string) => Promise.resolve({ [key]: store[key] })),
          set: vi.fn((values: Record<string, unknown>) => Promise.resolve(Object.assign(store, values))),
          remove: vi.fn((key: string) => Promise.resolve(delete store[key])),
        },
      },
      runtime: {
        onMessage: { addListener: vi.fn((fn) => (onMessage = fn)) },
        onConnect: { addListener: vi.fn() },
      },
    });
  });

  it('pairs with a code, keeps the token in local storage, reports status and unpairs', async () => {
    const fetchImpl = vi.fn((...args: [string, RequestInit?]) => Promise.resolve(args[0].endsWith('/claim') ? json({ token: 'paired-tok' }) : json(sessions)));
    vi.stubGlobal('fetch', fetchImpl);
    const { handleRequest } = await import('./background');

    expect(await handleRequest({ kind: 'pair', code: '123456' })).toEqual({ paired: true, sessions });
    expect(store.token).toBe('paired-tok');
    expect(JSON.parse(fetchImpl.mock.calls[0]![1]!.body as string)).toEqual({ code: '123456', name: 'Firefox' });
    expect(await handleRequest({ kind: 'unpair' })).toEqual({ paired: false, sessions: [] });
    expect(store.token).toBeUndefined();

    const respond = vi.fn();
    expect(onMessage({ kind: 'status' }, {}, respond)).toBe(true);
    await vi.waitFor(() => expect(respond).toHaveBeenCalledWith({ paired: false, sessions: [] }));
  });

  it('keeps the pairing and reports the error when the backend is down', async () => {
    store.token = 'paired-tok';
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new TypeError('offline'))));
    const { handleRequest } = await import('./background');

    const status = await handleRequest({ kind: 'status' });

    expect(status).toEqual({ paired: true, sessions: [], error: 'Desktop app not running. Open InterviewCopilot first.' });
    expect(store.token).toBe('paired-tok');
  });

  it('does not store a token when the code is rejected', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(null, { status: 400 }))));
    const { handleRequest } = await import('./background');

    const status = await handleRequest({ kind: 'pair', code: '000000' });

    expect(status).toEqual({ paired: false, sessions: [], error: 'That pairing code is invalid or expired.' });
    expect(store.token).toBeUndefined();
  });

  it('clears the token and asks to pair again when the backend answers 401', async () => {
    store.token = 'revoked-tok';
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(null, { status: 401 }))));
    const { handleRequest } = await import('./background');

    const status = await handleRequest({ kind: 'status' });

    expect(status).toEqual({ paired: false, sessions: [], error: 'Pair again with a new code from the desktop app.' });
    expect(store.token).toBeUndefined();
  });

  it('streams answers over the port, or explains when not paired', async () => {
    const fetchImpl = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(() => Promise.resolve(sse(['delta', 'Hi'], ['done', ''])));
    vi.stubGlobal('fetch', fetchImpl);
    const { handlePort } = await import('./background');
    const sent: AnswerPortMessage[] = [];
    let listener: (message: AnswerPortMessage) => void = () => undefined;
    const port = {
      name: ANSWER_PORT,
      postMessage: (m: AnswerPortMessage) => sent.push(m),
      onMessage: { addListener: (fn: typeof listener) => (listener = fn) },
    } as unknown as chrome.runtime.Port;

    handlePort({ name: 'other' } as chrome.runtime.Port);
    handlePort(port);
    listener({ kind: 'event', event: { type: 'done' } });
    listener({ kind: 'ask', sessionId: 's', question: 'Q?' });
    await vi.waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]).toEqual({ kind: 'event', event: { type: 'error', message: 'Pair the extension from its popup first.' } });

    store.token = 'paired-tok';
    listener({ kind: 'ask', sessionId: 's', question: 'Q?' });
    await vi.waitFor(() => expect(sent).toHaveLength(3));
    expect(sent.slice(1)).toEqual([
      { kind: 'event', event: { type: 'delta', token: 'Hi' } },
      { kind: 'event', event: { type: 'done' } },
    ]);
    expect(fetchImpl.mock.calls[0]![1]!.headers).toMatchObject({ 'X-Pairing-Token': 'paired-tok' });

    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('boom'))));
    listener({ kind: 'ask', sessionId: 's', question: 'Q?' });
    await vi.waitFor(() => expect(sent).toHaveLength(4));
    expect(sent[3]).toEqual({ kind: 'event', event: { type: 'error', message: 'Desktop app not running. Open InterviewCopilot first.' } });

    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(null, { status: 401 }))));
    listener({ kind: 'ask', sessionId: 's', question: 'Q?' });
    await vi.waitFor(() => expect(sent).toHaveLength(5));
    expect(sent[4]).toEqual({ kind: 'event', event: { type: 'error', message: 'Pair again with a new code from the desktop app.' } });
    expect(store.token).toBeUndefined();
  });
});
