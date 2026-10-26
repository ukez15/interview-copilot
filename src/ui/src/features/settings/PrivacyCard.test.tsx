import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { createDesktopPlatform, createWebPlatform, type DesktopBridge } from '@/platform';
import { fakeApi, json, renderWithApp } from '@/test/helpers';
import { PrivacyCard } from './PrivacyCard';

const FOLDER = 'C:/Users/me/AppData/Roaming/InterviewCopilot';

function desktop(openDataFolder: () => Promise<void>) {
  const bridge = { getAuthToken: () => Promise.resolve('token'), openDataFolder };
  return createDesktopPlatform(bridge as unknown as DesktopBridge);
}

describe('PrivacyCard', () => {
  it('shows the data folder, what is stored and how to delete it', async () => {
    const { api, fetchImpl } = fakeApi(() => json({ dataDirectory: FOLDER }));
    renderWithApp(<PrivacyCard />, { api });

    expect(await screen.findByText(FOLDER)).toBeInTheDocument();
    expect(fetchImpl.mock.calls[0]?.[0]).toBe('http://api.test/api/storage');
    expect(screen.getByText(/account, sessions, transcripts, documents/i)).toBeInTheDocument();
    expect(screen.getByText(/quit InterviewCopilot, then delete this folder/i)).toBeInTheDocument();
  });

  it('hides the Open folder button on the web', async () => {
    const { api } = fakeApi(() => json({ dataDirectory: FOLDER }));
    renderWithApp(<PrivacyCard />, { api, platform: createWebPlatform() });

    await screen.findByText(FOLDER);
    expect(screen.queryByRole('button', { name: 'Open folder' })).not.toBeInTheDocument();
  });

  it('opens the folder from the desktop app without sending a path', async () => {
    const { api } = fakeApi(() => json({ dataDirectory: FOLDER }));
    const open = vi.fn().mockResolvedValue(undefined);
    renderWithApp(<PrivacyCard />, { api, platform: desktop(open) });

    await userEvent.click(await screen.findByRole('button', { name: 'Open folder' }));

    await waitFor(() => expect(open).toHaveBeenCalledWith());
  });

  it('shows why the folder could not be opened', async () => {
    const { api } = fakeApi(() => json({ dataDirectory: FOLDER }));
    renderWithApp(<PrivacyCard />, { api, platform: desktop(() => Promise.reject(new Error("Can't reach the local backend."))) });

    await userEvent.click(await screen.findByRole('button', { name: 'Open folder' }));

    expect(await screen.findByRole('alert')).toHaveTextContent("Can't reach the local backend.");
  });

  it('falls back to a generic message for non-Error rejections', async () => {
    const { api } = fakeApi(() => json({ dataDirectory: FOLDER }));
    renderWithApp(<PrivacyCard />, { api, platform: desktop(() => Promise.reject('nope')) });

    await userEvent.click(await screen.findByRole('button', { name: 'Open folder' }));

    expect(await screen.findByRole('alert')).toHaveTextContent("Couldn't open the data folder.");
  });

  it('shows an error when the path cannot be loaded', async () => {
    const { api } = fakeApi(() => json({ detail: 'boom' }, 500));
    renderWithApp(<PrivacyCard />, { api });

    expect(await screen.findByRole('alert')).toHaveTextContent("Couldn't load the data folder.");
  });
});
