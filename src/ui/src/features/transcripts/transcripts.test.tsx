import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { fakeApi, json, renderWithApp, sampleSession } from '@/test/helpers';
import type { Transcript } from '@/types/api';
import { SessionPage } from '../sessions/SessionPage';
import { SessionsPage } from '../sessions/SessionsPage';
import { formatTimestamp, TranscriptPage } from './TranscriptPage';

const transcript: Transcript = {
  sessionId: 's1',
  title: 'Acme onsite',
  mode: 'LiveInterview',
  createdAt: '2026-10-15T09:00:00Z',
  entries: [
    { atMs: 3_723_000, speaker: 'Candidate', text: 'Third line' },
    { atMs: 1_000, speaker: 'Interviewer', text: 'First line' },
    { atMs: 65_000, speaker: 'Candidate', text: 'Second line' },
  ],
  answers: [
    { createdAt: '2026-10-15T09:10:00Z', question: 'Later question', questionType: 'Situational', version: 2, text: 'Later answer' },
    { createdAt: '2026-10-15T09:05:00Z', question: 'Early question', questionType: 'Behavioral', version: 1, text: 'Early answer' },
  ],
};

function renderPage(handler: Parameters<typeof fakeApi>[0]) {
  const { api, fetchImpl } = fakeApi(handler);
  renderWithApp(<TranscriptPage />, { api, route: '/sessions/s1/transcript', path: '/sessions/:sessionId/transcript' });
  return fetchImpl;
}

describe('formatTimestamp', () => {
  it('formats milliseconds as hh:mm:ss', () => {
    expect(formatTimestamp(0)).toBe('00:00:00');
    expect(formatTimestamp(65_000)).toBe('00:01:05');
    expect(formatTimestamp(3_723_999)).toBe('01:02:03');
  });
});

describe('TranscriptPage', () => {
  it('shows entries in time order with speaker and timestamp', async () => {
    renderPage(() => json(transcript));

    expect(await screen.findByRole('heading', { name: 'Acme onsite' })).toBeInTheDocument();
    const items = within(screen.getByRole('list', { name: 'Transcript' })).getAllByRole('listitem');
    expect(items.map((item) => item.textContent)).toEqual([
      '00:00:01Them: First line',
      '00:01:05Me: Second line',
      '01:02:03Me: Third line',
    ]);
    expect(screen.getByRole('link', { name: /back to session/i })).toHaveAttribute('href', '/sessions/s1');
  });

  it('shows suggested answers in time order with type and version', async () => {
    renderPage(() => json(transcript));

    const list = await screen.findByRole('list', { name: 'Suggested answers' });
    const items = within(list).getAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent('Early question');
    expect(items[0]).toHaveTextContent('Behavioral');
    expect(items[0]).toHaveTextContent('v1');
    expect(items[0]).toHaveTextContent('Early answer');
    expect(items[1]).toHaveTextContent('Later question');
    expect(items[1]).toHaveTextContent('Situational');
    expect(items[1]).toHaveTextContent('v2');
  });

  it('shows empty states', async () => {
    renderPage(() => json({ ...transcript, entries: [], answers: [] }));

    expect(await screen.findByText('No transcript was captured for this session.')).toBeInTheDocument();
    expect(screen.getByText('No suggested answers yet.')).toBeInTheDocument();
  });

  it('shows a loading state, then an error', async () => {
    renderPage(() => json({ detail: 'Transcript missing' }, 404));

    expect(screen.getByText('Loading…')).toBeInTheDocument();
    expect(await screen.findByRole('alert')).toHaveTextContent('Transcript missing');
  });

  it('exports the PDF', async () => {
    URL.createObjectURL = vi.fn(() => 'blob:x');
    URL.revokeObjectURL = vi.fn();
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    const fetchImpl = renderPage((url) => (url.endsWith('/pdf') ? new Response(new Blob(['%PDF'])) : json(transcript)));

    await userEvent.click(await screen.findByRole('button', { name: 'Export PDF' }));

    await vi.waitFor(() => expect(click).toHaveBeenCalled());
    expect(fetchImpl.mock.calls.some(([url]) => String(url).endsWith('/api/sessions/s1/transcript/pdf'))).toBe(true);
    click.mockRestore();
  });

  it('shows an export failure and keeps the transcript', async () => {
    renderPage((url) => (url.endsWith('/pdf') ? json({ detail: 'Export failed here' }, 500) : json(transcript)));

    await userEvent.click(await screen.findByRole('button', { name: 'Export PDF' }));

    expect(await screen.findByText('Export failed here')).toBeInTheDocument();
    expect(screen.getByText('First line')).toBeInTheDocument();
  });

  it('renders 1,000 entries', async () => {
    const entries = Array.from({ length: 1000 }, (_, i) => ({
      atMs: i * 1000,
      speaker: i % 2 === 0 ? ('Interviewer' as const) : ('Candidate' as const),
      text: `Line ${i}`,
    }));
    renderPage(() => json({ ...transcript, entries }));

    await screen.findByText('Line 999');
    expect(within(screen.getByRole('list', { name: 'Transcript' })).getAllByRole('listitem')).toHaveLength(1000);
  });
});

describe('transcript links', () => {
  it('SessionPage links to the transcript', async () => {
    const { api } = fakeApi(() => json(sampleSession));
    renderWithApp(<SessionPage />, { api, route: '/sessions/s1', path: '/sessions/:sessionId' });

    expect(await screen.findByRole('link', { name: 'View transcript' })).toHaveAttribute('href', '/sessions/s1/transcript');
  });

  it('SessionsPage links only ended sessions to their transcript', async () => {
    const ended = { ...sampleSession, id: 's2', title: 'Done call', status: 'Ended' as const };
    const { api } = fakeApi(() => json([sampleSession, ended]));
    renderWithApp(<SessionsPage />, { api });

    const links = await screen.findAllByRole('link', { name: 'View transcript' });
    expect(links).toHaveLength(1);
    expect(links[0]).toHaveAttribute('href', '/sessions/s2/transcript');
  });
});
