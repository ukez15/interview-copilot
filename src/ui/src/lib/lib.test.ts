import { describe, expect, it, vi } from 'vitest';
import { fakeApi, json, sse } from '@/test/helpers';
import { ApiClient, ApiError } from './api';
import { SseParser } from './sse';
import { clampOpacity, cn, OVERLAY_OPACITY } from './utils';

describe('SseParser', () => {
  it('parses complete events and keeps partial ones for the next chunk', () => {
    const parser = new SseParser();

    expect(parser.push('event: delta\ndata:  Hello\n\nevent: del')).toEqual([{ event: 'delta', data: ' Hello' }]);
    expect(parser.push('ta\ndata: world\r\n\r\n')).toEqual([{ event: 'delta', data: 'world' }]);
  });

  it('joins multi-line data, defaults the event name and skips empty blocks', () => {
    const parser = new SseParser();

    expect(parser.push('data: a\ndata: b\n\n\n\n: comment\n\n')).toEqual([{ event: 'message', data: 'a\nb' }]);
  });

  it('keeps events without data', () => {
    expect(new SseParser().push('event: done\n\n')).toEqual([{ event: 'done', data: '' }]);
  });
});

describe('utils', () => {
  it('clamps opacity to the readable range', () => {
    expect(clampOpacity(0)).toBe(OVERLAY_OPACITY.min);
    expect(clampOpacity(2)).toBe(1);
    expect(clampOpacity(0.5)).toBe(0.5);
    expect(clampOpacity(Number.NaN)).toBe(0.85);
  });

  it('merges class names', () => {
    expect(cn('p-2', false, 'p-4')).toBe('p-4');
  });
});

describe('ApiClient token refresh', () => {
  const sessionsHandler = (accepted: string) => (url: string, init: RequestInit) =>
    url.endsWith('/api/sessions') && new Headers(init.headers).get('Authorization') === `Bearer ${accepted}`
      ? json([])
      : new Response(null, { status: 401 });

  it('refreshes once on a 401 and retries with the new token', async () => {
    const { api, fetchImpl } = fakeApi(sessionsHandler('fresh'));
    const refresh = vi.fn().mockResolvedValue('fresh');
    const signedOut = vi.fn();
    api.setAuthHandlers({ refresh, signedOut });

    await expect(api.listSessions()).resolves.toEqual([]);

    expect(refresh).toHaveBeenCalledTimes(1);
    expect(signedOut).not.toHaveBeenCalled();
    expect(fetchImpl).toHaveBeenCalledTimes(2);
  });

  it('signs out when the refresh fails', async () => {
    const { api } = fakeApi(sessionsHandler('fresh'));
    const signedOut = vi.fn();
    api.setAuthHandlers({ refresh: () => Promise.resolve(null), signedOut });

    await expect(api.listSessions()).rejects.toMatchObject({ status: 401 });
    expect(signedOut).toHaveBeenCalledTimes(1);
  });

  it('signs out when the refresh throws or the retry is still unauthorized', async () => {
    const throwing = fakeApi(sessionsHandler('fresh'));
    const signedOut = vi.fn();
    throwing.api.setAuthHandlers({ refresh: () => Promise.reject(new Error('x')), signedOut });
    await expect(throwing.api.listSessions()).rejects.toBeInstanceOf(ApiError);

    const stillBad = fakeApi(sessionsHandler('never'));
    stillBad.api.setAuthHandlers({ refresh: () => Promise.resolve('fresh'), signedOut });
    await expect(stillBad.api.listSessions()).rejects.toBeInstanceOf(ApiError);

    expect(signedOut).toHaveBeenCalledTimes(2);
  });

  it('shares one refresh between concurrent requests', async () => {
    const { api } = fakeApi(sessionsHandler('fresh'));
    const refresh = vi.fn().mockResolvedValue('fresh');
    api.setAuthHandlers({ refresh, signedOut: vi.fn() });

    await Promise.all([api.listSessions(), api.listSessions()]);

    expect(refresh).toHaveBeenCalledTimes(1);
  });

  it('never tries to refresh a failed sign-in', async () => {
    const { api } = fakeApi(() => new Response(null, { status: 401 }));
    const refresh = vi.fn();
    api.setAuthHandlers({ refresh, signedOut: vi.fn() });

    await expect(api.signIn('alice', 'wrong-password')).rejects.toBeInstanceOf(ApiError);
    expect(refresh).not.toHaveBeenCalled();
  });
});

