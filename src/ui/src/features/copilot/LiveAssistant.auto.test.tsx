import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createWebPlatform } from '@/platform';
import { FakeWebSocket } from '@/test/fakeWebSocket';
import { fakeApi, json, renderWithApp, sampleSession, sse } from '@/test/helpers';
import { LiveAssistant } from './LiveAssistant';

async function connected() {
  const platform = createWebPlatform();
  await platform.setAuthTokens({ accessToken: 'tok', refreshToken: 'r' });
  return platform;
}

async function openSocket() {
  await waitFor(() => expect(FakeWebSocket.instances.length).toBeGreaterThan(0));
  const socket = FakeWebSocket.last;
  act(() => socket.open());
  return socket;
}

function send(socket: FakeWebSocket, type: string, data: unknown) {
  act(() => socket.emit({ id: 1, type, data }));
}

describe('LiveAssistant automatic answers', () => {
  beforeEach(() => {
    FakeWebSocket.reset();
    vi.stubGlobal('WebSocket', FakeWebSocket);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the detected question and streams the automatic answer', async () => {
    const { api } = fakeApi(() => json({}));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connected() });
    const socket = await openSocket();

    send(socket, 'question.detected', { segmentId: 'g1', text: 'Tell me about a time you led a team.' });
    expect(screen.getByText('Tell me about a time you led a team.')).toBeInTheDocument();
    expect(screen.getByText('Thinking…')).toBeInTheDocument();

    send(socket, 'answer.delta', { token: 'I led ', version: 1, questionType: 'Behavioral' });
    send(socket, 'answer.delta', { token: 'it.', version: 1, questionType: 'Behavioral' });
    expect(screen.getByText('I led it.')).toBeInTheDocument();
    expect(screen.getByText('Behavioral · v1')).toBeInTheDocument();

    send(socket, 'answer.done', { version: 1, questionType: 'Behavioral', answerId: 'a1' });
    expect(screen.getByRole('button', { name: /Shorter/ })).toBeEnabled();
  });

  it('replaces the answer when a newer question is detected', async () => {
    const { api } = fakeApi(() => json({}));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connected() });
    const socket = await openSocket();

    send(socket, 'question.detected', { segmentId: 'g1', text: 'First question here?' });
    send(socket, 'answer.delta', { token: 'old', version: 1, questionType: 'General' });
    send(socket, 'question.detected', { segmentId: 'g2', text: 'Second question here?' });
    send(socket, 'answer.delta', { token: 'new', version: 1, questionType: 'General' });

    expect(screen.queryByText('First question here?')).not.toBeInTheDocument();
    expect(screen.getByText('Second question here?')).toBeInTheDocument();
    expect(screen.getByText('new')).toBeInTheDocument();
  });

  it('shows an error event and ignores answer events when no automatic answer is running', async () => {
    const { api } = fakeApi(() => json({}));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connected() });
    const socket = await openSocket();

    send(socket, 'answer.delta', { token: 'stray', version: 1, questionType: 'General' });
    send(socket, 'error', { message: 'Part of the conversation couldn’t be transcribed.' });
    expect(screen.queryByText('stray')).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();

    send(socket, 'question.detected', { segmentId: 'g1', text: 'Why do you want this job?' });
    send(socket, 'error', { message: "Can't reach Ollama." });
    expect(screen.getByRole('alert')).toHaveTextContent("Can't reach Ollama.");
  });

  it('does not double up with the events a manual answer also publishes', async () => {
    const { api } = fakeApi(() => sse(['meta', '{"questionType":"Behavioral","version":1}'], ['delta', 'Typed'], ['done', '']));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connected() });
    const socket = await openSocket();

    await userEvent.type(screen.getByLabelText('Question'), 'Typed question here');
    await userEvent.click(screen.getByRole('button', { name: 'Answer' }));
    expect(await screen.findByText('Typed')).toBeInTheDocument();
    send(socket, 'answer.delta', { token: 'Typed', version: 1, questionType: 'Behavioral' });
    send(socket, 'answer.done', { version: 1, questionType: 'Behavioral', answerId: 'a1' });

    expect(screen.getByText('Typed')).toBeInTheDocument();
  });

  it('pauses and resumes auto-answer through the API', async () => {
    const { api, fetchImpl } = fakeApi((url, init) => {
      const body = JSON.parse(String(init.body)) as { enabled: boolean };
      expect(url).toBe('http://api.test/api/sessions/s1/auto-answer');
      expect(init.method).toBe('PUT');
      return json({ ...sampleSession, autoAnswer: body.enabled });
    });
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connected() });
    await openSocket();

    await userEvent.click(screen.getByRole('button', { name: 'Pause auto-answer' }));
    expect(await screen.findByRole('button', { name: 'Resume auto-answer' })).toBeInTheDocument();
    expect(JSON.parse(String(fetchImpl.mock.calls[0]![1]!.body))).toEqual({ enabled: false });

    await userEvent.click(screen.getByRole('button', { name: 'Resume auto-answer' }));
    expect(await screen.findByRole('button', { name: 'Pause auto-answer' })).toBeInTheDocument();
  });

  it('follows autoanswer.state events from other windows and the initial value', async () => {
    const { api } = fakeApi(() => json({}));
    renderWithApp(<LiveAssistant sessionId="s1" autoAnswer={false} />, { api, platform: await connected() });
    const socket = await openSocket();
    expect(screen.getByRole('button', { name: 'Resume auto-answer' })).toBeInTheDocument();

    send(socket, 'autoanswer.state', { enabled: true });
    expect(screen.getByRole('button', { name: 'Pause auto-answer' })).toBeInTheDocument();
  });

  it('reports a failed pause request', async () => {
    const { api } = fakeApi(() => json({ detail: 'Session not found.' }, 404));
    renderWithApp(<LiveAssistant sessionId="s1" />, { api, platform: await connected() });
    await openSocket();

    await userEvent.click(screen.getByRole('button', { name: 'Pause auto-answer' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Session not found.');
    expect(screen.getByRole('button', { name: 'Pause auto-answer' })).toBeInTheDocument();
  });

  it('shows a detected question without answering while paused, and answers it on request', async () => {
    const { api, fetchImpl } = fakeApi(() => sse(['meta', '{"questionType":"Behavioral","version":1}'], ['delta', 'Manual.'], ['done', '']));
    renderWithApp(<LiveAssistant sessionId="s1" autoAnswer={false} />, { api, platform: await connected() });
    const socket = await openSocket();

    send(socket, 'question.detected', { segmentId: 'g1', text: 'Walk me through your resume.' });
    expect(screen.getByText('Walk me through your resume.')).toBeInTheDocument();
    expect(screen.queryByText('Thinking…')).not.toBeInTheDocument();
    expect(fetchImpl).not.toHaveBeenCalled();

    await userEvent.click(screen.getByRole('button', { name: 'Answer it' }));

    expect(await screen.findByText('Manual.')).toBeInTheDocument();
    expect(JSON.parse(String(fetchImpl.mock.calls[0]![1]!.body))).toMatchObject({ question: 'Walk me through your resume.' });
    expect(screen.queryByRole('button', { name: 'Answer it' })).not.toBeInTheDocument();
  });
});
