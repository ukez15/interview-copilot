import { describe, expect, it, vi } from 'vitest';
import { END_ON_QUIT_TIMEOUT_MS, endLiveSession } from './end-on-quit';

const SESSION = '3f2b8c1e-0d4a-4c8e-9f1a-2b3c4d5e6f70';

describe('endLiveSession (#84)', () => {
  it('ends the live session with the signed-in token, like clicking End session', async () => {
    const fetchImpl = vi.fn(() => Promise.resolve(new Response('{}', { status: 200 })));

    await endLiveSession({ sessionId: SESSION, token: 'tok', backendUrl: 'http://api.test', fetchImpl: fetchImpl as unknown as typeof fetch });

    expect(fetchImpl).toHaveBeenCalledTimes(1);
    const [url, init] = fetchImpl.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe(`http://api.test/api/sessions/${SESSION}/end`);
    expect(init.method).toBe('POST');
    expect(new Headers(init.headers).get('Authorization')).toBe('Bearer tok');
    expect(init.signal).toBeInstanceOf(AbortSignal);
  });

  it.each([
    [null, 'tok'],
    [SESSION, null],
  ])('does nothing without a live session or a sign-in (%s, %s)', async (sessionId, token) => {
    const fetchImpl = vi.fn();

    await endLiveSession({ sessionId, token, backendUrl: 'http://api.test', fetchImpl: fetchImpl as unknown as typeof fetch });

    expect(fetchImpl).not.toHaveBeenCalled();
  });

  it('never stops the app from quitting: errors and refusals are swallowed', async () => {
    const failing = vi.fn(() => Promise.reject(new Error('backend down')));
    const refused = vi.fn(() => Promise.resolve(new Response('{}', { status: 409 })));

    await expect(endLiveSession({ sessionId: SESSION, token: 'tok', backendUrl: 'http://api.test', fetchImpl: failing as unknown as typeof fetch })).resolves.toBeUndefined();
    await expect(endLiveSession({ sessionId: SESSION, token: 'tok', backendUrl: 'http://api.test', fetchImpl: refused as unknown as typeof fetch })).resolves.toBeUndefined();
  });

  it('gives up after the timeout when the backend does not answer', async () => {
    vi.useFakeTimers();
    try {
      const hanging = vi.fn((_url: string, init: RequestInit) => new Promise<Response>((_resolve, reject) => {
        init.signal?.addEventListener('abort', () => reject(new Error('aborted')));
      }));

      const ending = endLiveSession({ sessionId: SESSION, token: 'tok', backendUrl: 'http://api.test', fetchImpl: hanging as unknown as typeof fetch });
      await vi.advanceTimersByTimeAsync(END_ON_QUIT_TIMEOUT_MS);

      await expect(ending).resolves.toBeUndefined();
    } finally {
      vi.useRealTimers();
    }
  });
});
