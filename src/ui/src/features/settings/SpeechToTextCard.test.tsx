import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { createWebPlatform } from '@/platform';
import { fakeApi, json, renderWithApp } from '@/test/helpers';
import type { ProviderInfo } from '@/types/api';
import { SpeechToTextCard } from './SpeechToTextCard';
import { chooseOption, closeSelect, openSelect } from '@/test/select';

const whisper: ProviderInfo = { name: 'whisper', displayName: 'Whisper (local, free)', isLocal: true, requiresApiKey: false, isDefault: true };
const deepgram: ProviderInfo = { name: 'deepgram', displayName: 'Deepgram (cloud, paid)', isLocal: false, requiresApiKey: true, isDefault: false };
const unavailable = "That speech-to-text provider isn't available.";

function setup(saved: string | null = null, putResponse?: Response) {
  return fakeApi((url, init) => {
    if (init.method === 'PUT') return putResponse ?? json({ speechToTextProvider: JSON.parse(String(init.body)).provider });
    return json({ speechToTextProvider: saved });
  });
}

describe('SpeechToTextCard', () => {
  it('stays hidden when only Whisper is offered', async () => {
    const { api, fetchImpl } = setup();
    const { container } = renderWithApp(<SpeechToTextCard providers={[whisper]} />, { api });

    await waitFor(() => expect(fetchImpl).toHaveBeenCalled());
    expect(container).toBeEmptyDOMElement();
  });

  it('offers the providers, names the default, and shows no key field for Whisper', async () => {
    const { api } = setup();
    renderWithApp(<SpeechToTextCard providers={[whisper, deepgram]} />, { api });

    const options = await openSelect(userEvent, 'Speech-to-text provider');
    expect(await options.findByRole('option', { name: 'Default (Whisper (local, free))' })).toBeInTheDocument();
    expect(options.getByRole('option', { name: 'Deepgram (cloud, paid)' })).toBeInTheDocument();
    await closeSelect(userEvent);
    expect(screen.queryByLabelText('deepgram API key')).not.toBeInTheDocument();
  });

  it('saves the choice, and back to default with null', async () => {
    const { api, fetchImpl } = setup();
    renderWithApp(<SpeechToTextCard providers={[whisper, deepgram]} />, { api });

    await chooseOption(userEvent, 'Speech-to-text provider', 'Deepgram (cloud, paid)');
    expect(await screen.findByText('Saved.')).toBeInTheDocument();
    expect(await screen.findByLabelText('deepgram API key')).toBeInTheDocument();
    await chooseOption(userEvent, 'Speech-to-text provider', /^Default/);

    await waitFor(() => {
      const bodies = fetchImpl.mock.calls.filter((c) => c[1]?.method === 'PUT').map((c) => JSON.parse(String(c[1]!.body)));
      expect(bodies).toEqual([{ provider: 'deepgram' }, { provider: null }]);
    });
    expect(screen.queryByLabelText('deepgram API key')).not.toBeInTheDocument();
  });

  it('shows the key field when Deepgram is the saved choice', async () => {
    const { api } = setup('deepgram');
    renderWithApp(<SpeechToTextCard providers={[whisper, deepgram]} />, { api });

    expect(await screen.findByLabelText('deepgram API key')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByLabelText('Speech-to-text provider')).toHaveTextContent('Deepgram (cloud, paid)'));
  });

  it('shows the key field when Deepgram is the configured default', async () => {
    const { api } = setup();
    renderWithApp(<SpeechToTextCard providers={[{ ...whisper, isDefault: false }, { ...deepgram, isDefault: true }]} />, { api });

    expect(await screen.findByLabelText('deepgram API key')).toBeInTheDocument();
  });

  it('stores the key through the platform, reports it as saved, never shows it, and can remove it', async () => {
    const { api } = setup('deepgram');
    const platform = createWebPlatform();
    let stored: string | null = null;
    vi.spyOn(platform, 'getApiKey').mockImplementation(() => Promise.resolve(stored));
    const setKey = vi.spyOn(platform, 'setApiKey').mockImplementation((_p, key) => {
      stored = key;
      return Promise.resolve();
    });
    const deleteKey = vi.spyOn(platform, 'deleteApiKey').mockImplementation(() => {
      stored = null;
      return Promise.resolve();
    });
    renderWithApp(<SpeechToTextCard providers={[whisper, deepgram]} />, { api, platform });

    expect(await screen.findByText('No key saved yet.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Remove key' })).not.toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('deepgram API key'), 'typed-secret');
    await userEvent.click(screen.getByRole('button', { name: 'Save key' }));

    expect(await screen.findByText('A key is saved in your OS keychain.')).toBeInTheDocument();
    expect(setKey).toHaveBeenCalledWith('deepgram', 'typed-secret');
    expect(screen.getByLabelText('deepgram API key')).toHaveValue('');
    expect(screen.queryByDisplayValue('typed-secret')).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Remove key' }));
    expect(await screen.findByText('No key saved yet.')).toBeInTheDocument();
    expect(deleteKey).toHaveBeenCalledWith('deepgram');
  });

  it('explains when the key cannot be saved or removed', async () => {
    const { api } = setup('deepgram');
    const platform = createWebPlatform();
    vi.spyOn(platform, 'getApiKey').mockResolvedValue('present');
    vi.spyOn(platform, 'deleteApiKey').mockRejectedValue(new Error('Keychain locked.'));
    renderWithApp(<SpeechToTextCard providers={[whisper, deepgram]} />, { api, platform });

    await userEvent.type(await screen.findByLabelText('deepgram API key'), 'k');
    await userEvent.click(screen.getByRole('button', { name: 'Save key' }));
    expect(await screen.findByText('Saving API keys needs the desktop app.')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Remove key' }));
    expect(await screen.findByText('Keychain locked.')).toBeInTheDocument();
  });

  it('treats an unreadable keychain as no key', async () => {
    const { api } = setup('deepgram');
    const platform = createWebPlatform();
    vi.spyOn(platform, 'getApiKey').mockRejectedValue(new Error('locked'));
    renderWithApp(<SpeechToTextCard providers={[whisper, deepgram]} />, { api, platform });

    expect(await screen.findByText('No key saved yet.')).toBeInTheDocument();
  });

  it('shows the backend message when the choice cannot be saved or loaded', async () => {
    const { api } = setup(null, json({ detail: unavailable }, 400));
    renderWithApp(<SpeechToTextCard providers={[whisper, deepgram]} />, { api });

    await chooseOption(userEvent, 'Speech-to-text provider', 'Deepgram (cloud, paid)');
    expect(await screen.findByRole('alert')).toHaveTextContent(unavailable);

    const failing = fakeApi(() => json({ detail: 'Backend down' }, 500));
    renderWithApp(<SpeechToTextCard providers={[whisper, deepgram]} />, { api: failing.api });
    expect(await screen.findByText('Backend down')).toBeInTheDocument();
  });
});
