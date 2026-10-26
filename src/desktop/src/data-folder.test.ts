import path from 'node:path';
import { describe, expect, it, vi } from 'vitest';
import { openDataFolder } from './data-folder';

const FOLDER = path.resolve('data', 'InterviewCopilot');

function setup(response: Partial<Response> | Error, openResult = '', token: string | null = 'tok') {
  const fetchImpl = vi.fn(() => (response instanceof Error ? Promise.reject(response) : Promise.resolve(response as Response)));
  const openPath = vi.fn(() => Promise.resolve(openResult));
  const run = () => openDataFolder({ backendUrl: 'http://127.0.0.1:5480', getToken: () => token, fetchImpl, openPath });
  return { fetchImpl, openPath, run };
}

const ok = (body: unknown): Partial<Response> => ({ ok: true, json: () => Promise.resolve(body) });

describe('openDataFolder', () => {
  it('asks the backend for the path with the auth token and opens exactly that path', async () => {
    const { fetchImpl, openPath, run } = setup(ok({ dataDirectory: FOLDER }));

    await run();

    expect(fetchImpl).toHaveBeenCalledWith('http://127.0.0.1:5480/api/storage', { headers: { Authorization: 'Bearer tok' } });
    expect(openPath).toHaveBeenCalledWith(FOLDER);
  });

  it('fails when the backend is unreachable', async () => {
    const { openPath, run } = setup(new Error('ECONNREFUSED'));

    await expect(run()).rejects.toThrow("Can't reach the local backend.");
    expect(openPath).not.toHaveBeenCalled();
  });

  it('fails when the backend rejects the request', async () => {
    const { openPath, run } = setup({ ok: false, status: 401 });

    await expect(run()).rejects.toThrow("Can't reach the local backend.");
    expect(openPath).not.toHaveBeenCalled();
  });

  it('fails when not signed in', async () => {
    const { fetchImpl, run } = setup(ok({ dataDirectory: FOLDER }), '', null);

    await expect(run()).rejects.toThrow('Sign in first.');
    expect(fetchImpl).not.toHaveBeenCalled();
  });

  it.each([{ dataDirectory: '' }, { dataDirectory: 'relative/folder' }, { dataDirectory: 42 }, {}, null])('rejects a bad path %j', async (body) => {
    const { openPath, run } = setup(ok(body));

    await expect(run()).rejects.toThrow('The backend returned an invalid data folder.');
    expect(openPath).not.toHaveBeenCalled();
  });

  it("surfaces the shell's error when the folder can't be opened", async () => {
    const { run } = setup(ok({ dataDirectory: FOLDER }), 'Failed to open path');

    await expect(run()).rejects.toThrow("Couldn't open the data folder: Failed to open path");
  });
});
