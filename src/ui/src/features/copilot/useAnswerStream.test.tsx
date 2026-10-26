import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { AppProvider } from '@/lib/app-context';
import { createWebPlatform } from '@/platform';
import { fakeApi, json, sse } from '@/test/helpers';
import { useAnswerStream } from './useAnswerStream';

function setup(prefs: unknown | Response) {
  const askKeys: (string | null)[] = [];
  const { api } = fakeApi((url, init) => {
    if (url.endsWith('/api/preferences')) return prefs instanceof Response ? prefs : json(prefs);
    askKeys.push(new Headers(init.headers).get('X-Llm-Api-Key'));
    return sse(['meta', '{"questionType":"Behavioral","version":1}'], ['delta', 'Hi'], ['done', '']);
  });
  const platform = createWebPlatform();
  const getApiKey = vi.spyOn(platform, 'getApiKey').mockImplementation((provider) => Promise.resolve(`key-${provider}`));
  const wrapper = ({ children }: { children: ReactNode }) => (
    <AppProvider api={api} platform={platform}>
      {children}
    </AppProvider>
  );
  return { wrapper, getApiKey, askKeys };
}

describe('useAnswerStream key header', () => {
  it('sends the key of the account chat provider', async () => {
    const { wrapper, getApiKey, askKeys } = setup({ llmProvider: 'openai', defaultLlmProvider: 'ollama' });
    const { result } = renderHook(() => useAnswerStream('s1'), { wrapper });

    await result.current.ask('Why?');

    await waitFor(() => expect(result.current.answer.status).toBe('done'));
    expect(getApiKey).toHaveBeenCalledWith('openai');
    expect(askKeys).toEqual(['key-openai']);
  });

  it('uses the configured default when no provider is chosen', async () => {
    const { wrapper, getApiKey } = setup({ llmProvider: null, defaultLlmProvider: 'google' });
    const { result } = renderHook(() => useAnswerStream('s1'), { wrapper });

    await result.current.ask('Why?');

    expect(getApiKey).toHaveBeenCalledWith('google');
  });

  it('falls back to Ollama when preferences cannot be read', async () => {
    const { wrapper, getApiKey } = setup(new Response(null, { status: 500 }));
    const { result } = renderHook(() => useAnswerStream('s1'), { wrapper });

    await result.current.ask('Why?');

    expect(getApiKey).toHaveBeenCalledWith('ollama');
  });
});
