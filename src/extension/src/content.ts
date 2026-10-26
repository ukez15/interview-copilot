import { createOverlay } from './content/overlay';
import { ANSWER_PORT, type AnswerPortMessage, type StatusResponse } from './shared/messages';

// Injected into meeting pages (see manifest matches). Shows the companion overlay (Mode B, FR-18).
const overlay = createOverlay(document, {
  onAsk: (sessionId, question) => {
    const port = chrome.runtime.connect({ name: ANSWER_PORT });
    port.onMessage.addListener((message: AnswerPortMessage) => {
      if (message.kind === 'event') {
        overlay.apply(message.event);
        if (message.event.type === 'done' || message.event.type === 'error') {
          port.disconnect();
        }
      }
    });
    port.postMessage({ kind: 'ask', sessionId, question } satisfies AnswerPortMessage);
  },
  onClose: () => overlay.host.remove(),
});

document.documentElement.append(overlay.host);
void chrome.runtime.sendMessage({ kind: 'status' }).then((status: StatusResponse) =>
  overlay.setSessions(status.sessions, status.paired ? status.error : (status.error ?? 'Pair the extension from the InterviewCopilot extension popup.')),
);
