import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { App } from '@/App';
import { AppProvider } from '@/lib/app-context';
import { createWebPlatform } from '@/platform';
import { fakeApi, json, renderWithApp, sampleSession, sse } from '@/test/helpers';
import { SignInPage } from './auth/SignInPage';
import { AnswerCard } from './copilot/AnswerCard';
import { LiveAssistant } from './copilot/LiveAssistant';
import { OverlayPage } from './overlay/OverlayPage';
import { SessionPage } from './sessions/SessionPage';
import { SessionsPage } from './sessions/SessionsPage';
import { SettingsPage } from './settings/SettingsPage';

describe('SignInPage', () => {
  it('registers, signs in and shares the token with the platform', async () => {
    const { api, fetchImpl } = fakeApi((url) => (url.endsWith('/sign-in') ? json({ accessToken: 'tok' }) : json({}, 201)));
    const platform = createWebPlatform();
    renderWithApp(<SignInPage />, { api, platform });

    await userEvent.click(screen.getByRole('button', { name: /create an account/i }));
    await userEvent.type(screen.getByLabelText('Username'), 'alice');
    await userEvent.type(screen.getByLabelText('Password'), 'long-enough');
    await userEvent.click(screen.getByRole('button', { name: 'Create account' }));

    await waitFor(() => expect(fetchImpl).toHaveBeenCalledTimes(2));
    expect(await platform.getAuthToken()).toBe('tok');
  });

  it('shows the error when sign-in fails', async () => {
    const { api } = fakeApi(() => new Response(null, { status: 401 }));
    renderWithApp(<SignInPage />, { api });

    await userEvent.type(screen.getByLabelText('Username'), 'alice');
    await userEvent.type(screen.getByLabelText('Password'), 'wrong-password');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Please sign in again.');
  });
});

