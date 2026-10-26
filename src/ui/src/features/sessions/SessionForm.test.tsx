import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { fakeApi, json, renderWithApp, type Handler } from '@/test/helpers';
import type { CreateSessionInput } from '@/types/api';
import { SessionForm } from './SessionForm';

const LINK = 'https://jobs.test/roles/1';
const preview = { suggestedName: 'Senior Engineer - Acme.txt', text: 'Build things.', source: 'JsonLd' };

function setup(handler: Handler = () => json(preview), variant: 'create' | 'edit' = 'create') {
  const { api, fetchImpl } = fakeApi(handler);
  const onSubmit = vi.fn<(input: CreateSessionInput, jobDescription?: File) => void>();
  const initial: CreateSessionInput | undefined =
    variant === 'edit'
      ? { title: 'Acme', mode: 'LiveInterview', answerLength: 'Short', framework: 'Star', pinnedQuestionType: null, sessionPrompt: '' }
      : undefined;
  renderWithApp(<SessionForm onSubmit={onSubmit} variant={variant} initial={initial} />, { api });
  const previews = () => fetchImpl.mock.calls.filter(([url]) => String(url).endsWith('/api/documents/from-link/preview'));
  return { fetchImpl, onSubmit, previews };
}

const titleField = () => screen.getByLabelText('Title') as HTMLInputElement;
const createButton = () => screen.getByRole('button', { name: 'Create session' });

function paste(text: string) {
  fireEvent.paste(titleField(), { clipboardData: { getData: () => text } });
}

/** Selects the whole title, as ctrl+A would, then pastes. */
function pasteOverAll(text: string) {
  titleField().setSelectionRange(0, titleField().value.length);
  paste(text);
}

