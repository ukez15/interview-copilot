import { SseParser } from './sse';
import type {
  AnswerHeader,
  AnswerModifier,
  CreateSessionInput,
  DocumentSummary,
  Providers,
  Session,
  StorageInfo,
  SessionSettingsInput,
  Transcript,
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

/** Typed client for the InterviewCopilot backend. Same client serves Mode C with a different base URL. */
export class ApiClient {
  constructor(
    private readonly baseUrl: string = DEFAULT_API_BASE_URL,
    private readonly fetchImpl: FetchLike = (...args) => fetch(...args),
    private token: string | null = null,
  ) {}

  setToken(token: string | null): void {
    this.token = token;
  }

  async register(username: string, password: string): Promise<void> {
    await this.send('/api/auth/register', { method: 'POST', body: JSON.stringify({ username, password }) });
  }

  async signIn(username: string, password: string): Promise<string> {
    const result = await this.json<{ accessToken: string }>('/api/auth/sign-in', {
      method: 'POST',
      body: JSON.stringify({ username, password }),
    });
    this.token = result.accessToken;
    return result.accessToken;
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
    const headers = new Headers(init.headers);
    if (init.body && !(init.body instanceof FormData)) {
      headers.set('Content-Type', 'application/json');
    }
    if (this.token) {
      headers.set('Authorization', `Bearer ${this.token}`);
    }

    let response: Response;
    try {
      response = await this.fetchImpl(`${this.baseUrl}${path}`, { ...init, headers });
    } catch {
      throw new ApiError(0, "Can't reach the InterviewCopilot backend. Is the desktop app running?");
    }

    if (!response.ok) {
      throw new ApiError(response.status, await problemMessage(response));
    }
    return response;
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
