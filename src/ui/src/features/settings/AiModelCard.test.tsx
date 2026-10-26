import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { createWebPlatform, type Platform } from '@/platform';
import { fakeApi, json, renderWithApp } from '@/test/helpers';
import type { Preferences } from '@/types/api';
import { AiModelCard } from './AiModelCard';

const llm = [
  { name: 'ollama', displayName: 'Ollama (local, free)', isLocal: true, requiresApiKey: false, isDefault: true },
  { name: 'anthropic', displayName: 'Anthropic (cloud, your key)', isLocal: false, requiresApiKey: true, isDefault: false },
  { name: 'openai', displayName: 'OpenAI (cloud, your key)', isLocal: false, requiresApiKey: true, isDefault: false },
];

const basePrefs: Preferences = {
  llmModel: null,
  defaultLlmModel: 'llama3.2:3b',
  llmProvider: null,
  defaultLlmProvider: 'ollama',
  speechToTextProvider: null,
  defaultSpeechToTextProvider: 'whisper',
};

const cloudModels = [
  { id: 'gpt-b', displayName: 'GPT B' },
  { id: 'gpt-a', displayName: 'GPT A' },
];

/** A desktop-like platform with an in-memory keychain. */
function desktop(initial: Record<string, string> = {}) {
  const keychain = new Map(Object.entries(initial));
  const platform: Platform = {
    ...createWebPlatform(),
    kind: 'desktop',
    getApiKey: vi.fn((provider: string) => Promise.resolve(keychain.get(provider) ?? null)),
    setApiKey: vi.fn((provider: string, key: string) => {
      keychain.set(provider, key);
      return Promise.resolve();
    }),
    deleteApiKey: vi.fn((provider: string) => {
      keychain.delete(provider);
      return Promise.resolve();
    }),
  };
  return { platform, keychain };
}

function setup(prefs: Partial<Preferences> = {}, modelsResponse?: () => Response) {
  const put: { url: string; body: unknown }[] = [];
  const modelCalls: { url: string; key: string | null }[] = [];
  const merged = { ...basePrefs, ...prefs };
  const { api } = fakeApi((url, init) => {
    if (url.endsWith('/api/providers')) return json({ speechToText: [], llm });
    if (url.endsWith('/api/providers/ollama/models')) return json([]);
    if (/\/api\/providers\/[a-z]+\/models$/.test(url)) {
      modelCalls.push({ url, key: new Headers(init.headers).get('X-Llm-Api-Key') });
      return modelsResponse ? modelsResponse() : json(cloudModels);
    }
    if (init.method === 'PUT') {
      const body = JSON.parse(String(init.body)) as { provider: string | null; model: string | null };
      put.push({ url, body });
      return json({ ...merged, llmProvider: body.provider, llmModel: body.model });
    }
    return json(merged);
  });
  return { api, put, modelCalls };
}