describe('SessionForm paste a job link into the title', () => {
  it('fetches on paste into an empty title, names the session from the posting and shows the editor', async () => {
    const { previews } = setup();

    paste(LINK);

    expect(titleField()).toHaveValue(LINK);
    expect(await screen.findByLabelText('Job description')).toHaveValue('Build things.');
    expect(titleField()).toHaveValue('Senior Engineer - Acme');
    expect(screen.getByLabelText('Document name')).toHaveValue('Senior Engineer - Acme.txt');
    expect(JSON.parse(String(previews()[0]![1]!.body))).toEqual({ url: LINK });
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Cancel' })).toHaveLength(1);
    expect(screen.queryByRole('button', { name: 'Add to documents' })).not.toBeInTheDocument();
  });

  it('works with a real paste action too', async () => {
    const { previews } = setup();

    await userEvent.click(titleField());
    await userEvent.paste(`  ${LINK}\n`);

    expect(await screen.findByLabelText('Job description')).toBeInTheDocument();
    expect(previews()).toHaveLength(1);
  });

  it('fetches when the whole title is selected', async () => {
    const { previews } = setup();
    await userEvent.type(titleField(), 'Old title');

    pasteOverAll(LINK);

    expect(await screen.findByLabelText('Job description')).toBeInTheDocument();
    expect(previews()).toHaveLength(1);
    expect(titleField()).toHaveValue('Senior Engineer - Acme');
  });

  it('submits a file with the edited name and text', async () => {
    const { onSubmit } = setup();
    paste(LINK);
    const name = await screen.findByLabelText('Document name');
    await userEvent.clear(name);
    await userEvent.type(name, 'Role at Acme');
    const text = screen.getByLabelText('Job description');
    await userEvent.clear(text);
    await userEvent.type(text, 'Edited requirements');

    await userEvent.click(createButton());

    expect(onSubmit).toHaveBeenCalledTimes(1);
    const [input, file] = onSubmit.mock.calls[0]!;
    expect(input.title).toBe('Senior Engineer - Acme');
    expect(file!.name).toBe('Role at Acme.txt');
    expect(file!.type).toBe('text/plain');
    expect(await file!.text()).toBe('Edited requirements');
  });

  it('does not fetch when plain text is pasted', () => {
    const { fetchImpl } = setup();

    paste('Senior engineer at Acme');
    paste('ftp://jobs.test/1');
    paste('see ' + LINK);

    expect(fetchImpl).not.toHaveBeenCalled();
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Job description')).not.toBeInTheDocument();
  });

  it('lets a plain-text paste land in the title as usual', async () => {
    setup();

    await userEvent.click(titleField());
    await userEvent.paste('Acme onsite');

    expect(titleField()).toHaveValue('Acme onsite');
  });

  it('does not fetch when a link is pasted into a non-empty title that is not fully selected', async () => {
    const { fetchImpl } = setup();
    await userEvent.type(titleField(), 'Acme');

    titleField().setSelectionRange(1, 3);
    paste(LINK);
    titleField().setSelectionRange(4, 4);
    paste(LINK);

    expect(fetchImpl).not.toHaveBeenCalled();
    expect(titleField()).toHaveValue('Acme');
  });

  it('shows a status line and disables Create while fetching', async () => {
    let release: (response: Response) => void = () => undefined;
    setup(() => new Promise<Response>((resolve) => (release = resolve)) as unknown as Response);

    paste(LINK);

    expect(await screen.findByRole('status')).toHaveTextContent('Getting job description…');
    expect(createButton()).toBeDisabled();

    release(json(preview));
    expect(await screen.findByLabelText('Job description')).toBeInTheDocument();
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
    expect(createButton()).toBeEnabled();
  });

  it('shows the error, an empty editor named after the host and keeps the pasted link as the title', async () => {
    const { onSubmit } = setup(() => json({ detail: 'That page needs a sign-in. Paste the text instead.' }, 400));

    paste('https://www.jobs.test/view/1');

    expect(await screen.findByRole('alert')).toHaveTextContent('That page needs a sign-in. Paste the text instead.');
    expect(screen.getByLabelText('Document name')).toHaveValue('www.jobs.test.txt');
    expect(screen.getByLabelText('Job description')).toHaveValue('');
    expect(titleField()).toHaveValue('https://www.jobs.test/view/1');

    await userEvent.click(createButton());
    expect(onSubmit.mock.calls[0]![1]).toBeUndefined();
  });

  it('pastes the text instead after a failure and submits it as the file', async () => {
    const { onSubmit } = setup(() => json({ detail: 'Nope.' }, 400));
    paste(LINK);
    await userEvent.type(await screen.findByLabelText('Job description'), 'Pasted JD');

    await userEvent.click(createButton());

    const file = onSubmit.mock.calls[0]![1]!;
    expect(file.name).toBe('jobs.test.txt');
    expect(await file.text()).toBe('Pasted JD');
  });

  it('falls back to the default message when the error has none', async () => {
    const { api } = fakeApi(() => json(preview));
    vi.spyOn(api, 'previewJobPostingLink').mockRejectedValue(new Error(''));
    renderWithApp(<SessionForm onSubmit={vi.fn()} />, { api });

    paste(LINK);

    expect(await screen.findByRole('alert')).toHaveTextContent("We couldn't read a job description from that page. Paste the text instead.");
  });

  it('cancel drops the draft and the error, keeps the title and submits no file', async () => {
    const { onSubmit } = setup(() => json({ detail: 'Nope.' }, 400));
    paste(LINK);
    await screen.findByRole('alert');

    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByLabelText('Job description')).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(titleField()).toHaveValue(LINK);

    await userEvent.click(createButton());
    expect(onSubmit.mock.calls[0]![0].title).toBe(LINK);
    expect(onSubmit.mock.calls[0]![1]).toBeUndefined();
  });

  it('submits no file when the editor text is blank', async () => {
    const { onSubmit } = setup();
    paste(LINK);
    await userEvent.clear(await screen.findByLabelText('Job description'));
    await userEvent.type(screen.getByLabelText('Job description'), '   ');

    await userEvent.click(createButton());

    expect(onSubmit).toHaveBeenCalledTimes(1);
    expect(onSubmit.mock.calls[0]![1]).toBeUndefined();
  });

  it('falls back to "Job description.txt" when the name is blank', async () => {
    const { onSubmit } = setup();
    paste(LINK);
    await userEvent.clear(await screen.findByLabelText('Document name'));

    await userEvent.click(createButton());

    expect(onSubmit.mock.calls[0]![1]!.name).toBe('Job description.txt');
  });

  it('keeps an existing .txt extension as typed', async () => {
    const { onSubmit } = setup(() => json({ ...preview, suggestedName: 'JD.TXT' }));
    paste(LINK);
    await screen.findByLabelText('Job description');

    await userEvent.click(createButton());

    expect(onSubmit.mock.calls[0]![1]!.name).toBe('JD.TXT');
    expect(titleField()).toHaveValue('JD');
  });

  it('truncates a suggested title to the 200 character limit', async () => {
    setup(() => json({ ...preview, suggestedName: `${'a'.repeat(250)}.txt` }));

    paste(LINK);

    await waitFor(() => expect(titleField()).toHaveValue('a'.repeat(200)));
  });

  it('ignores a response for a link that was replaced by a newer paste', async () => {
    const releases: ((response: Response) => void)[] = [];
    setup(() => new Promise<Response>((resolve) => releases.push(resolve)) as unknown as Response);

    paste(LINK);
    await waitFor(() => expect(releases).toHaveLength(1));
    pasteOverAll('https://other.test/2');
    await waitFor(() => expect(releases).toHaveLength(2));

    releases[0]!(json({ ...preview, suggestedName: 'First.txt' }));
    await Promise.resolve();
    expect(screen.getByRole('status')).toBeInTheDocument();
    expect(titleField()).toHaveValue('https://other.test/2');

    releases[1]!(json({ ...preview, suggestedName: 'Second.txt' }));
    expect(await screen.findByLabelText('Document name')).toHaveValue('Second.txt');
    expect(titleField()).toHaveValue('Second');
  });

  it('ignores a response that arrives after the user cancelled', async () => {
    const releases: ((response: Response) => void)[] = [];
    let calls = 0;
    // The first request answers at once; the second hangs until the test releases it.
    const { onSubmit } = setup(() =>
      calls++ === 0 ? json(preview) : (new Promise<Response>((resolve) => releases.push(resolve)) as unknown as Response),
    );
    paste(LINK);
    await screen.findByLabelText('Job description');

    pasteOverAll('https://other.test/2');
    await screen.findByRole('status');
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
    expect(createButton()).toBeEnabled();

    releases[0]!(json({ ...preview, suggestedName: 'Late.txt' }));
    await Promise.resolve();
    await Promise.resolve();

    expect(screen.queryByLabelText('Job description')).not.toBeInTheDocument();
    expect(titleField()).toHaveValue('https://other.test/2');
    await userEvent.click(createButton());
    expect(onSubmit.mock.calls[0]![1]).toBeUndefined();
  });

  it('has no paste behaviour in the edit variant', () => {
    const { fetchImpl } = setup(undefined, 'edit');

    expect(screen.queryByLabelText('Title')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save settings' })).toBeInTheDocument();
    expect(fetchImpl).not.toHaveBeenCalled();
  });

  it('still submits just the settings from the edit variant', async () => {
    const { onSubmit } = setup(undefined, 'edit');

    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }));

    expect(onSubmit).toHaveBeenCalledTimes(1);
    expect(onSubmit.mock.calls[0]).toHaveLength(1);
  });
});