describe('ApiClient', () => {
  it('signs in and sends the bearer token afterwards', async () => {
    const { api, fetchImpl } = fakeApi((url) => (url.endsWith('/sign-in') ? json({ accessToken: 'abc', refreshToken: 'ref' }) : json([])));
    api.setToken(null);

    expect(await api.signIn('alice', 'secret-value')).toEqual({ accessToken: 'abc', refreshToken: 'ref' });
    await api.listSessions();

    const headers = new Headers(fetchImpl.mock.calls[1]![1]!.headers);
    expect(headers.get('Authorization')).toBe('Bearer abc');
  });

  it('calls every session endpoint with the right method', async () => {
    const { api, fetchImpl } = fakeApi(() => json({}));

    await api.register('alice', 'secret-value');
    await api.getSession('s1');
    await api.createSession({ title: 't', mode: 'LiveInterview', answerLength: 'Short', framework: 'None', pinnedQuestionType: null, sessionPrompt: '' });
    await api.updateSessionSettings('s1', { answerLength: 'Long', framework: 'Star', pinnedQuestionType: null, sessionPrompt: '' });
    await api.getTranscript('s1');
    await api.startSession('s1');
    await api.endSession('s1');
    await api.uploadDocument('s1', new File(['x'], 'a.txt'));
    await api.previewJobPosting('s1', 'https://jobs.test/1');
    await api.previewJobPostingLink('https://jobs.test/1');
    await api.removeDocument('s1', 'd1');
    await api.providers();
    await api.getStorage();
    await api.downloadTranscriptPdf('s1');

    const calls = fetchImpl.mock.calls.map(([url, init]) => `${init?.method ?? 'GET'} ${String(url).replace('http://api.test', '')}`);
    expect(calls).toEqual([
      'POST /api/auth/register',
      'GET /api/sessions/s1',
      'POST /api/sessions',
      'PUT /api/sessions/s1/settings',
      'GET /api/sessions/s1/transcript',
      'POST /api/sessions/s1/start',
      'POST /api/sessions/s1/end',
      'POST /api/sessions/s1/documents',
      'POST /api/sessions/s1/documents/from-link/preview',
      'POST /api/documents/from-link/preview',
      'DELETE /api/sessions/s1/documents/d1',
      'GET /api/providers',
      'GET /api/storage',
      'GET /api/sessions/s1/transcript/pdf',
    ]);
    expect(api.transcriptPdfUrl('s1')).toBe('http://api.test/api/sessions/s1/transcript/pdf');
  });

  it('sends copyFromSessionId when creating a session from another (#16)', async () => {
    const { api, fetchImpl } = fakeApi(() => json({}));

    await api.createSession({ title: 'Zoom', mode: 'LiveInterview', answerLength: 'Short', framework: 'None', pinnedQuestionType: null, sessionPrompt: '', copyFromSessionId: 's0' });

    expect(fetchImpl.mock.calls[0]![0]).toBe('http://api.test/api/sessions');
    expect(JSON.parse(String(fetchImpl.mock.calls[0]![1]!.body))).toMatchObject({ title: 'Zoom', copyFromSessionId: 's0' });
  });

  it('calls the wave-3 provider and session-key endpoints', async () => {
    const { api, fetchImpl } = fakeApi(() => json({}));

    await api.setLlmChoice('openai', 'gpt-test');
    await api.setSpeechToTextProvider(null);
    await api.setSessionKey('s1', 'openai', 'sk-test');

    const calls = fetchImpl.mock.calls.map(([url, init]) => [`${init?.method} ${String(url).replace('http://api.test', '')}`, init?.body]);
    expect(calls).toEqual([
      ['PUT /api/preferences/llm', JSON.stringify({ provider: 'openai', model: 'gpt-test' })],
      ['PUT /api/preferences/speech-to-text', JSON.stringify({ provider: null })],
      ['PUT /api/sessions/s1/keys/openai', JSON.stringify({ key: 'sk-test' })],
    ]);
  });

  it('streams answer events to the handlers and sends the BYOK key header', async () => {
    const { api, fetchImpl } = fakeApi(() =>
      sse(['meta', '{"questionType":"Behavioral","version":1}'], ['delta', 'Hi'], ['delta', ' there'], ['error', 'oops'], ['done', '']),
    );
    const handlers = { onHeader: vi.fn(), onToken: vi.fn(), onError: vi.fn(), onDone: vi.fn() };

    await api.ask('s1', { question: 'Why?', llmApiKey: 'byok' }, handlers);

    expect(handlers.onHeader).toHaveBeenCalledWith({ questionType: 'Behavioral', version: 1 });
    expect(handlers.onToken.mock.calls.map(([t]) => t)).toEqual(['Hi', ' there']);
    expect(handlers.onError).toHaveBeenCalledWith('oops');
    expect(handlers.onDone).toHaveBeenCalled();
    expect(new Headers(fetchImpl.mock.calls[0]![1]!.headers).get('X-Llm-Api-Key')).toBe('byok');
  });

  it('fails when the stream has no body', async () => {
    const { api } = fakeApi(() => new Response(null, { status: 200 }));

    await expect(api.ask('s1', { question: 'q' }, { onHeader: vi.fn(), onToken: vi.fn(), onError: vi.fn(), onDone: vi.fn() })).rejects.toThrow(
      'empty',
    );
  });

  it.each([
    [json({ detail: 'At most 10 documents.' }, 400), 'At most 10 documents.'],
    [json({ title: 'Not found' }, 404), 'Not found'],
    [json({}, 500), 'Request failed (500).'],
    [new Response('not json', { status: 502 }), 'Request failed (502).'],
    [new Response(null, { status: 401 }), 'Please sign in again.'],
  ])('turns error responses into readable messages', async (response, message) => {
    const { api } = fakeApi(() => response);

    await expect(api.listSessions()).rejects.toThrow(message);
  });

  it('explains when the backend is not running', async () => {
    const api = new ApiClient('http://api.test', () => Promise.reject(new TypeError('Failed to fetch')));

    const error = await api.listSessions().catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(0);
  });
});
