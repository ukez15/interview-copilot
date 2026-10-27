import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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
import { chooseOption, closeSelect, openSelect } from '@/test/select';

describe('SignInPage', () => {
  it('registers, signs in and shares the token with the platform', async () => {
    const { api, fetchImpl } = fakeApi((url) => (url.endsWith('/sign-in') ? json({ accessToken: 'tok', refreshToken: 'ref' }) : json({}, 201)));
    const platform = createWebPlatform();
    const setTokens = vi.spyOn(platform, 'setAuthTokens');
    renderWithApp(<SignInPage />, { api, platform });

    await userEvent.click(screen.getByRole('button', { name: /create an account/i }));
    await userEvent.type(screen.getByLabelText('Username'), 'alice');
    await userEvent.type(screen.getByLabelText('Password'), 'long-enough');
    await userEvent.click(screen.getByRole('button', { name: 'Create account' }));

    await waitFor(() => expect(fetchImpl).toHaveBeenCalledTimes(2));
    expect(setTokens).toHaveBeenCalledWith({ accessToken: 'tok', refreshToken: 'ref' });
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
    await chooseOption(userEvent, 'Mode', 'Sales call / other conversation');
    await chooseOption(userEvent, 'Answer length', 'Long');
    await chooseOption(userEvent, 'Answer framework', 'SMART');
    await chooseOption(userEvent, /Pin a question type/, 'System design');
    await chooseOption(userEvent, /Pin a question type/, 'Auto-detect');
    await userEvent.type(screen.getByLabelText('Session prompt'), 'Buyer is a CTO');
    await userEvent.click(screen.getByRole('button', { name: 'Create session' }));

    expect(await screen.findByText('navigated')).toBeInTheDocument();
    const post = fetchImpl.mock.calls.find((call) => call[1]?.method === 'POST')!;
    const body = JSON.parse(String(post[1]!.body)) as Record<string, unknown>;
    expect(body).toMatchObject({ title: 'Globex call', mode: 'Conversation', answerLength: 'Long', framework: 'Smart', pinnedQuestionType: null });
  });

  it('removes a session from the history once it is deleted (#80)', async () => {
    const ended = { ...sampleSession, status: 'Ended' as const };
    const { api } = fakeApi((url, init) => (init.method === 'DELETE' ? new Response(null, { status: 204 }) : json([ended])));
    renderWithApp(<SessionsPage />, { api });

    await screen.findByText('Acme onsite');
    await userEvent.click(screen.getByRole('button', { name: 'Delete' }));
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Delete' }));

    await waitFor(() => expect(screen.queryByText('Acme onsite')).not.toBeInTheDocument());
    expect(screen.getByText('No sessions yet.')).toBeInTheDocument();
  });

  describe('with a job link pasted into the title', () => {
    const LINK = 'https://jobs.test/roles/1';
    const created = { ...sampleSession, id: 'n1', title: 'Senior Engineer - Acme', documents: [] };
    const preview = { suggestedName: 'Senior Engineer - Acme.txt', text: 'Build things.', source: 'JsonLd' };

    function routes(uploadResponse: Response) {
      return fakeApi((url, init) => {
        if (url.endsWith('/from-link/preview')) return json(preview);
        if (url.endsWith('/api/sessions/n1/documents') && init.method === 'POST') return uploadResponse;
        if (init.method === 'POST') return json(created, 201);
        return json([]);
      });
    }

    async function pasteAndCreate() {
      await screen.findByText('No sessions yet.');
      fireEvent.paste(screen.getByLabelText('Title'), { clipboardData: { getData: () => LINK } });
      await screen.findByLabelText('Job description');
      await userEvent.click(screen.getByRole('button', { name: 'Create session' }));
    }

    it('uploads the job description after creating the session, then navigates', async () => {
      const { api, fetchImpl } = routes(json({ id: 'd9', fileName: 'Senior Engineer - Acme.txt', characterCount: 13, createdAt: '' }, 201));
      renderWithApp(<SessionsPage />, { api });

      await pasteAndCreate();

      expect(await screen.findByText('navigated')).toBeInTheDocument();
      const calls = fetchImpl.mock.calls.map(([url, init]) => `${init?.method ?? 'GET'} ${String(url).replace('http://api.test', '')}`);
      expect(calls.slice(-2)).toEqual(['POST /api/sessions', 'POST /api/sessions/n1/documents']);
      const upload = fetchImpl.mock.calls.find(([url]) => String(url).endsWith('/api/sessions/n1/documents'))!;
      const file = (upload[1]!.body as FormData).get('file') as File;
      expect(file.name).toBe('Senior Engineer - Acme.txt');
      expect(await file.text()).toBe('Build things.');
    });

    it('stays on the page, lists the new session and explains when the upload fails', async () => {
      const { api } = routes(json({ detail: 'Document limit reached.' }, 400));
      renderWithApp(<SessionsPage />, { api });

      await pasteAndCreate();

      expect(await screen.findByRole('alert')).toHaveTextContent(
        "The session was created, but the job description couldn't be saved: Document limit reached. Add it from the session's Documents panel.",
      );
      expect(screen.queryByText('navigated')).not.toBeInTheDocument();
      expect(screen.getByRole('link', { name: /Senior Engineer - Acme/ })).toHaveAttribute('href', '/sessions/n1');
      expect(screen.queryByText('No sessions yet.')).not.toBeInTheDocument();
    });

    it('does not upload anything when there is no job description', async () => {
      const { api, fetchImpl } = routes(json({}, 201));
      renderWithApp(<SessionsPage />, { api });
      await screen.findByText('No sessions yet.');

      await userEvent.type(screen.getByLabelText('Title'), 'Plain');
      await userEvent.click(screen.getByRole('button', { name: 'Create session' }));

      expect(await screen.findByText('navigated')).toBeInTheDocument();
      expect(fetchImpl.mock.calls.some(([url]) => String(url).endsWith('/documents'))).toBe(false);
    });
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
    expect(screen.getByText('Choose files').tagName).toBe('LABEL');
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
    const { api } = fakeApi((url) => (url.endsWith('/pdf') ? new Response('%PDF', { headers: { 'Content-Type': 'application/pdf' } }) : json(sampleSession)));
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

  it('shows the practice panel instead of the live assistant in mock interviews', async () => {
    const { api } = fakeApi((url) => (url.endsWith('/practice/attempts') ? json([]) : json({ ...sampleSession, mode: 'MockInterview' })));
    renderWithApp(<SessionPage />, { api, ...route });

    expect(await screen.findByRole('button', { name: 'Next question' })).toBeInTheDocument();
    expect(screen.queryByText('Live assistant')).not.toBeInTheDocument();
  });

  it('goes back to the session list after deleting the session (#80)', async () => {
    const { api } = fakeApi((url, init) =>
      init.method === 'DELETE' ? new Response(null, { status: 204 }) : json({ ...sampleSession, status: 'Ended' }),
    );
    renderWithApp(<SessionPage />, { api, ...route });

    await userEvent.click(await screen.findByRole('button', { name: 'Delete' }));
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Delete' }));

    expect(await screen.findByText('navigated')).toBeInTheDocument();
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
    const asks = () => fetchImpl.mock.calls.filter((call) => call[1]?.method === 'POST');
    await waitFor(() => expect(asks()).toHaveLength(2));
    expect(JSON.parse(String(asks()[1]![1]!.body))).toMatchObject({ modifier: 'Shorter', previousAnswer: 'I led it.', previousVersion: 1 });
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
    const { api } = fakeApi((url, init) =>
      url.endsWith('/api/pairing/browsers')
        ? json([])
        : !url.endsWith('/api/providers')
        ? json(url.endsWith('/models') ? [] : { llmModel: null, speechToTextProvider: init.method === 'PUT' ? 'deepgram' : null })
        : json({
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

    const providers = await openSelect(userEvent, 'Speech-to-text provider');
    expect(await providers.findByRole('option', { name: 'Deepgram (cloud, paid)' })).toBeInTheDocument();
    await closeSelect(userEvent);
    await userEvent.click(screen.getByRole('button', { name: 'Reset to 85%' }));
    expect(setOpacity).toHaveBeenCalledWith(0.85);
    fireEvent.keyDown(screen.getByRole('slider', { name: 'Overlay opacity' }), { key: 'ArrowLeft' });
    await waitFor(() => expect(screen.getByText('Opacity: 80%')).toBeInTheDocument());

    await userEvent.click(screen.getByRole('checkbox'));
    expect((await platform.getSettings()).autoStartOnMeeting).toBe(true);

    await chooseOption(userEvent, 'Speech-to-text provider', 'Deepgram (cloud, paid)');
    await userEvent.type(await screen.findByLabelText('deepgram API key'), 'dg-key');
    await userEvent.click(screen.getByRole('button', { name: 'Save key' }));
    expect(await screen.findByText('Saving API keys needs the desktop app.')).toBeInTheDocument();
  });

  it('confirms when the key is saved and shows provider load errors', async () => {
    const platform = createWebPlatform();
    vi.spyOn(platform, 'setApiKey').mockResolvedValue();
    const { api } = fakeApi((url) =>
      url.endsWith('/api/pairing/browsers')
        ? json([])
        : !url.endsWith('/api/providers')
        ? json(url.endsWith('/models') ? [] : { llmModel: null })
        : json({
            speechToText: [
              { name: 'deepgram', displayName: 'Deepgram', isLocal: false, requiresApiKey: true, isDefault: true },
              { name: 'whisper', displayName: 'Whisper', isLocal: true, requiresApiKey: false, isDefault: false },
            ],
            llm: [{ name: 'ollama', displayName: 'Ollama', isLocal: true, requiresApiKey: true, isDefault: true }],
          }),
    );
    renderWithApp(<SettingsPage />, { api, platform });

    await userEvent.type(await screen.findByLabelText('deepgram API key'), 'k');
    await userEvent.click(screen.getByRole('button', { name: 'Save key' }));
    await waitFor(() => expect(platform.setApiKey).toHaveBeenCalledWith('deepgram', 'k'));

    const failing = fakeApi((url) => (url.endsWith('/api/pairing/browsers') ? json([]) : url.endsWith('/api/providers') ? json({ detail: 'No providers' }, 500) : json(url.endsWith('/models') ? [] : { llmModel: null })));
    renderWithApp(<SettingsPage />, { api: failing.api });
    expect((await screen.findAllByText('No providers')).length).toBeGreaterThan(0);
  });
});

describe('Sign-in persistence', () => {
  const tokens = { accessToken: 'tok', refreshToken: 'ref' };

  function renderApp(api: ReturnType<typeof fakeApi>['api'], platform: ReturnType<typeof createWebPlatform>, hash: string) {
    window.location.hash = hash;
    return render(
      <AppProvider api={api} platform={platform}>
        <App />
      </AppProvider>,
    );
  }

  it('signs out from Settings: clears the tokens and shows the sign-in page', async () => {
    // Settings also renders the model picker (#31) and speech-model card (#19): answer each endpoint properly.
    const { api } = fakeApi((url) => {
      if (url.endsWith('/api/providers/ollama/models')) return json([]);
      if (url.endsWith('/api/pairing/browsers')) return json([]);
      if (url.endsWith('/api/preferences')) return json({ llmModel: null, defaultLlmModel: 'llama3.2:3b' });
      if (url.endsWith('/api/providers/whisper/model')) {
        return json({ state: 'ready', model: 'ggml-small.en.bin', bytesDownloaded: 0, totalBytes: null, message: null });
      }
      return json({ speechToText: [], llm: [] });
    });
    const platform = createWebPlatform();
    await platform.setAuthTokens(tokens);
    renderApp(api, platform, '#/settings');

    await userEvent.click(await screen.findByRole('button', { name: 'Sign out' }));

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    expect(await platform.getAuthToken()).toBeNull();
  });

  it('returns to the sign-in page when an expired session cannot be refreshed', async () => {
    const { api } = fakeApi(() => new Response(null, { status: 401 }));
    const platform = createWebPlatform();
    await platform.setAuthTokens(tokens);
    renderApp(api, platform, '#/');

    // The startup token check signs in first, then the 401 + failed refresh signs out again.
    await waitFor(async () => expect(await platform.getAuthToken()).toBeNull());
    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
  });

  it('keeps the user signed in when the refresh works', async () => {
    const { api } = fakeApi((_url, init) =>
      new Headers(init.headers).get('Authorization') === 'Bearer fresh' ? json([]) : new Response(null, { status: 401 }),
    );
    const platform = createWebPlatform();
    await platform.setAuthTokens(tokens);
    const refresh = vi.spyOn(platform, 'refreshAuth').mockResolvedValue('fresh');
    renderApp(api, platform, '#/');

    await waitFor(() => expect(refresh).toHaveBeenCalled());
    expect(screen.queryByRole('heading', { name: 'Sign in' })).not.toBeInTheDocument();
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

    await platform.setAuthTokens({ accessToken: 'tok', refreshToken: 'ref' });
    window.location.hash = '#/overlay/s1';
    view.unmount();
    render(
      <AppProvider api={api} platform={platform}>
        <App />
      </AppProvider>,
    );

    // The token check is async, then the route renders; the default 1 s wait was flaky under full-suite load.
    expect(await screen.findByLabelText('Question', undefined, { timeout: 5_000 })).toBeInTheDocument();
    expect(document.documentElement).toHaveClass('overlay');
  });

  it('requires the provider', () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    expect(() => render(<OverlayPage />)).toThrow();
  });
});
