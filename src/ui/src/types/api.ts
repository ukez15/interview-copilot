// Mirrors the backend DTOs (src/backend/src/InterviewCopilot.Application). Enums travel as names.

export const SESSION_MODES = ['LiveInterview', 'MockInterview', 'Conversation'] as const;
export type SessionMode = (typeof SESSION_MODES)[number];

export const ANSWER_LENGTHS = ['Short', 'Balanced', 'Long'] as const;
export type AnswerLength = (typeof ANSWER_LENGTHS)[number];

export const ANSWER_FRAMEWORKS = ['None', 'Star', 'Smart'] as const;
export type AnswerFramework = (typeof ANSWER_FRAMEWORKS)[number];

export const QUESTION_TYPES = [
  'Behavioral',
  'HowDoYou',
  'Situational',
  'TechnicalKnowledge',
  'SystemDesign',
  'Coding',
  'TellMeAboutYourself',
  'Experience',
] as const;
export type QuestionType = (typeof QUESTION_TYPES)[number];

export type AnswerModifier = 'None' | 'Regenerate' | 'Shorter' | 'Longer';
export type SessionStatus = 'Draft' | 'Live' | 'Ended';
export type Speaker = 'Interviewer' | 'Candidate';

/** FR-02 */
export const MAX_DOCUMENTS_PER_SESSION = 10;

export interface DocumentSummary {
  id: string;
  fileName: string;
  characterCount: number;
  createdAt: string;
}

export interface Session {
  id: string;
  title: string;
  mode: SessionMode;
  status: SessionStatus;
  answerLength: AnswerLength;
  framework: AnswerFramework;
  pinnedQuestionType: QuestionType | null;
  sessionPrompt: string;
  createdAt: string;
  startedAt: string | null;
  endedAt: string | null;
  documents: DocumentSummary[];
  /** #22: answer detected questions automatically (on by default, pausable). */
  autoAnswer: boolean;
}

export interface CreateSessionInput {
  title: string;
  mode: SessionMode;
  answerLength: AnswerLength;
  framework: AnswerFramework;
  pinnedQuestionType: QuestionType | null;
  sessionPrompt: string;
}

/** PUT /api/sessions/{id}/settings (#10). */
export interface SessionSettingsInput {
  answerLength: AnswerLength;
  framework: AnswerFramework;
  pinnedQuestionType: QuestionType | null;
  sessionPrompt: string;
}

/** GET /api/sessions/{id}/transcript (#28). */
export interface TranscriptEntry {
  atMs: number;
  speaker: Speaker;
  text: string;
}

export interface TranscriptAnswer {
  createdAt: string;
  question: string;
  questionType: QuestionType;
  version: number;
  text: string;
}

export interface Transcript {
  sessionId: string;
  title: string;
  mode: SessionMode;
  createdAt: string;
  entries: TranscriptEntry[];
  answers: TranscriptAnswer[];
}

export interface ProviderInfo {
  name: string;
  displayName: string;
  isLocal: boolean;
  requiresApiKey: boolean;
  isDefault: boolean;
}

export interface Providers {
  speechToText: ProviderInfo[];
  llm: ProviderInfo[];
}

export type WhisperModelState = 'missing' | 'downloading' | 'ready' | 'failed';

export interface WhisperModel {
  state: WhisperModelState;
  model: string;
  bytesDownloaded: number;
  totalBytes: number | null;
  message: string | null;
}

export interface OllamaModel {
  name: string;
  sizeBytes: number;
  modifiedAt: string;
}

export interface Preferences {
  /** Chosen Ollama model; null means the backend's configured default. */
  llmModel: string | null;
  /** The backend's configured default (Llm:Model), shown as "Default (<name>)". */
  defaultLlmModel: string;
}

export interface AnswerHeader {
  questionType: QuestionType;
  version: number;
}

export const QUESTION_TYPE_LABELS: Record<QuestionType, string> = {
  Behavioral: 'Behavioral',
  HowDoYou: 'How do you…',
  Situational: 'Situational',
  TechnicalKnowledge: 'Technical knowledge',
  SystemDesign: 'System design',
  Coding: 'Coding',
  TellMeAboutYourself: 'Tell me about yourself',
  Experience: 'Experience',
};
