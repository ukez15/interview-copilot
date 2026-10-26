import { describe, expect, it, vi } from 'vitest';
import { createWebPlatform } from '@/platform';
import { fakeApi, json } from '@/test/helpers';
import type { Preferences } from '@/types/api';
import { startLiveSession } from './start-live';

const prefs: Preferences = {
  llmModel: null,
  defaultLlmModel: 'llama3.2:3b',
  llmProvider: null,
  defaultLlmProvider: 'ollama',
  speechToTextProvider: null,
  defaultSpeechToTextProvider: 'whisper',
};

function setup(preferences: Partial<Preferences> | Response = {}) {
  const calls: { url: string; method: string; body: string }[] = [];
  const { api, fetchImpl } = fakeApi((url, init) => {
    const method = init.method ?? 'GET';
    calls.push({ url, method, body: String(init.body ?? '') });
    if (url.endsWith('/api/preferences')) return preferences instanceof Response ? preferences : json({ ...prefs, ...preferences });
    if (url.includes('/keys/')) return new Response(null, { status: 204 });
    return json({ id: 's1', status: 'Live' });
  });
  const platform = createWebPlatform();
  const capture = vi.spyOn(platform, 'startCapture').mockResolvedValue({ state: 'live', interviewer: true, mic: true, message: null });
  const keysSent = () => calls.filter((c) => c.url.includes('/keys/'));
  return { api, fetchImpl, platform, capture, calls, keysSent };
}

describe('startLiveSession', () => {
  it('takes the session live, then starts capture', async () => {
    const { api, platform, capture } = setup();
    const order: string[] = [];
    const startSession = vi.spyOn(api, 'startSession').mockImplementation(async (id) => {
      order.push(`start ${id}`);
      return { id, status: 'Live' } as Awaited<ReturnType<typeof api.startSession>>;
    });
    capture.mockImplementation(async (id) => {
      order.push(`capture ${id}`);
      return { state: 'live', interviewer: true, mic: true, message: null };
    });

    const started = await startLiveSession(api, platform, 's1');

    expect(started.status).toBe('Live');
    expect(startSession).toHaveBeenCalledWith('s1');
    expect(order).toEqual(['start s1', 'capture s1']);
  });

  it('does not start capture when going live fails', async () => {
    const { api } = fakeApi(() => new Response(null, { status: 409 }));
    const platform = createWebPlatform();
    const capture = vi.spyOn(platform, 'startCapture');

    await expect(startLiveSession(api, platform, 's1')).rejects.toThrow();
    expect(capture).not.toHaveBeenCalled();
  });

  it('hands the chosen cloud provider key to the session before capture starts', async () => {
    const { api, platform, capture, keysSent, calls } = setup({ llmProvider: 'openai' });
    const key = `sk-${crypto.randomUUID()}`;
    vi.spyOn(platform, 'getApiKey').mockImplementation((provider) => Promise.resolve(provider === 'openai' ? key : null));
    capture.mockImplementation(async () => {
      expect(keysSent()).toHaveLength(1);
      return { state: 'live', interviewer: true, mic: true, message: null };
    });

    await startLiveSession(api, platform, 's1');

    expect(keysSent()).toEqual([{ url: 'http://api.test/api/sessions/s1/keys/openai', method: 'PUT', body: JSON.stringify({ key }) }]);
    expect(calls.findIndex((c) => c.url.endsWith('/start'))).toBeLessThan(calls.findIndex((c) => c.url.includes('/keys/')));
    expect(capture).toHaveBeenCalledWith('s1');
  });

  it('hands over both the chat and speech-to-text keys when both are cloud providers', async () => {
    const { api, platform, keysSent } = setup({ llmProvider: 'anthropic', speechToTextProvider: 'deepgram' });
    vi.spyOn(platform, 'getApiKey').mockImplementation((provider) => Promise.resolve(`k-${provider}`));

    await startLiveSession(api, platform, 's1');

    expect(keysSent().map((c) => c.url.split('/keys/')[1])).toEqual(['anthropic', 'deepgram']);
  });

  it('uses the configured defaults when nothing is chosen, and skips local providers', async () => {
    const { api, platform, keysSent } = setup({ defaultLlmProvider: 'google', defaultSpeechToTextProvider: 'whisper' });
    const getApiKey = vi.spyOn(platform, 'getApiKey').mockResolvedValue('k');

    await startLiveSession(api, platform, 's1');

    expect(getApiKey).toHaveBeenCalledTimes(1);
    expect(getApiKey).toHaveBeenCalledWith('google');
    expect(keysSent()).toHaveLength(1);
  });

  it('sends nothing for local providers or when no key is saved', async () => {
    const local = setup();
    const getApiKey = vi.spyOn(local.platform, 'getApiKey');
    await startLiveSession(local.api, local.platform, 's1');
    expect(getApiKey).not.toHaveBeenCalled();
    expect(local.keysSent()).toHaveLength(0);

    const noKey = setup({ llmProvider: 'openai' });
    await startLiveSession(noKey.api, noKey.platform, 's1');
    expect(noKey.keysSent()).toHaveLength(0);
    expect(noKey.capture).toHaveBeenCalled();
  });

  it('still captures when the key hand-off fails', async () => {
    const failing = setup({ llmProvider: 'openai', speechToTextProvider: 'deepgram' });
    vi.spyOn(failing.platform, 'getApiKey').mockImplementation((provider) =>
      provider === 'openai' ? Promise.reject(new Error('keychain locked')) : Promise.resolve('k'),
    );
    vi.spyOn(failing.api, 'setSessionKey').mockRejectedValue(new Error('boom'));

    await expect(startLiveSession(failing.api, failing.platform, 's1')).resolves.toMatchObject({ status: 'Live' });
    expect(failing.capture).toHaveBeenCalledWith('s1');
    expect(failing.api.setSessionKey).toHaveBeenCalledWith('s1', 'deepgram', 'k');
  });

  it('still captures when preferences cannot be read', async () => {
    const { api, platform, capture, keysSent } = setup(new Response(null, { status: 500 }));

    await expect(startLiveSession(api, platform, 's1')).resolves.toMatchObject({ status: 'Live' });
    expect(capture).toHaveBeenCalled();
    expect(keysSent()).toHaveLength(0);
  });
});
