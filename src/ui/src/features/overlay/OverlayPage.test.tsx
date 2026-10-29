import { screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { fakeApi, json, renderWithApp, sampleSession } from '@/test/helpers';
import { OverlayPage } from './OverlayPage';
import { useAutoScroll } from './useAutoScroll';

vi.mock('./useAutoScroll', () => ({ useAutoScroll: vi.fn() }));

function lastScrollMode() {
  return vi.mocked(useAutoScroll).mock.lastCall?.[1];
}

describe('OverlayPage', () => {
  it('follows new text in a mock interview but lets the user scroll up', async () => {
    const { api } = fakeApi((url) =>
      url.endsWith('/practice/attempts') ? json([]) : json({ ...sampleSession, mode: 'MockInterview', status: 'Live' }),
    );
    renderWithApp(<OverlayPage />, { api, route: '/overlay/s1', path: '/overlay/:sessionId' });

    await screen.findByRole('button', { name: 'Next question' });
    expect(lastScrollMode()).toBe('follow');
  });

  it('always jumps to the newest text on a live call', async () => {
    const { api } = fakeApi(() => json({ ...sampleSession, status: 'Live' }));
    renderWithApp(<OverlayPage />, { api, route: '/overlay/s1', path: '/overlay/:sessionId' });

    await screen.findByRole('button', { name: 'Pause auto-answer' });
    await waitFor(() => expect(lastScrollMode()).toBe('always'));
  });

  it('starts paused when the session has auto-answer off', async () => {
    const { api } = fakeApi(() => json({ ...sampleSession, autoAnswer: false }));
    renderWithApp(<OverlayPage />, { api, route: '/overlay/s1', path: '/overlay/:sessionId' });

    expect(await screen.findByRole('button', { name: 'Resume auto-answer' })).toBeInTheDocument();
  });

  it('runs the mock interview in the overlay instead of the answer assistant', async () => {
    const { api } = fakeApi((url) =>
      url.endsWith('/practice/attempts') ? json([]) : json({ ...sampleSession, mode: 'MockInterview', status: 'Live' }),
    );
    renderWithApp(<OverlayPage />, { api, route: '/overlay/s1', path: '/overlay/:sessionId' });

    expect(await screen.findByRole('button', { name: 'Next question' })).toBeEnabled();
    expect(screen.queryByRole('button', { name: 'Pause auto-answer' })).not.toBeInTheDocument();
  });

  it('keeps the answer assistant for live interviews', async () => {
    const { api } = fakeApi(() => json({ ...sampleSession, status: 'Live' }));
    renderWithApp(<OverlayPage />, { api, route: '/overlay/s1', path: '/overlay/:sessionId' });

    expect(await screen.findByRole('button', { name: 'Pause auto-answer' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Next question' })).not.toBeInTheDocument();
  });

  it('falls back to auto-answer on when the session cannot be read', async () => {
    const { api } = fakeApi(() => json({ detail: 'nope' }, 500));
    renderWithApp(<OverlayPage />, { api, route: '/overlay/s1', path: '/overlay/:sessionId' });

    expect(await screen.findByRole('button', { name: 'Pause auto-answer' })).toBeInTheDocument();
  });
});