describe('SessionsPage', () => {
  it('lists history and creates a session with the chosen settings', async () => {
    const { api, fetchImpl } = fakeApi((url, init) => (init.method === 'POST' ? json(sampleSession, 201) : json([sampleSession])));
    renderWithApp(<SessionsPage />, { api });

    expect(await screen.findByText('Acme onsite')).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('Title'), 'Globex call');
    await userEvent.selectOptions(screen.getByLabelText('Mode'), 'Conversation');
    await userEvent.selectOptions(screen.getByLabelText('Answer length'), 'Long');
    await userEvent.selectOptions(screen.getByLabelText('Answer framework'), 'Smart');
    await userEvent.selectOptions(screen.getByLabelText(/Pin a question type/), 'SystemDesign');
    await userEvent.selectOptions(screen.getByLabelText(/Pin a question type/), '');
    await userEvent.type(screen.getByLabelText('Session prompt'), 'Buyer is a CTO');
    await userEvent.click(screen.getByRole('button', { name: 'Create session' }));

    expect(await screen.findByText('navigated')).toBeInTheDocument();
    const body = JSON.parse(String(fetchImpl.mock.calls[1]![1]!.body)) as Record<string, unknown>;
    expect(body).toMatchObject({ title: 'Globex call', mode: 'Conversation', answerLength: 'Long', framework: 'Smart', pinnedQuestionType: null });
  });

  it('shows errors from the backend', async () => {
    const { api } = fakeApi((_, init) => (init.method === 'POST' ? json({ detail: 'Bad title' }, 400) : json([])));
    renderWithApp(<SessionsPage />, { api });

    expect(await screen.findByText('No sessions yet.')).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('Title'), 'x');
    await userEvent.click(screen.getByRole('button', { name: 'Create session' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Bad title');
  });

  it('shows a load error', async () => {
    const { api } = fakeApi(() => json({ detail: 'Backend broke' }, 500));
    renderWithApp(<SessionsPage />, { api });

    expect(await screen.findByRole('alert')).toHaveTextContent('Backend broke');
  });
});

describe('SessionPage', () => {
  const route = { route: '/sessions/s1', path: '/sessions/:sessionId' };

  it('uploads and removes documents, starts and ends the session and opens the overlay', async () => {
    const live = { ...sampleSession, status: 'Live' as const };
    const { api } = fakeApi((url, init) => {
      if (url.endsWith('/documents') && init.method === 'POST') return json({ id: 'd2', fileName: 'jd.txt', characterCount: 5, createdAt: '' }, 201);
      if (url.includes('/documents/')) return new Response(null, { status: 204 });
      if (url.endsWith('/start')) return json(live);
      if (url.endsWith('/end')) return json({ ...sampleSession, status: 'Ended' });
      return json(sampleSession);
    });
    const platform = createWebPlatform();
    const showOverlay = vi.spyOn(platform, 'showOverlay');
    const hideOverlay = vi.spyOn(platform, 'hideOverlay');
    renderWithApp(<SessionPage />, { api, platform, ...route });

    expect(await screen.findByText('Acme onsite')).toBeInTheDocument();
    expect(screen.getByLabelText('Add documents')).toHaveAttribute('accept', '.txt,.md,.markdown,.pdf,.docx');
    expect(screen.getByText(/PDF and Word \.docx/)).toBeInTheDocument();
    await userEvent.upload(screen.getByLabelText('Add documents'), new File(['JD'], 'jd.txt', { type: 'text/plain' }));
    expect(await screen.findByText('jd.txt')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Remove resume.txt' }));
    await waitFor(() => expect(screen.queryByText('resume.txt')).not.toBeInTheDocument());

    await userEvent.click(screen.getByRole('button', { name: 'Start session' }));
    await userEvent.click(await screen.findByRole('button', { name: 'End session' }));

    expect(showOverlay).toHaveBeenCalledWith('s1');
    expect(hideOverlay).toHaveBeenCalled();
  });

  it('refuses more than 10 documents before uploading', async () => {
    const full = { ...sampleSession, documents: Array.from({ length: 9 }, (_, i) => ({ ...sampleSession.documents[0]!, id: `d${i}`, fileName: `f${i}.txt` })) };
    const { api, fetchImpl } = fakeApi(() => json(full));
    renderWithApp(<SessionPage />, { api, ...route });

    await screen.findByText('Acme onsite');
    await userEvent.upload(screen.getByLabelText('Add documents'), [new File(['a'], 'a.txt'), new File(['b'], 'b.txt')]);

    expect(await screen.findByRole('alert')).toHaveTextContent('You can add 1 more document (max 10).');
    expect(fetchImpl).toHaveBeenCalledTimes(1);
  });

  it('exports the transcript as a PDF download', async () => {
    const { api } = fakeApi((url) => (url.endsWith('/pdf') ? new Response(new Blob(['%PDF'])) : json(sampleSession)));
    URL.createObjectURL = vi.fn(() => 'blob:x');
    URL.revokeObjectURL = vi.fn();
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    renderWithApp(<SessionPage />, { api, ...route });

    await userEvent.click(await screen.findByRole('button', { name: 'Export PDF' }));

    await waitFor(() => expect(click).toHaveBeenCalled());
  });

  it('shows errors for loading, uploads and exports', async () => {
    const { api } = fakeApi((url) => {
      if (url.endsWith('/documents')) return json({ detail: 'Unsupported file' }, 400);
      if (url.endsWith('/pdf')) return json({ detail: 'Export failed here' }, 500);
      return json(sampleSession);
    });
    renderWithApp(<SessionPage />, { api, ...route });

    await userEvent.upload(await screen.findByLabelText('Add documents'), new File(['x'], 'x.txt'));
    expect(await screen.findByText('Unsupported file')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Export PDF' }));
    expect(await screen.findByText('Export failed here')).toBeInTheDocument();
  });

  it('shows a not-found error', async () => {
    const { api } = fakeApi(() => json({ detail: 'Session not found.' }, 404));
    renderWithApp(<SessionPage />, { api, ...route });

    expect(await screen.findByRole('alert')).toHaveTextContent('Session not found.');
  });
});

describe('LiveAssistant and AnswerCard', () => {
  it('streams an answer and re-asks with Shorter', async () => {
    const { api, fetchImpl } = fakeApi(() => sse(['meta', '{"questionType":"Behavioral","version":1}'], ['delta', 'I led'], ['delta', ' it.'], ['done', '']));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api });

    await userEvent.type(screen.getByLabelText('Question'), 'Tell me about a time you led');
    await userEvent.click(screen.getByRole('button', { name: 'Answer' }));

    expect(await screen.findByText('I led it.')).toBeInTheDocument();
    expect(screen.getByText('Behavioral · v1')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /Shorter/ }));
    await waitFor(() => expect(fetchImpl).toHaveBeenCalledTimes(2));
    expect(JSON.parse(String(fetchImpl.mock.calls[1]![1]!.body))).toMatchObject({ modifier: 'Shorter', previousAnswer: 'I led it.', previousVersion: 1 });
  });

  it('shows stream and network errors', async () => {
    const { api } = fakeApi(() => sse(['error', "Can't reach Ollama."]));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api });

    await userEvent.type(screen.getByLabelText('Question'), 'Why?');
    await userEvent.click(screen.getByRole('button', { name: 'Answer' }));

    expect(await screen.findByRole('alert')).toHaveTextContent("Can't reach Ollama.");
  });

  it('reports a failed request', async () => {
    const { api } = fakeApi(() => json({ detail: 'Session not found.' }, 404));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api });

    fireEvent.change(screen.getByLabelText('Question'), { target: { value: 'Why?' } });
    fireEvent.submit(screen.getByLabelText('Question'));

    expect(await screen.findByRole('alert')).toHaveTextContent('Session not found.');
  });

  it('ignores empty questions and renders the idle and longer/regenerate states', async () => {
    const onModify = vi.fn();
    render(<AnswerCard answer={{ question: 'Q', text: 'A', questionType: null, version: 2, status: 'done', error: null }} onModify={onModify} />);

    await userEvent.click(screen.getByRole('button', { name: /Longer/ }));
    await userEvent.click(screen.getByRole('button', { name: /Regenerate/ }));
    expect(onModify.mock.calls.map(([m]) => m)).toEqual(['Longer', 'Regenerate']);

    const { api, fetchImpl } = fakeApi(() => json({}));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api });
    fireEvent.submit(screen.getByLabelText('Question'));
    expect(fetchImpl).not.toHaveBeenCalled();
    expect(screen.getByText(/Suggested answers appear here/)).toBeInTheDocument();
  });
});

