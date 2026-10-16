import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { fakeApi, json, renderWithApp, sampleSession } from '@/test/helpers';
import { SessionPage } from './SessionPage';
import { SessionsPage } from './SessionsPage';

const route = { route: '/sessions/s1', path: '/sessions/:sessionId' };

function setup(status: 'Draft' | 'Live' | 'Ended', putResponse?: Response) {
  const session = { ...sampleSession, status, sessionPrompt: 'Panel of two' };
  const { api, fetchImpl } = fakeApi((_, init) => (init.method === 'PUT' ? (putResponse ?? json(session)) : json(session)));
  renderWithApp(<SessionPage />, { api, ...route });
  return { fetchImpl };
}

function putBody(fetchImpl: ReturnType<typeof setup>['fetchImpl']) {
  const call = fetchImpl.mock.calls.find((c) => c[1]?.method === 'PUT')!;
  expect(String(call[0])).toContain('/api/sessions/s1/settings');
  return JSON.parse(String(call[1]!.body)) as Record<string, unknown>;
}

describe.each(['Draft', 'Live'] as const)('session settings editing (%s)', (status) => {
  it('pre-fills, hides title and mode, saves and shows the returned session', async () => {
    const saved = { ...sampleSession, status, answerLength: 'Long' as const, framework: 'Smart' as const, pinnedQuestionType: null, sessionPrompt: 'New prompt' };
    const { fetchImpl } = setup(status, json(saved));

    await screen.findByText('Acme onsite');
    expect(screen.queryByLabelText('Title')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Mode')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Answer length')).toHaveValue('Short');
    expect(screen.getByLabelText('Answer framework')).toHaveValue('Star');
    expect(screen.getByLabelText(/Pin a question type/)).toHaveValue('Behavioral');
    expect(screen.getByLabelText('Session prompt')).toHaveValue('Panel of two');

    await userEvent.selectOptions(screen.getByLabelText('Answer length'), 'Long');
    await userEvent.selectOptions(screen.getByLabelText('Answer framework'), 'Smart');
    await userEvent.selectOptions(screen.getByLabelText(/Pin a question type/), '');
    await userEvent.clear(screen.getByLabelText('Session prompt'));
    await userEvent.type(screen.getByLabelText('Session prompt'), 'New prompt');
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }));

    expect(await screen.findByText('Settings saved. They apply to the next answer.')).toBeInTheDocument();
    expect(putBody(fetchImpl)).toEqual({ answerLength: 'Long', framework: 'Smart', pinnedQuestionType: null, sessionPrompt: 'New prompt' });
    expect(screen.getByText(/Long answers/)).toBeInTheDocument();
  });

  it('shows the backend error', async () => {
    setup(status, json({ detail: 'Prompt too long' }, 400));

    await screen.findByText('Acme onsite');
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Prompt too long');
    expect(screen.queryByText(/Settings saved/)).not.toBeInTheDocument();
  });
});

describe('session settings when Ended', () => {
  it('offers no edit control', async () => {
    setup('Ended');

    await screen.findByText('Acme onsite');
    expect(screen.queryByRole('button', { name: 'Save settings' })).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Answer length')).not.toBeInTheDocument();
  });
});

describe('create mode', () => {
  it('still shows title and mode and has no Save settings button', async () => {
    const { api } = fakeApi(() => json([]));
    renderWithApp(<SessionsPage />, { api });

    expect(await screen.findByLabelText('Title')).toBeInTheDocument();
    expect(screen.getByLabelText('Mode')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create session' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save settings' })).not.toBeInTheDocument();
  });
});
