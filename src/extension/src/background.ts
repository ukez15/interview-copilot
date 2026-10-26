import { claimPairing, liveSessions, streamAnswer, BackendError, UnauthorizedError } from './shared/backend';
import { browserName } from './shared/browser';
import { ANSWER_PORT, type AnswerPortMessage, type Request, type StatusResponse } from './shared/messages';

/**
 * Background worker. Holds the pairing token in local storage (it outlives the browser session until the
 * user revokes it in the desktop app) and is the only part of the extension that talks to the local backend.
 */
const TOKEN_KEY = 'token';

async function token(): Promise<string | null> {
  const stored = await chrome.storage.local.get(TOKEN_KEY);
  return typeof stored[TOKEN_KEY] === 'string' ? (stored[TOKEN_KEY] as string) : null;
}

export async function handleRequest(request: Request): Promise<StatusResponse> {
  try {
    if (request.kind === 'pair') {
      await chrome.storage.local.set({ [TOKEN_KEY]: await claimPairing(request.code, browserName(navigator.userAgent)) });
    } else if (request.kind === 'unpair') {
      await chrome.storage.local.remove(TOKEN_KEY);
    }
    const current = await token();
    return { paired: current !== null, sessions: current ? await liveSessions(current) : [] };
  } catch (error) {
    await dropTokenIfRejected(error);
    return { paired: (await token()) !== null, sessions: [], error: errorMessage(error) };
  }
}

export function handlePort(port: chrome.runtime.Port): void {
  if (port.name !== ANSWER_PORT) {
    return;
  }
  port.onMessage.addListener((message: AnswerPortMessage) => {
    if (message.kind !== 'ask') {
      return;
    }
    void (async () => {
      const send = (event: AnswerPortMessage) => port.postMessage(event);
      const current = await token();
      if (!current) {
        send({ kind: 'event', event: { type: 'error', message: 'Pair the extension from its popup first.' } });
        return;
      }
      try {
        await streamAnswer(current, message.sessionId, message.question, (event) => send({ kind: 'event', event }));
      } catch (error) {
        await dropTokenIfRejected(error);
        send({ kind: 'event', event: { type: 'error', message: errorMessage(error) } });
      }
    })();
  });
}

/** A 401 means the token was revoked (or never valid): forget it so the popup asks to pair again. */
async function dropTokenIfRejected(error: unknown): Promise<void> {
  if (error instanceof UnauthorizedError) {
    await chrome.storage.local.remove(TOKEN_KEY);
  }
}

function errorMessage(error: unknown): string {
  return error instanceof BackendError ? error.message : 'Something went wrong.';
}

chrome.runtime.onMessage.addListener((request: Request, _sender, sendResponse) => {
  void handleRequest(request).then(sendResponse);
  return true; // async response
});
chrome.runtime.onConnect.addListener(handlePort);