describe('AiModelCard', () => {
  it('shows the Ollama model picker for the default local provider', async () => {
    const { api } = setup();
    renderWithApp(<AiModelCard />, { api });

    expect(await screen.findByLabelText('Provider')).toHaveValue('ollama');
    expect(await screen.findByLabelText('AI model')).toBeInTheDocument();
    expect(screen.queryByLabelText('API key')).not.toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'OpenAI (cloud, your key)' })).toBeInTheDocument();
  });

  it('shows the cloud settings for a saved cloud provider and its saved model', async () => {
    const { api } = setup({ llmProvider: 'openai', llmModel: 'gpt-a' });
    renderWithApp(<AiModelCard />, { api, platform: desktop().platform });

    expect(await screen.findByLabelText('API key')).toBeInTheDocument();
    expect(screen.queryByLabelText('AI model')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Provider')).toHaveValue('openai');
    expect(screen.getByLabelText('Model')).toHaveValue('gpt-a');
  });

  it('saves and deletes the key through the keychain and never shows it', async () => {
    const { api } = setup({ llmProvider: 'openai' });
    const { platform, keychain } = desktop();
    renderWithApp(<AiModelCard />, { api, platform });
    const secret = `sk-${crypto.randomUUID()}`;

    expect(await screen.findByText('No key saved.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Load models' })).toBeDisabled();
    await userEvent.type(screen.getByLabelText('API key'), secret);
    await userEvent.click(screen.getByRole('button', { name: 'Save key' }));

    expect(await screen.findByText('A key is saved on this computer.')).toBeInTheDocument();
    expect(platform.setApiKey).toHaveBeenCalledWith('openai', secret);
    expect(keychain.get('openai')).toBe(secret);
    expect(screen.getByLabelText('API key')).toHaveValue('');
    expect(document.body).not.toHaveTextContent(secret);

    await userEvent.click(screen.getByRole('button', { name: 'Delete key' }));
    expect(await screen.findByText('No key saved.')).toBeInTheDocument();
    expect(platform.deleteApiKey).toHaveBeenCalledWith('openai');
    expect(keychain.has('openai')).toBe(false);
  });

  it('loads models with the saved key and saves the chosen provider and model', async () => {
    const { api, put, modelCalls } = setup({ llmProvider: 'openai' });
    const key = `sk-${crypto.randomUUID()}`;
    const { platform } = desktop({ openai: key });
    renderWithApp(<AiModelCard />, { api, platform });

    await waitFor(() => expect(screen.getByRole('button', { name: 'Load models' })).toBeEnabled());
    await userEvent.click(screen.getByRole('button', { name: 'Load models' }));
    await userEvent.selectOptions(await screen.findByRole('combobox', { name: 'Model' }), 'gpt-b');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Saved.')).toBeInTheDocument();
    expect(modelCalls).toEqual([{ url: 'http://api.test/api/providers/openai/models', key }]);
    expect(put).toEqual([{ url: 'http://api.test/api/preferences/llm', body: { provider: 'openai', model: 'gpt-b' } }]);
  });

  it('keeps Save disabled until a model is chosen', async () => {
    const { api } = setup();
    renderWithApp(<AiModelCard />, { api, platform: desktop().platform });

    await userEvent.selectOptions(await screen.findByLabelText('Provider'), 'anthropic');

    expect(await screen.findByLabelText('API key')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('switches back to Ollama and saves that choice right away', async () => {
    const { api, put } = setup({ llmProvider: 'openai', llmModel: 'gpt-a' });
    renderWithApp(<AiModelCard />, { api, platform: desktop().platform });

    await userEvent.selectOptions(await screen.findByLabelText('Provider'), 'ollama');

    expect(await screen.findByLabelText('AI model')).toBeInTheDocument();
    expect(put).toEqual([{ url: 'http://api.test/api/preferences/llm', body: { provider: 'ollama', model: null } }]);
  });

  it('shows the actionable message when the provider rejects the key', async () => {
    const { api } = setup({ llmProvider: 'anthropic' }, () => json({ detail: 'Anthropic rejected the API key.' }, 401));
    const signedOut = vi.fn();
    api.setAuthHandlers({ refresh: () => Promise.resolve('fresh'), signedOut });
    renderWithApp(<AiModelCard />, { api, platform: desktop({ anthropic: 'bad' }).platform });

    await waitFor(() => expect(screen.getByRole('button', { name: 'Load models' })).toBeEnabled());
    await userEvent.click(screen.getByRole('button', { name: 'Load models' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Anthropic rejected the API key.');
    expect(signedOut).not.toHaveBeenCalled();
  });

  it('explains that cloud keys need the desktop app on the web platform', async () => {
    const { api } = setup({ llmProvider: 'openai' });
    renderWithApp(<AiModelCard />, { api });

    expect(await screen.findByText(/need the desktop app/)).toBeInTheDocument();
    expect(screen.getByLabelText('API key')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Save key' })).toBeDisabled();
  });

  it('reports failures to save the key, delete it or save the choice', async () => {
    const { api } = setup({ llmProvider: 'openai', llmModel: 'gpt-a' });
    const { platform } = desktop({ openai: 'k' });
    vi.mocked(platform.setApiKey).mockRejectedValueOnce(new Error('keychain locked'));
    vi.mocked(platform.deleteApiKey).mockRejectedValueOnce(new Error('cannot delete'));
    renderWithApp(<AiModelCard />, { api, platform });

    await userEvent.type(await screen.findByLabelText('API key'), 'new');
    await userEvent.click(screen.getByRole('button', { name: 'Save key' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('keychain locked');

    await userEvent.click(screen.getByRole('button', { name: 'Delete key' }));
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('cannot delete'));
  });

  it('asks for a key when it vanished before loading models', async () => {
    const { api } = setup({ llmProvider: 'openai' });
    const { platform, keychain } = desktop({ openai: 'k' });
    renderWithApp(<AiModelCard />, { api, platform });
    await waitFor(() => expect(screen.getByRole('button', { name: 'Load models' })).toBeEnabled());

    keychain.delete('openai');
    await userEvent.click(screen.getByRole('button', { name: 'Load models' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Add your OpenAI API key first.');
  });

  it('shows an error when the providers cannot be loaded', async () => {
    const { api } = fakeApi(() => json({ detail: 'down' }, 503));
    renderWithApp(<AiModelCard />, { api });

    expect(await screen.findByRole('alert')).toHaveTextContent('down');
  });
});
