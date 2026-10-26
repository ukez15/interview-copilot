import type { CaptureConfig, CaptureStatus } from './capture/status';

export const IDLE: CaptureStatus = { state: 'idle', interviewer: false, mic: false, message: null };

/** The part of a BrowserWindow the controller needs (keeps it testable without Electron). */
export interface CaptureWindowLike {
  readonly webContentsId: number;
  destroy(): void;
  isDestroyed(): boolean;
  onClosed(listener: () => void): void;
}

export interface CaptureControllerDeps {
  createWindow: () => CaptureWindowLike;
  getToken: () => string | null;
  getMicDeviceId: () => Promise<string | null>;
  backendUrl: string;
  /** Used for the session lookup; defaults to the global `fetch`. */
  fetchImpl?: typeof fetch;
}

/**
 * Owns the hidden capture window for at most one live session (docs/atlas-gotcha/audio-capture-windows.md).
 * Capture only starts from an explicit user action routed here via IPC.
 */
export class CaptureController {
  private window: CaptureWindowLike | null = null;
  private config: CaptureConfig | null = null;
  private current: CaptureStatus = IDLE;

  constructor(private readonly deps: CaptureControllerDeps) {}

  get status(): CaptureStatus {
    return this.current;
  }

  async start(sessionId: string): Promise<CaptureStatus> {
    if (typeof sessionId !== 'string' || !/^[0-9a-f-]{36}$/i.test(sessionId)) {
      throw new Error('Invalid session id.');
    }
    const token = this.deps.getToken();
    if (!token) {
      throw new Error('Sign in before starting capture.');
    }
    this.stop();
    const micOnly = await this.isMockInterview(sessionId, token);
    this.config = { sessionId, token, micDeviceId: await this.deps.getMicDeviceId(), backendUrl: this.deps.backendUrl, micOnly };
    this.current = { state: 'connecting', interviewer: false, mic: false, message: null };
    const window = this.deps.createWindow();
    window.onClosed(() => {
      if (this.window === window) {
        this.window = null;
        this.config = null;
        if (this.current.state !== 'stopped') {
          this.current = { ...IDLE, state: 'error', message: 'Audio capture stopped unexpectedly.' };
        }
      }
    });
    this.window = window;
    return this.current;
  }

  /** Mock interviews capture the microphone only. Any lookup failure falls back to normal capture. */
  private async isMockInterview(sessionId: string, token: string): Promise<boolean> {
    try {
      const response = await (this.deps.fetchImpl ?? fetch)(`${this.deps.backendUrl}/api/sessions/${sessionId}`, {
        headers: { Authorization: `Bearer ${token}` },
      });
      if (!response.ok) {
        return false;
      }
      const session = (await response.json()) as { mode?: unknown };
      return session.mode === 'MockInterview';
    } catch {
      return false;
    }
  }

  stop(): CaptureStatus {
    const window = this.window;
    this.window = null;
    this.config = null;
    if (window) {
      this.current = { ...IDLE, state: 'stopped' };
      if (!window.isDestroyed()) {
        window.destroy();
      }
    }
    return this.current;
  }

  /** Only the capture window may read the config (it contains the token). */
  configFor(webContentsId: number): CaptureConfig {
    if (!this.isCaptureWindow(webContentsId) || !this.config) {
      throw new Error('Not the capture window.');
    }
    return this.config;
  }

  report(webContentsId: number, status: unknown): void {
    if (!this.isCaptureWindow(webContentsId)) {
      return;
    }
    this.current = sanitizeStatus(status);
  }

  isCaptureWindow(webContentsId: number): boolean {
    return this.window !== null && !this.window.isDestroyed() && this.window.webContentsId === webContentsId;
  }
}

const STATES = new Set(['idle', 'connecting', 'live', 'reconnecting', 'error', 'stopped']);

function sanitizeStatus(input: unknown): CaptureStatus {
  const value = (typeof input === 'object' && input !== null ? input : {}) as Record<string, unknown>;
  return {
    state: typeof value.state === 'string' && STATES.has(value.state) ? (value.state as CaptureStatus['state']) : 'error',
    interviewer: value.interviewer === true,
    mic: value.mic === true,
    message: typeof value.message === 'string' ? value.message.slice(0, 300) : null,
  };
}
