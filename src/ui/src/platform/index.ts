/**
 * Everything the UI needs from its host. The desktop app provides it via the Electron preload
 * bridge; in a plain browser (Mode C) the web fallback is used. Never import electron in src/ui.
 */
export interface DesktopBridge {
  /** Held in the main process's memory only, so the overlay window shares the sign-in. */
  getAuthToken(): Promise<string | null>;
  setAuthToken(token: string | null): Promise<void>;
  getApiKey(provider: string): Promise<string | null>;
  setApiKey(provider: string, key: string): Promise<void>;
  deleteApiKey(provider: string): Promise<void>;
  setOverlayOpacity(opacity: number): Promise<void>;
  showOverlay(sessionId: string): Promise<void>;
  hideOverlay(): Promise<void>;
  getSettings(): Promise<DesktopSettings>;
  setSettings(settings: Partial<DesktopSettings>): Promise<DesktopSettings>;
  /** Starts system-audio + mic capture for a live session (docs/atlas-gotcha/audio-capture-windows.md). */
  startCapture(sessionId: string): Promise<CaptureStatus>;
  stopCapture(): Promise<CaptureStatus>;
  getCaptureStatus(): Promise<CaptureStatus>;
}

export interface DesktopSettings {
  overlayOpacity: number;
  /** FR-16: off by default, so the app asks before it starts listening. */
  autoStartOnMeeting: boolean;
  /** Microphone to capture; null = the OS default device. */
  micDeviceId: string | null;
}

export type CaptureState = 'idle' | 'connecting' | 'live' | 'reconnecting' | 'error' | 'stopped';

export interface CaptureStatus {
  state: CaptureState;
  interviewer: boolean;
  mic: boolean;
  message: string | null;
}

export interface Platform extends DesktopBridge {
  readonly kind: 'desktop' | 'web';
}

declare global {
  interface Window {
    interviewCopilot?: DesktopBridge;
  }
}

export const DEFAULT_SETTINGS: DesktopSettings = { overlayOpacity: 0.85, autoStartOnMeeting: false, micDeviceId: null };

const WEB_CAPTURE: CaptureStatus = {
  state: 'idle',
  interviewer: false,
  mic: false,
  message: 'Audio capture needs the desktop app.',
};

/** Mode C placeholder: no keychain, overlay or capture in a browser; keys are never put in web storage. */
export function createWebPlatform(): Platform {
  let settings = { ...DEFAULT_SETTINGS };
  let authToken: string | null = null;
  return {
    kind: 'web',
    getAuthToken: () => Promise.resolve(authToken),
    setAuthToken: (token) => {
      authToken = token;
      return Promise.resolve();
    },
    getApiKey: () => Promise.resolve(null),
    setApiKey: () => Promise.reject(new Error('Saving API keys needs the desktop app.')),
    deleteApiKey: () => Promise.resolve(),
    setOverlayOpacity: () => Promise.resolve(),
    showOverlay: () => Promise.resolve(),
    hideOverlay: () => Promise.resolve(),
    getSettings: () => Promise.resolve(settings),
    setSettings: (patch) => {
      settings = { ...settings, ...patch };
      return Promise.resolve(settings);
    },
    startCapture: () => Promise.resolve(WEB_CAPTURE),
    stopCapture: () => Promise.resolve(WEB_CAPTURE),
    getCaptureStatus: () => Promise.resolve(WEB_CAPTURE),
  };
}

export function createDesktopPlatform(bridge: DesktopBridge): Platform {
  return {
    kind: 'desktop',
    getAuthToken: () => bridge.getAuthToken(),
    setAuthToken: (token) => bridge.setAuthToken(token),
    getApiKey: (provider) => bridge.getApiKey(provider),
    setApiKey: (provider, key) => bridge.setApiKey(provider, key),
    deleteApiKey: (provider) => bridge.deleteApiKey(provider),
    setOverlayOpacity: (opacity) => bridge.setOverlayOpacity(opacity),
    showOverlay: (sessionId) => bridge.showOverlay(sessionId),
    hideOverlay: () => bridge.hideOverlay(),
    getSettings: () => bridge.getSettings(),
    setSettings: (patch) => bridge.setSettings(patch),
    startCapture: (sessionId) => bridge.startCapture(sessionId),
    stopCapture: () => bridge.stopCapture(),
    getCaptureStatus: () => bridge.getCaptureStatus(),
  };
}

export function detectPlatform(win: Window = window): Platform {
  return win.interviewCopilot ? createDesktopPlatform(win.interviewCopilot) : createWebPlatform();
}
