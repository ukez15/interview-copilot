import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import { AppProvider } from '@/lib/app-context';
import { createWebPlatform, type CaptureStatus, type MeetingDetectedEvent, type Platform } from '@/platform';
import { fakeApi, json, sampleSession, type Handler } from '@/test/helpers';
import type { Session } from '@/types/api';
import { handleMeetingDetected } from './handle-meeting';
import { MeetingListener } from './MeetingListener';
import { meetingTitle } from './meeting-title';
import { StartFromMeetingPage } from './StartFromMeetingPage';

const idle: CaptureStatus = { state: 'idle', interviewer: false, mic: false, message: null };
const session = (over: Partial<Session>): Session => ({ ...sampleSession, ...over });
const NOW = new Date(2026, 9, 16, 14, 5);

function Where() {
  const location = useLocation();
  return <p data-testid="where">{location.pathname + location.search}</p>;
}

function routes(api: ReturnType<typeof fakeApi>['api'], platform: Platform, route = '/') {
  return render(
    <AppProvider api={api} platform={platform}>
      <MemoryRouter initialEntries={[route]}>
        <MeetingListener />
        <Where />
        <Routes>
          <Route path="/start-from-meeting" element={<StartFromMeetingPage />} />
          <Route path="*" element={<div>other</div>} />
        </Routes>
      </MemoryRouter>
    </AppProvider>,
  );
}

/** A platform whose meeting event the test fires by hand. */
function platformWithEvents() {
  const platform = createWebPlatform();
  let listener: ((event: MeetingDetectedEvent) => void) | null = null;
  const unsubscribe = vi.fn();
  vi.spyOn(platform, 'onMeetingDetected').mockImplementation((cb) => {
    listener = cb;
    return unsubscribe;
  });
  vi.spyOn(platform, 'getCaptureStatus').mockResolvedValue(idle);
  vi.spyOn(platform, 'startCapture').mockResolvedValue({ ...idle, state: 'live' });
  vi.spyOn(platform, 'showOverlay').mockResolvedValue(undefined);
  return { platform, unsubscribe, fire: (event: MeetingDetectedEvent) => listener?.(event) };
}

describe('meetingTitle', () => {
  it('formats "<App> meeting – dd MMM HH:mm" in local time', () => {
    expect(meetingTitle('Zoom', NOW)).toBe('Zoom meeting – 16 Oct 14:05');
    expect(meetingTitle('Webex', new Date(2026, 0, 3, 7, 9))).toBe('Webex meeting – 03 Jan 07:09');
  });
});

describe('MeetingListener', () => {
  it('subscribes once, goes to the picker when auto-start is off, and unsubscribes on unmount', async () => {
    const { platform, fire, unsubscribe } = platformWithEvents();
    const { api } = fakeApi(() => json([]));
    const view = routes(api, platform);

    fire({ app: 'Microsoft Teams', autoStart: false });

    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('/start-from-meeting?app=Microsoft%20Teams'));
    expect(platform.onMeetingDetected).toHaveBeenCalledTimes(1);
    view.unmount();
    expect(unsubscribe).toHaveBeenCalled();
  });

  it('ignores the event while capture is connecting, live or reconnecting', async () => {
    for (const state of ['connecting', 'live', 'reconnecting'] as const) {
      const { platform, fire } = platformWithEvents();
      vi.spyOn(platform, 'getCaptureStatus').mockResolvedValue({ ...idle, state });
      const { api, fetchImpl } = fakeApi(() => json([]));
      const view = routes(api, platform);

      fire({ app: 'Zoom', autoStart: true });
      await new Promise((resolve) => setTimeout(resolve, 0));

      expect(screen.getByTestId('where')).toHaveTextContent(/^\/$/);
      expect(fetchImpl).not.toHaveBeenCalled();
      view.unmount();
    }
  });
});

