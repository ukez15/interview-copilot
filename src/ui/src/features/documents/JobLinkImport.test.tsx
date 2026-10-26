import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { fakeApi, json, renderWithApp, type Handler } from '@/test/helpers';
import type { DocumentSummary } from '@/types/api';
import { DocumentsPanel } from './DocumentsPanel';

const FALLBACK = "We couldn't read a job description from that page. Paste the text instead.";
const preview = { suggestedName: 'Engineer - Acme.txt', text: 'Build things.', source: 'JsonLd' };

function Harness({ initial = [] }: { initial?: DocumentSummary[] }) {
  const [documents, setDocuments] = useState(initial);
  return <DocumentsPanel sessionId="s1" documents={documents} onChange={setDocuments} />;
}

function doc(i: number): DocumentSummary {
  return { id: `d${i}`, fileName: `f${i}.txt`, characterCount: 1, createdAt: '' };
}

function setup(handler: Handler, initial: DocumentSummary[] = []) {
  const { api, fetchImpl } = fakeApi((url, init) => {
    if (url.endsWith('/documents') && init.method === 'POST') return json({ id: 'new', fileName: 'x', characterCount: 1, createdAt: '' }, 201);
    return handler(url, init);
  });
  renderWithApp(<Harness initial={initial} />, { api });
  const previews = () => fetchImpl.mock.calls.filter(([url]) => String(url).endsWith('/from-link/preview'));
  const uploads = () => fetchImpl.mock.calls.filter(([url, init]) => String(url).endsWith('/documents') && init?.method === 'POST');
  return { fetchImpl, previews, uploads };
}

async function fetchLink(link: string) {
  await userEvent.type(screen.getByLabelText('Job posting link'), link);
  await userEvent.click(screen.getByRole('button', { name: 'Get job description' }));
}

