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

describe('ApiClient', () => {
  it('signs in and sends the bearer token afterwards', async () => {
    const { api, fetchImpl } = fakeApi((url) => (url.endsWith('/sign-in') ? json({ accessToken: 'abc' }) : json([])));
    api.setToken(null);

    await api.signIn('alice', 'secret-value');
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
      'DELETE /api/sessions/s1/documents/d1',
      'GET /api/providers',
      'GET /api/storage',
      'GET /api/sessions/s1/transcript/pdf',
    ]);
    expect(api.transcriptPdfUrl('s1')).toBe('http://api.test/api/sessions/s1/transcript/pdf');
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