describe('handleMeetingDetected', () => {
  function setup(handler: Handler) {
    const { api, fetchImpl } = fakeApi(handler);
    const platform = createWebPlatform();
    vi.spyOn(platform, 'getCaptureStatus').mockResolvedValue(idle);
    const startCapture = vi.spyOn(platform, 'startCapture').mockResolvedValue({ ...idle, state: 'live' });
    const showOverlay = vi.spyOn(platform, 'showOverlay').mockResolvedValue(undefined);
    const navigate = vi.fn();
    const run = (event: MeetingDetectedEvent) => handleMeetingDetected(event, { api, platform, navigate, now: () => NOW });
    return { run, fetchImpl, navigate, startCapture, showOverlay };
  }

  it('copies the most recent session, starts it live, opens the overlay and the session', async () => {
    const older = session({ id: 'old', createdAt: '2026-10-01T09:00:00Z' });
    const newest = session({ id: 'new', createdAt: '2026-10-14T09:00:00Z', answerLength: 'Long' });
    const { run, fetchImpl, navigate, startCapture, showOverlay } = setup((url, init) => {
      if (url.endsWith('/api/sessions') && init.method === 'POST') return json(session({ id: 'copy' }), 201);
      if (url.endsWith('/api/sessions/copy/start')) return json(session({ id: 'copy', status: 'Live' }));
      return json([older, newest]);
    });

    await run({ app: 'Zoom', autoStart: true });

    const post = fetchImpl.mock.calls.find(([, init]) => init?.method === 'POST' && String(init.body).includes('copyFromSessionId'));
    expect(JSON.parse(String(post?.[1]?.body))).toMatchObject({ title: 'Zoom meeting – 16 Oct 14:05', copyFromSessionId: 'new' });
    expect(startCapture).toHaveBeenCalledWith('copy');
    expect(showOverlay).toHaveBeenCalledWith('copy');
    expect(navigate).toHaveBeenCalledWith('/sessions/copy');
  });

  it('falls back to the picker when there are no sessions', async () => {
    const { run, navigate, startCapture } = setup(() => json([]));

    await run({ app: 'Zoom', autoStart: true });

    expect(navigate).toHaveBeenCalledWith('/start-from-meeting?app=Zoom');
    expect(startCapture).not.toHaveBeenCalled();
  });

  it('falls back to the picker when copying fails', async () => {
    const { run, navigate, startCapture } = setup((_url, init) => (init.method === 'POST' ? new Response(null, { status: 500 }) : json([session({})])));

    await run({ app: 'Zoom', autoStart: true });

    expect(navigate).toHaveBeenCalledWith('/start-from-meeting?app=Zoom');
    expect(startCapture).not.toHaveBeenCalled();
  });

  it('still opens the picker when the capture status cannot be read', async () => {
    const { api } = fakeApi(() => json([]));
    const platform = createWebPlatform();
    vi.spyOn(platform, 'getCaptureStatus').mockRejectedValue(new Error('no bridge'));
    const navigate = vi.fn();

    await handleMeetingDetected({ app: 'Zoom', autoStart: false }, { api, platform, navigate, now: () => NOW });

    expect(navigate).toHaveBeenCalledWith('/start-from-meeting?app=Zoom');
  });
});

