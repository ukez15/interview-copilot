import { SseParser } from './sse';
import type {
  AnswerHeader,
  AnswerModifier,
  CreateSessionInput,
  DocumentSummary,
  OllamaModel,
  Preferences,
  Providers,
  Session,
  StorageInfo,
  SessionSettingsInput,
  Transcript,
  WhisperModel,
} from '@/types/api';

export const DEFAULT_API_BASE_URL = 'http://127.0.0.1:5480';

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

export interface AnswerStreamHandlers {
  onHeader: (header: AnswerHeader) => void;
  onToken: (token: string) => void;
  onError: (message: string) => void;
  onDone: () => void;
}

export interface AskInput {
  question: string;
  modifier?: AnswerModifier;
  previousAnswer?: string;
  previousVersion?: number;
  /** BYOK key from the OS keychain (desktop) — sent per request, never stored by the backend. */
  llmApiKey?: string;
}

type FetchLike = typeof fetch;

export interface SignInTokens {
  accessToken: string;
  refreshToken: string;
}

/** How the client recovers from an expired access token (#8). */
export interface AuthHandlers {
  /** Returns a fresh access token, or null when the sign-in can't be renewed. */
  refresh: () => Promise<string | null>;
  /** Called when a 401 couldn't be recovered, so the app can show the sign-in page. */
  signedOut: () => void;
}

/** Typed client for the InterviewCopilot backend. Same client serves Mode C with a different base URL. */
export class ApiClient {
  constructor(
    private readonly baseUrl: string = DEFAULT_API_BASE_URL,
    private readonly fetchImpl: FetchLike = (...args) => fetch(...args),
    private token: string | null = null,
  ) {}

  private handlers: AuthHandlers | null = null;
  private refreshing: Promise<string | null> | null = null;

  setToken(token: string | null): void {
    this.token = token;
  }

  setAuthHandlers(handlers: AuthHandlers | null): void {
    this.handlers = handlers;
  }

  async register(username: string, password: string): Promise<void> {
    await this.send('/api/auth/register', { method: 'POST', body: JSON.stringify({ username, password }) });
  }

  async signIn(username: string, password: string): Promise<SignInTokens> {
    const result = await this.json<SignInTokens>('/api/auth/sign-in', {
      method: 'POST',
      body: JSON.stringify({ username, password }),
    });
    this.token = result.accessToken;
    return { accessToken: result.accessToken, refreshToken: result.refreshToken };
  }

  listSessions(): Promise<Session[]> {
    return this.json('/api/sessions');
  }

  getSession(id: string): Promise<Session> {
    return this.json(`/api/sessions/${id}`);
  }

  createSession(input: CreateSessionInput): Promise<Session> {
    return this.json('/api/sessions', { method: 'POST', body: JSON.stringify(input) });
  }

  updateSessionSettings(id: string, input: SessionSettingsInput): Promise<Session> {
    return this.json(`/api/sessions/${id}/settings`, { method: 'PUT', body: JSON.stringify(input) });
  }

  /** #22: pause or resume automatic answers; the backend also announces it as `autoanswer.state`. */
  setAutoAnswer(id: string, enabled: boolean): Promise<Session> {
    return this.json(`/api/sessions/${id}/auto-answer`, { method: 'PUT', body: JSON.stringify({ enabled }) });
  }

  /** WebSocket URL of a session's events channel (same host as the API). The access token is added by the caller. */
  eventsUrl(sessionId: string): string {
    return `${this.baseUrl.replace(/^http/, 'ws')}/ws/sessions/${sessionId}/events`;
  }

  getTranscript(id: string): Promise<Transcript> {
    return this.json(`/api/sessions/${id}/transcript`);
  }

  startSession(id: string): Promise<Session> {
    return this.json(`/api/sessions/${id}/start`, { method: 'POST' });
  }

  endSession(id: string): Promise<Session> {
    return this.json(`/api/sessions/${id}/end`, { method: 'POST' });
  }

  uploadDocument(sessionId: string, file: File): Promise<DocumentSummary> {
    const form = new FormData();
    form.append('file', file);
    return this.json(`/api/sessions/${sessionId}/documents`, { method: 'POST', body: form });
  }

  async removeDocument(sessionId: string, documentId: string): Promise<void> {
    await this.send(`/api/sessions/${sessionId}/documents/${documentId}`, { method: 'DELETE' });
  }

  providers(): Promise<Providers> {
    return this.json('/api/providers');
  }

  getStorage(): Promise<StorageInfo> {
    return this.json('/api/storage');
  }

