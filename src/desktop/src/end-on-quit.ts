/** How long quitting waits for the backend to end the session before giving up. */
export const END_ON_QUIT_TIMEOUT_MS = 2_000;

export interface EndOnQuitInput {
  sessionId: string | null;
  token: string | null;
  backendUrl: string;
  fetchImpl?: typeof fetch;
}

/**
 * Ends the live session as the app quits, like clicking "End session" (#84), so it doesn't stay Live and
 * undeletable. Best effort: no session or sign-in, a refusal, an error or a slow backend never stops the quit.
 */
export async function endLiveSession({ sessionId, token, backendUrl, fetchImpl = fetch }: EndOnQuitInput): Promise<void> {
  if (!sessionId || !token) {
    return;
  }
  try {
    await fetchImpl(`${backendUrl}/api/sessions/${sessionId}/end`, {
      method: 'POST',
      headers: { Authorization: `Bearer ${token}` },
      signal: AbortSignal.timeout(END_ON_QUIT_TIMEOUT_MS),
    });
  } catch {
    // Quitting matters more: the session can still be ended from its page.
  }
}
