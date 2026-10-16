import { act, fireEvent, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SpeechModelBanner } from '@/features/sessions/SpeechModelBanner';
import { fakeApi, json, renderWithApp } from '@/test/helpers';
import type { WhisperModel } from '@/types/api';
import { SpeechModelCard } from './SpeechModelCard';

const base: WhisperModel = { state: 'missing', model: 'ggml-small.en.bin', bytesDownloaded: 0, totalBytes: null, message: null };

/** Fake backend whose model state tests can change between polls. */
function backend(initial: WhisperModel) {
  const state = { model: initial, getCalls: 0, postCalls: 0, postResponse: null as Response | null };
  const handler = (url: string, init: RequestInit) => {
    if (url.endsWith('/download') && init.method === 'POST') {
      state.postCalls++;
      return state.postResponse ?? json((state.model = { ...base, state: 'downloading' }), 202);
    }
    state.getCalls++;
    return json(state.model);
  };
  return { state, ...fakeApi(handler) };
}

async function tick(ms: number) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

describe.each([
  ['SpeechModelCard', SpeechModelCard],
  ['SpeechModelBanner', SpeechModelBanner],
])('%s', (_, Component) => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('does not download until the user clicks, then polls every second until ready', async () => {
    const { api, state } = backend(base);
    renderWithApp(<Component />, { api });
    await tick(0);

    expect(screen.getByText(/about 466 MB/)).toBeInTheDocument();
    await tick(3000);
    expect(state.postCalls).toBe(0);
    expect(state.getCalls).toBe(1);

    fireEvent.click(screen.getByRole('button', { name: 'Download' }));
    await tick(0);
    expect(state.postCalls).toBe(1);
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '0');

    state.model = { ...base, state: 'downloading', bytesDownloaded: 233 * 1024 * 1024, totalBytes: 466 * 1024 * 1024 };
    await tick(1000);
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '50');
    expect(screen.getByText(/233 MB downloaded/)).toBeInTheDocument();

    state.model = { ...base, state: 'ready' };
    await tick(1000);
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
    const callsWhenReady = state.getCalls;
    await tick(3000);
    expect(state.getCalls).toBe(callsWhenReady);
  });

  it('estimates progress from the expected size when the total is unknown', async () => {
    const { api } = backend({ ...base, state: 'downloading', bytesDownloaded: 466 * 1024 * 1024 * 2, totalBytes: null });
    renderWithApp(<Component />, { api });
    await tick(0);

    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '100');
  });

  it('shows the failure and retries', async () => {
    const { api, state } = backend({ ...base, state: 'failed', message: 'The download failed: network down' });
    renderWithApp(<Component />, { api });
    await tick(0);

    expect(screen.getByText('The download failed: network down')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await tick(0);

    expect(state.postCalls).toBe(1);
    expect(screen.getByRole('progressbar')).toBeInTheDocument();
  });

  it('refreshes instead of failing when a download is already running (409)', async () => {
    const { api, state } = backend(base);
    state.postResponse = json({ detail: 'busy' }, 409);
    renderWithApp(<Component />, { api });
    await tick(0);

    state.model = { ...base, state: 'downloading', bytesDownloaded: 1024 * 1024 };
    fireEvent.click(screen.getByRole('button', { name: 'Download' }));
    await tick(0);

    expect(screen.getByRole('progressbar')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});

describe('SpeechModelCard states', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('says when the model is ready', async () => {
    const { api } = backend({ ...base, state: 'ready' });
    renderWithApp(<SpeechModelCard />, { api });
    await tick(0);

    expect(screen.getByText(/Speech is transcribed on this computer/)).toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('shows load and start errors', async () => {
    const failing = fakeApi(() => json({ detail: 'Backend broke' }, 500));
    renderWithApp(<SpeechModelCard />, { api: failing.api });
    await tick(0);
    expect(screen.getByRole('alert')).toHaveTextContent("Couldn't check the speech model.");

    const { api, state } = backend(base);
    state.postResponse = json({ detail: 'Disk full' }, 500);
    renderWithApp(<SpeechModelCard />, { api });
    await tick(0);
    fireEvent.click(screen.getByRole('button', { name: 'Download' }));
    await tick(0);
    expect(screen.getAllByRole('alert').some((a) => a.textContent === 'Disk full')).toBe(true);
  });
});

describe('SpeechModelBanner states', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('renders nothing when the model is ready', async () => {
    const { api } = backend({ ...base, state: 'ready' });
    renderWithApp(<SpeechModelBanner />, { api });
    await tick(0);

    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  it('renders nothing when the state cannot be loaded', async () => {
    const { api } = fakeApi(() => json({ detail: 'nope' }, 500));
    renderWithApp(<SpeechModelBanner />, { api });
    await tick(0);

    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });
});
