/** Capture state shared by the capture window, main process and UI. */
export type CaptureState = 'idle' | 'connecting' | 'live' | 'reconnecting' | 'error' | 'stopped';

export interface CaptureStatus {
  state: CaptureState;
  /** System audio (interviewer) track is running. */
  interviewer: boolean;
  /** Microphone (candidate) track is running. */
  mic: boolean;
  /** User-facing explanation for errors or degraded capture. */
  message: string | null;
}

export interface CaptureConfig {
  sessionId: string;
  token: string;
  micDeviceId: string | null;
  backendUrl: string;
}

export const IDLE_STATUS: CaptureStatus = { state: 'idle', interviewer: false, mic: false, message: null };

/** Builds the audio WebSocket URL. The token goes in the query because browsers can't set WS headers. */
export function audioSocketUrl(config: CaptureConfig): string {
  const base = config.backendUrl.replace(/^http/, 'ws');
  return `${base}/ws/sessions/${encodeURIComponent(config.sessionId)}/audio?access_token=${encodeURIComponent(config.token)}`;
}
