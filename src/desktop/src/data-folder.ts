import path from 'node:path';

export interface DataFolderDeps {
  backendUrl: string;
  getToken: () => string | null;
  fetchImpl: (url: string, init: { headers: Record<string, string> }) => Promise<Pick<Response, 'ok' | 'json'>>;
  /** Electron's shell.openPath: resolves to '' on success, otherwise an error message. */
  openPath: (folder: string) => Promise<string>;
}

/**
 * Opens the backend's data folder in the OS file manager. The path always comes from the backend
 * (GET /api/storage), never from the renderer.
 */
export async function openDataFolder({ backendUrl, getToken, fetchImpl, openPath }: DataFolderDeps): Promise<void> {
  const token = getToken();
  if (!token) {
    throw new Error('Sign in first.');
  }

  let folder: unknown;
  try {
    const response = await fetchImpl(`${backendUrl}/api/storage`, { headers: { Authorization: `Bearer ${token}` } });
    if (!response.ok) {
      throw new Error('Request failed.');
    }
    folder = ((await response.json()) as { dataDirectory?: unknown } | null)?.dataDirectory;
  } catch {
    throw new Error("Can't reach the local backend.");
  }

  if (typeof folder !== 'string' || !path.isAbsolute(folder)) {
    throw new Error('The backend returned an invalid data folder.');
  }

  const failure = await openPath(folder);
  if (failure) {
    throw new Error(`Couldn't open the data folder: ${failure}`);
  }
}