describe('StartFromMeetingPage', () => {
  const draft = session({ id: 'd1', title: 'Draft one', status: 'Draft' });
  const live = session({ id: 'l1', title: 'Live one', status: 'Live' });
  const ended = session({ id: 'e1', title: 'Ended one', status: 'Ended' });

  function setup(extra?: (url: string, init: RequestInit) => Response | null) {
    const { platform } = platformWithEvents();
    const { api, fetchImpl } = fakeApi((url, init) => extra?.(url, init) ?? json([draft, live, ended]));
    routes(api, platform, '/start-from-meeting?app=Zoom');
    return { platform, fetchImpl };
  }

  it('names the app and lists only Draft and Live sessions', async () => {
    setup();

    expect(await screen.findByRole('heading', { name: /Zoom meeting detected/ })).toBeInTheDocument();
    expect(await screen.findByText('Draft one')).toBeInTheDocument();
    expect(screen.getByText('Live one')).toBeInTheDocument();
    expect(screen.queryByText('Ended one')).not.toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Use this' })).toHaveLength(2);
  });

  it('starts a Draft session live, opens the overlay and goes to the session', async () => {
    const { platform, fetchImpl } = setup((url) => (url.endsWith('/sessions/d1/start') ? json({ ...draft, status: 'Live' }) : null));
    await screen.findByText('Draft one');

    await userEvent.click(screen.getAllByRole('button', { name: 'Use this' })[0]!);

    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('/sessions/d1'));
    expect(fetchImpl.mock.calls.some(([url]) => String(url).endsWith('/sessions/d1/start'))).toBe(true);
    expect(platform.startCapture).toHaveBeenCalledWith('d1');
    expect(platform.showOverlay).toHaveBeenCalledWith('d1');
  });

  it('only opens the overlay and starts capture for a Live session', async () => {
    const { platform, fetchImpl } = setup();
    await screen.findByText('Live one');

    await userEvent.click(screen.getAllByRole('button', { name: 'Use this' })[1]!);

    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('/sessions/l1'));
    expect(fetchImpl.mock.calls.some(([url]) => String(url).includes('/start'))).toBe(false);
    expect(platform.showOverlay).toHaveBeenCalledWith('l1');
    expect(platform.startCapture).toHaveBeenCalledWith('l1');
  });

  it('creates a new session from the form, starts it and goes to it', async () => {
    const { platform } = setup((url, init) => {
      if (init.method === 'POST' && url.endsWith('/api/sessions')) return json(session({ id: 'n1', title: 'Fresh' }), 201);
      if (url.endsWith('/sessions/n1/start')) return json(session({ id: 'n1', status: 'Live' }));
      return null;
    });
    await screen.findByText('Draft one');

    await userEvent.type(screen.getByLabelText('Title'), 'Fresh');
    await userEvent.click(screen.getByRole('button', { name: 'Create session' }));

    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('/sessions/n1'));
    expect(platform.startCapture).toHaveBeenCalledWith('n1');
    expect(platform.showOverlay).toHaveBeenCalledWith('n1');
  });

  describe('with a job link pasted into the title', () => {
    const LINK = 'https://jobs.test/roles/1';
    const preview = { suggestedName: 'Fresh - Acme.txt', text: 'Build things.', source: 'JsonLd' };

    function withJobLink(uploadResponse: Response) {
      return setup((url, init) => {
        if (url.endsWith('/from-link/preview')) return json(preview);
        if (url.endsWith('/sessions/n1/documents') && init.method === 'POST') return uploadResponse;
        if (init.method === 'POST' && url.endsWith('/api/sessions')) return json(session({ id: 'n1', title: 'Fresh' }), 201);
        if (url.endsWith('/sessions/n1/start')) return json(session({ id: 'n1', status: 'Live' }));
        return null;
      });
    }

    async function pasteAndCreate() {
      await screen.findByText('Draft one');
      fireEvent.paste(screen.getByLabelText('Title'), { clipboardData: { getData: () => LINK } });
      await screen.findByLabelText('Job description');
      await userEvent.click(screen.getByRole('button', { name: 'Create session' }));
    }

    it('uploads the job description, then starts and goes to the session', async () => {
      const { platform, fetchImpl } = withJobLink(json({ id: 'd9', fileName: 'Fresh - Acme.txt', characterCount: 13, createdAt: '' }, 201));

      await pasteAndCreate();

      await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('/sessions/n1'));
      const upload = fetchImpl.mock.calls.find(([url]) => String(url).endsWith('/sessions/n1/documents'))!;
      expect(((upload[1]!.body as FormData).get('file') as File).name).toBe('Fresh - Acme.txt');
      expect(platform.startCapture).toHaveBeenCalledWith('n1');
      expect(platform.showOverlay).toHaveBeenCalledWith('n1');
    });

    it('still starts the meeting when the upload fails', async () => {
      const { platform } = withJobLink(json({ detail: 'Document limit reached.' }, 400));

      await pasteAndCreate();

      await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('/sessions/n1'));
      expect(platform.startCapture).toHaveBeenCalledWith('n1');
      expect(platform.showOverlay).toHaveBeenCalledWith('n1');
    });
  });

  it('shows an error and stays when starting fails', async () => {
    setup((url) => (url.endsWith('/sessions/d1/start') ? new Response(null, { status: 409 }) : null));
    await screen.findByText('Draft one');

    await userEvent.click(screen.getAllByRole('button', { name: 'Use this' })[0]!);

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.getByTestId('where')).toHaveTextContent('/start-from-meeting');
  });

  it('shows an error when the list cannot load, and "Not now" returns to the start page', async () => {
    setup(() => new Response(null, { status: 500 }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('link', { name: 'Not now' }));

    expect(screen.getByTestId('where')).toHaveTextContent(/^\/$/);
  });

  it('copes with a missing app name', async () => {
    const { platform } = platformWithEvents();
    const { api } = fakeApi(() => json([]));
    routes(api, platform, '/start-from-meeting');

    expect(await screen.findByRole('heading', { name: /Meeting detected/ })).toBeInTheDocument();
  });
});