describe('SettingsPage', () => {
  it('changes opacity and auto-start, and saves a BYOK key when a provider needs one', async () => {
    const { api } = fakeApi(() =>
      json({
        speechToText: [
          { name: 'whisper', displayName: 'Whisper (local, free)', isLocal: true, requiresApiKey: false, isDefault: true },
          { name: 'deepgram', displayName: 'Deepgram (cloud, paid)', isLocal: false, requiresApiKey: true, isDefault: false },
        ],
        llm: [{ name: 'ollama', displayName: 'Ollama (local, free)', isLocal: true, requiresApiKey: false, isDefault: true }],
      }),
    );
    const platform = createWebPlatform();
    const setOpacity = vi.spyOn(platform, 'setOverlayOpacity');
    renderWithApp(<SettingsPage />, { api, platform });

    expect(await screen.findByText('Deepgram (cloud, paid)')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Reset to 85%' }));
    expect(setOpacity).toHaveBeenCalledWith(0.85);
    fireEvent.keyDown(screen.getByRole('slider', { name: 'Overlay opacity' }), { key: 'ArrowLeft' });
    await waitFor(() => expect(screen.getByText('Opacity: 80%')).toBeInTheDocument());

    await userEvent.click(screen.getByRole('checkbox'));
    expect((await platform.getSettings()).autoStartOnMeeting).toBe(true);

    await userEvent.type(screen.getByLabelText('deepgram API key'), 'dg-key');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('Saving API keys needs the desktop app.')).toBeInTheDocument();
  });

  it('confirms when the key is saved and shows provider load errors', async () => {
    const platform = createWebPlatform();
    vi.spyOn(platform, 'setApiKey').mockResolvedValue();
    const { api } = fakeApi(() =>
      json({ speechToText: [{ name: 'deepgram', displayName: 'Deepgram', isLocal: false, requiresApiKey: true, isDefault: true }], llm: [] }),
    );
    renderWithApp(<SettingsPage />, { api, platform });

    await userEvent.type(await screen.findByLabelText('deepgram API key'), 'k');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('Saved to your OS keychain.')).toBeInTheDocument();

    const failing = fakeApi(() => json({ detail: 'No providers' }, 500));
    renderWithApp(<SettingsPage />, { api: failing.api });
    expect(await screen.findByText('No providers')).toBeInTheDocument();
  });
});

describe('App shell', () => {
  it('shows sign-in until a token exists, then the overlay route', async () => {
    const { api } = fakeApi(() => json([]));
    const platform = createWebPlatform();
    const view = render(
      <AppProvider api={api} platform={platform}>
        <App />
      </AppProvider>,
    );
    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument();

    await platform.setAuthToken('tok');
    window.location.hash = '#/overlay/s1';
    view.unmount();
    render(
      <AppProvider api={api} platform={platform}>
        <App />
      </AppProvider>,
    );

    expect(await screen.findByLabelText('Question')).toBeInTheDocument();
    expect(document.documentElement).toHaveClass('overlay');
  });

  it('requires the provider', () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    expect(() => render(<OverlayPage />)).toThrow();
  });
});
