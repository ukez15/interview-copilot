/** The stored refresh token, as far as the refresh logic needs it. */
export interface RefreshTokenStore {
  get(): Promise<string | null>;
  set(refreshToken: string): Promise<void>;
  clear(): Promise<void>;
}

/**
 * Exchanges the stored refresh token for a new access token (#8). A 401 means the token is expired or
 * invalid, so it is cleared; a network error or other failure keeps it so a later attempt can succeed.
 */
export async function refreshAccessToken(
  store: RefreshTokenStore,
  backendUrl: string,
  fetchImpl: typeof fetch = fetch,
): Promise<string | null> {
  const refreshToken = await store.get();
  if (!refreshToken) {
    return null;
  }

  let response: Response;
  try {
    response = await fetchImpl(`${backendUrl}/api/auth/refresh`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken }),
    });
  } catch {
    return null;
  }

  if (response.status === 401) {
    await store.clear();
    return null;
  }
  if (!response.ok) {
    return null;
  }

  try {
    const body = (await response.json()) as { accessToken?: unknown; refreshToken?: unknown };
    if (typeof body.accessToken !== 'string' || typeof body.refreshToken !== 'string') {
      return null;
    }
    await store.set(body.refreshToken);
    return body.accessToken;
  } catch {
    return null;
  }
}
