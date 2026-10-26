import { render } from '@testing-library/react';
import type { ReactElement } from 'react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { vi } from 'vitest';
import { ApiClient } from '@/lib/api';
import { AppProvider } from '@/lib/app-context';
import { createWebPlatform, type Platform } from '@/platform';
import type { Session } from '@/types/api';

export type Handler = (url: string, init: RequestInit) => Response | Promise<Response>;

export function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

export function sse(...events: [string, string][]): Response {
  const text = events.map(([event, data]) => `event: ${event}\ndata: ${data}\n\n`).join('');
  return new Response(text, { headers: { 'Content-Type': 'text/event-stream' } });
}

/** ApiClient backed by a fake fetch; records every call. */
export function fakeApi(handler: Handler) {
  const fetchImpl = vi.fn((input: RequestInfo | URL, init?: RequestInit) => Promise.resolve(handler(String(input), init ?? {})));
  return { api: new ApiClient('http://api.test', fetchImpl as unknown as typeof fetch, 'token'), fetchImpl };
}

export function renderWithApp(
  ui: ReactElement,
  { api, platform = createWebPlatform(), route = '/', path = '/' }: { api: ApiClient; platform?: Platform; route?: string; path?: string },
) {
  return render(
    <AppProvider api={api} platform={platform}>
      <MemoryRouter initialEntries={[route]}>
        <Routes>
          <Route path={path} element={ui} />
          <Route path="*" element={<div>navigated</div>} />
        </Routes>
      </MemoryRouter>
    </AppProvider>,
  );
}

export const sampleSession: Session = {
  id: 's1',
  title: 'Acme onsite',
  mode: 'LiveInterview',
  status: 'Draft',
  answerLength: 'Short',
  framework: 'Star',
  pinnedQuestionType: 'Behavioral',
  sessionPrompt: '',
  createdAt: '2026-10-15T09:00:00Z',
  startedAt: null,
  endedAt: null,
  documents: [{ id: 'd1', fileName: 'resume.txt', characterCount: 10, createdAt: '2026-10-15T09:00:00Z' }],
  autoAnswer: true,
};
