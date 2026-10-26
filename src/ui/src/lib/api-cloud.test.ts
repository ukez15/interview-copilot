import { describe, expect, it, vi } from 'vitest';
import { fakeApi, json } from '@/test/helpers';

describe('ApiClient cloud models', () => {
  it('sends the key in the X-Llm-Api-Key header, not the URL', async () => {
    const { api, fetchImpl } = fakeApi(() => json([{ id: 'm1', displayName: 'M1' }]));
    const key = `sk-${crypto.randomUUID()}`;

    const models = await api.cloudModels('open ai', key);

    expect(models).toEqual([{ id: 'm1', displayName: 'M1' }]);
    const [url, init] = fetchImpl.mock.calls[0]!;
    expect(String(url)).toBe('http://api.test/api/providers/open%20ai/models');
    expect(String(url)).not.toContain(key);
    expect(new Headers(init!.headers).get('X-Llm-Api-Key')).toBe(key);
  });

  it('keeps a self-explaining 401 as an error message instead of signing the user out', async () => {
    const { api, fetchImpl } = fakeApi(() => json({ detail: 'OpenAI rejected the API key.' }, 401));
    const signedOut = vi.fn();
    const refresh = vi.fn().mockResolvedValue('fresh');
    api.setAuthHandlers({ refresh, signedOut });

    await expect(api.cloudModels('openai', 'k')).rejects.toMatchObject({ status: 401, message: 'OpenAI rejected the API key.' });
    expect(refresh).not.toHaveBeenCalled();
    expect(signedOut).not.toHaveBeenCalled();
    expect(fetchImpl).toHaveBeenCalledTimes(1);
  });
});
