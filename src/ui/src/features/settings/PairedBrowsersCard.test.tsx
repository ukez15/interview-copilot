import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeApi, json, renderWithApp } from '@/test/helpers';
import type { PairedBrowser } from '@/types/api';
import { PairedBrowsersCard } from './PairedBrowsersCard';

const NOW = new Date('2026-10-16T10:00:00Z');
const CODE = { code: '482913', expiresAt: '2026-10-16T10:05:00Z' };
const CHROME: PairedBrowser = { id: 'b1', name: 'Chrome', createdAt: '2026-10-15T09:00:00Z', lastUsedAt: '2026-10-16T09:30:00Z' };
const EDGE: PairedBrowser = { id: 'b2', name: 'Edge', createdAt: '2026-10-16T08:00:00Z', lastUsedAt: null };

interface Failures {
  list?: boolean;
  code?: boolean;
  revoke?: boolean;
}

/** Fake backend that keeps the list of paired browsers. */
function backend(initial: PairedBrowser[], failures: Failures = {}) {
  let browsers = [...initial];
  return fakeApi((url, init) => {
    if (url.endsWith('/api/pairing/codes')) {
      return failures.code ? json({ detail: 'boom' }, 500) : json(CODE);
    }
    if (init.method === 'DELETE') {
      if (failures.revoke) {
        return json({ detail: 'boom' }, 500);
      }
      browsers = browsers.filter((b) => !url.endsWith(`/${b.id}`));
      return new Response(null, { status: 204 });
    }
    return failures.list ? json({ detail: 'boom' }, 500) : json(browsers);
  });
}

describe('PairedBrowsersCard', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'], now: NOW });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  const user = () => userEvent.setup({ advanceTimers: (ms) => vi.advanceTimersByTime(ms) });

  it('lists paired browsers with their paired and last used dates', async () => {
    const { api, fetchImpl } = backend([CHROME, EDGE]);
    renderWithApp(<PairedBrowsersCard />, { api });

    expect(await screen.findByText('Chrome')).toBeInTheDocument();
    expect(screen.getByText('Edge')).toBeInTheDocument();
    expect(screen.getByText(/Last used: never/i)).toBeInTheDocument();
    expect(screen.getAllByText(/Last used/)).toHaveLength(2);
    expect(fetchImpl.mock.calls[0]?.[0]).toBe('http://api.test/api/pairing/browsers');
  });

  it('says so when no browser is paired', async () => {
    const { api } = backend([]);
    renderWithApp(<PairedBrowsersCard />, { api });

    expect(await screen.findByText(/No browsers paired yet/i)).toBeInTheDocument();
  });

  it('shows the code with a live countdown, then says it expired', async () => {
    const { api } = backend([]);
    renderWithApp(<PairedBrowsersCard />, { api });

    await user().click(await screen.findByRole('button', { name: 'Pair a browser' }));

    expect(await screen.findByText(CODE.code)).toBeInTheDocument();
    expect(screen.getByText(/Expires in 5:00/)).toBeInTheDocument();

    act(() => {
      vi.advanceTimersByTime(61_000);
    });
    expect(screen.getByText(/Expires in 3:59/)).toBeInTheDocument();

    act(() => {
      vi.advanceTimersByTime(300_000);
    });
    expect(screen.queryByText(CODE.code)).not.toBeInTheDocument();
    expect(screen.getByText(/code expired/i)).toBeInTheDocument();
  });

  it('hides the code once the new browser shows up in the list', async () => {
    let browsers: PairedBrowser[] = [];
    const { api } = fakeApi((url) => (url.endsWith('/codes') ? json(CODE) : json(browsers)));
    renderWithApp(<PairedBrowsersCard />, { api });
    await user().click(await screen.findByRole('button', { name: 'Pair a browser' }));
    expect(await screen.findByText(CODE.code)).toBeInTheDocument();

    browsers = [CHROME];
    await act(async () => {
      await vi.advanceTimersByTimeAsync(3_000);
    });

    await waitFor(() => expect(screen.queryByText(CODE.code)).not.toBeInTheDocument());
    expect(screen.getByText('Chrome')).toBeInTheDocument();
  });

  it('revokes a browser and removes it from the list', async () => {
    const { api, fetchImpl } = backend([CHROME, EDGE]);
    renderWithApp(<PairedBrowsersCard />, { api });

    await user().click(await screen.findByRole('button', { name: 'Revoke Chrome' }));

    await waitFor(() => expect(screen.queryByText('Chrome')).not.toBeInTheDocument());
    expect(screen.getByText('Edge')).toBeInTheDocument();
    const call = fetchImpl.mock.calls.find(([, init]) => init?.method === 'DELETE');
    expect(call?.[0]).toBe('http://api.test/api/pairing/browsers/b1');
  });

  it.each<[string, Failures, string | undefined, string]>([
    ['load the list', { list: true }, undefined, "Couldn't load paired browsers."],
    ['create a code', { code: true }, 'Pair a browser', "Couldn't create a pairing code."],
    ['revoke', { revoke: true }, 'Revoke Chrome', "Couldn't revoke that browser."],
  ])('shows a readable error when it cannot %s', async (_label, failures, button, message) => {
    const { api } = backend(failures.list ? [] : [CHROME], failures);
    renderWithApp(<PairedBrowsersCard />, { api });

    if (button) {
      await user().click(await screen.findByRole('button', { name: button }));
    }

    expect(await screen.findByRole('alert')).toHaveTextContent(message);
  });
});
