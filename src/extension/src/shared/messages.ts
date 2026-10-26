import type { AnswerEvent, SessionSummary } from './backend';

/** popup/content → background requests. */
export type Request =
  | { kind: 'pair'; code: string }
  | { kind: 'unpair' }
  | { kind: 'status' };

export type StatusResponse = { paired: boolean; sessions: SessionSummary[]; error?: string };

/** Port name for answer streaming (content ↔ background). */
export const ANSWER_PORT = 'answer-stream';

export type AnswerPortMessage = { kind: 'ask'; sessionId: string; question: string } | { kind: 'event'; event: AnswerEvent };
