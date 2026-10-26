import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { fakeApi, json, renderWithApp, sampleSession } from '@/test/helpers';
import { OverlayPage } from './OverlayPage';

describe('OverlayPage', () => {
  it('starts paused when the session has auto-answer off', async () => {
    const { api } = fakeApi(() => json({ ...sampleSession, autoAnswer: false }));
    renderWithApp(<OverlayPage />, { api, route: '/overlay/s1', path: '/overlay/:sessionId' });

    expect(await screen.findByRole('button', { name: 'Resume auto-answer' })).toBeInTheDocument();
  });

  it('falls back to auto-answer on when the session cannot be read', async () => {
    const { api } = fakeApi(() => json({ detail: 'nope' }, 500));
    renderWithApp(<OverlayPage />, { api, route: '/overlay/s1', path: '/overlay/:sessionId' });

    expect(await screen.findByRole('button', { name: 'Pause auto-answer' })).toBeInTheDocument();
  });
});