  getWhisperModel(): Promise<WhisperModel> {
    return this.json('/api/providers/whisper/model');
  }

  /** Starts the (user-initiated) speech model download; 409 means one is already running. */
  downloadWhisperModel(): Promise<WhisperModel> {
    return this.json('/api/providers/whisper/model/download', { method: 'POST' });
  }

  ollamaModels(): Promise<OllamaModel[]> {
    return this.json('/api/providers/ollama/models');
  }

  getPreferences(): Promise<Preferences> {
    return this.json('/api/preferences');
  }

  setLlmModel(llmModel: string | null): Promise<Preferences> {
    return this.json('/api/preferences', { method: 'PUT', body: JSON.stringify({ llmModel }) });
  }

  transcriptPdfUrl(sessionId: string): string {
    return `${this.baseUrl}/api/sessions/${sessionId}/transcript/pdf`;
  }

  async downloadTranscriptPdf(sessionId: string): Promise<Blob> {
    const response = await this.send(`/api/sessions/${sessionId}/transcript/pdf`);
    return response.blob();
  }

  /** Streams a suggested answer (FR-09/FR-10). Resolves when the stream ends. */
  async ask(sessionId: string, input: AskInput, handlers: AnswerStreamHandlers, signal?: AbortSignal): Promise<void> {
    const headers: Record<string, string> = {};
    if (input.llmApiKey) {
      headers['X-Llm-Api-Key'] = input.llmApiKey;
    }
    const response = await this.send(`/api/sessions/${sessionId}/answers`, {
      method: 'POST',
      headers,
      signal,
      body: JSON.stringify({
        question: input.question,
        modifier: input.modifier ?? 'None',
        previousAnswer: input.previousAnswer ?? null,
        previousVersion: input.previousVersion ?? 0,
      }),
    });

    if (!response.body) {
      throw new ApiError(response.status, 'The answer stream was empty.');
    }

    const parser = new SseParser();
    const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
    for (;;) {
      const { value, done } = await reader.read();
      if (done) {
        return;
      }
      for (const event of parser.push(value)) {
        switch (event.event) {
          case 'meta':
            handlers.onHeader(JSON.parse(event.data) as AnswerHeader);
            break;
          case 'delta':
            handlers.onToken(event.data);
            break;
          case 'error':
            handlers.onError(event.data);
            break;
          case 'done':
            handlers.onDone();
            break;
        }
      }
    }
  }

  private async json<T>(path: string, init: RequestInit = {}): Promise<T> {
    const response = await this.send(path, init);
    return (await response.json()) as T;
  }

  private async send(path: string, init: RequestInit = {}): Promise<Response> {
    const response = await this.attempt(path, init);
    const recoverable = response.status === 401 && this.handlers !== null && !path.startsWith('/api/auth/');
    if (!recoverable) {
      return this.checked(response);
    }

    // One refresh, one retry; if either fails the user goes back to the sign-in page.
    const token = await this.refreshOnce();
    if (token) {
      this.token = token;
      const retry = await this.attempt(path, init);
      if (retry.status !== 401) {
        return this.checked(retry);
      }
    }
    this.token = null;
    this.handlers?.signedOut();
    return this.checked(response);
  }

  private refreshOnce(): Promise<string | null> {
    this.refreshing ??= (this.handlers?.refresh() ?? Promise.resolve(null))
      .catch(() => null)
      .finally(() => (this.refreshing = null));
    return this.refreshing;
  }

  private async checked(response: Response): Promise<Response> {
    if (!response.ok) {
      throw new ApiError(response.status, await problemMessage(response));
    }
    return response;
  }

  private async attempt(path: string, init: RequestInit): Promise<Response> {
    const headers = new Headers(init.headers);
    if (init.body && !(init.body instanceof FormData)) {
      headers.set('Content-Type', 'application/json');
    }
    if (this.token) {
      headers.set('Authorization', `Bearer ${this.token}`);
    }

    try {
      return await this.fetchImpl(`${this.baseUrl}${path}`, { ...init, headers });
    } catch {
      throw new ApiError(0, "Can't reach the InterviewCopilot backend. Is the desktop app running?");
    }
  }
}

async function problemMessage(response: Response): Promise<string> {
  if (response.status === 401) {
    return 'Please sign in again.';
  }
  try {
    const problem = (await response.json()) as { detail?: string; title?: string };
    return problem.detail ?? problem.title ?? `Request failed (${response.status}).`;
  } catch {
    return `Request failed (${response.status}).`;
  }
}