describe('JobLinkImport', () => {
  it('rejects a link that is not http(s) without calling the API', async () => {
    const { fetchImpl } = setup(() => json(preview));

    await fetchLink('ftp://jobs.test/1');
    expect(await screen.findByRole('alert')).toHaveTextContent('Enter a link that starts with http:// or https://.');

    await userEvent.clear(screen.getByLabelText('Job posting link'));
    await fetchLink('not a url');
    expect(screen.getByRole('alert')).toHaveTextContent('Enter a link that starts with http:// or https://.');
    expect(fetchImpl).not.toHaveBeenCalled();
    expect(screen.queryByLabelText('Document name')).not.toBeInTheDocument();
  });

  it('keeps the button disabled while the link is empty', () => {
    setup(() => json(preview));

    expect(screen.getByRole('button', { name: 'Get job description' })).toBeDisabled();
  });

  it('previews, lets the user edit, then uploads the edited text with .txt appended', async () => {
    const { previews, uploads } = setup(() => json(preview));

    await fetchLink('https://jobs.test/1');
    const name = await screen.findByLabelText('Document name');
    const text = screen.getByLabelText('Job description');
    expect(name).toHaveValue('Engineer - Acme.txt');
    expect(text).toHaveValue('Build things.');
    expect(JSON.parse(String(previews()[0]![1]!.body))).toEqual({ url: 'https://jobs.test/1' });
    expect(uploads()).toHaveLength(0);

    await userEvent.clear(name);
    await userEvent.type(name, 'Role at Acme');
    await userEvent.clear(text);
    await userEvent.type(text, 'Edited requirements');
    await userEvent.click(screen.getByRole('button', { name: 'Add to documents' }));

    expect(await screen.findByText('x')).toBeInTheDocument();
    const file = (uploads()[0]![1]!.body as FormData).get('file') as File;
    expect(file.name).toBe('Role at Acme.txt');
    expect(await file.text()).toBe('Edited requirements');
    expect(screen.queryByLabelText('Document name')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Job posting link')).toHaveValue('');
    expect(screen.getByText('Documents (1/10)')).toBeInTheDocument();
  });

  it('keeps an existing .txt extension (any case) as typed', async () => {
    const { uploads } = setup(() => json({ ...preview, suggestedName: 'JD.TXT' }));

    await fetchLink('https://jobs.test/1');
    await screen.findByLabelText('Document name');
    await userEvent.click(screen.getByRole('button', { name: 'Add to documents' }));

    await waitFor(() => expect(uploads()).toHaveLength(1));
    expect(((uploads()[0]![1]!.body as FormData).get('file') as File).name).toBe('JD.TXT');
  });

  it('shows the backend message and opens the paste-instead editor named after the host', async () => {
    const { uploads } = setup(() => json({ detail: 'That page needs a sign-in. Paste the text instead.' }, 400));

    await fetchLink('https://www.jobs.test/view/1');

    expect(await screen.findByRole('alert')).toHaveTextContent('That page needs a sign-in. Paste the text instead.');
    expect(screen.getByLabelText('Document name')).toHaveValue('www.jobs.test.txt');
    const text = screen.getByLabelText('Job description');
    expect(text).toHaveValue('');
    expect(screen.getByRole('button', { name: 'Add to documents' })).toBeDisabled();

    await userEvent.type(text, 'Pasted JD');
    await userEvent.click(screen.getByRole('button', { name: 'Add to documents' }));

    await waitFor(() => expect(uploads()).toHaveLength(1));
    const file = (uploads()[0]![1]!.body as FormData).get('file') as File;
    expect(file.name).toBe('www.jobs.test.txt');
    expect(await file.text()).toBe('Pasted JD');
    await waitFor(() => expect(screen.queryByLabelText('Job description')).not.toBeInTheDocument());
  });

  it('falls back to the default message when the error has none', async () => {
    const { api } = fakeApi(() => json(preview));
    vi.spyOn(api, 'previewJobPosting').mockRejectedValue(new Error(''));
    renderWithApp(<Harness />, { api });

    await fetchLink('https://jobs.test/1');

    expect(await screen.findByRole('alert')).toHaveTextContent(FALLBACK);
    expect(screen.getByLabelText('Job description')).toHaveValue('');
  });

  it('keeps the editor and the text when the upload fails', async () => {
    const { api } = fakeApi((url, init) =>
      url.endsWith('/documents') && init.method === 'POST' ? json({ detail: 'Document limit reached.' }, 400) : json(preview),
    );
    renderWithApp(<Harness />, { api });

    await fetchLink('https://jobs.test/1');
    await screen.findByLabelText('Document name');
    await userEvent.type(screen.getByLabelText('Job description'), ' More.');
    await userEvent.click(screen.getByRole('button', { name: 'Add to documents' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Document limit reached.');
    expect(screen.getByLabelText('Job description')).toHaveValue('Build things. More.');
    expect(screen.getByLabelText('Document name')).toHaveValue('Engineer - Acme.txt');
    expect(screen.getByLabelText('Job posting link')).toHaveValue('https://jobs.test/1');
  });

  it('disables saving when the name or text is blank', async () => {
    setup(() => json(preview));

    await fetchLink('https://jobs.test/1');
    const name = await screen.findByLabelText('Document name');
    const save = screen.getByRole('button', { name: 'Add to documents' });
    expect(save).toBeEnabled();

    await userEvent.clear(name);
    expect(save).toBeDisabled();
    await userEvent.type(name, '   ');
    expect(save).toBeDisabled();
    await userEvent.type(name, 'a');
    expect(save).toBeEnabled();
    await userEvent.clear(screen.getByLabelText('Job description'));
    expect(save).toBeDisabled();
  });

  it('cancel closes the editor, saves nothing and keeps the link', async () => {
    const { uploads } = setup(() => json(preview));

    await fetchLink('https://jobs.test/1');
    await screen.findByLabelText('Document name');
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByLabelText('Document name')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Job description')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Job posting link')).toHaveValue('https://jobs.test/1');
    expect(uploads()).toHaveLength(0);

    await userEvent.click(screen.getByRole('button', { name: 'Get job description' }));
    expect(await screen.findByLabelText('Job description')).toHaveValue('Build things.');
  });

  it('disables the link controls when the session has 10 documents', () => {
    setup(() => json(preview), Array.from({ length: 10 }, (_, i) => doc(i)));

    expect(screen.getByLabelText('Job posting link')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Get job description' })).toBeDisabled();
  });

  it('disables the button and input while the preview request is in flight', async () => {
    let release: (response: Response) => void = () => undefined;
    const { previews } = setup(() => new Promise<Response>((resolve) => (release = resolve)) as unknown as Response);

    await fetchLink('https://jobs.test/1');

    const button = screen.getByRole('button', { name: /Get job description|Reading/ });
    expect(button).toBeDisabled();
    expect(screen.getByLabelText('Job posting link')).toBeDisabled();
    expect(previews()).toHaveLength(1);

    release(json(preview));
    expect(await screen.findByLabelText('Document name')).toBeInTheDocument();
    expect(screen.getByLabelText('Job posting link')).toBeEnabled();
  });
});
