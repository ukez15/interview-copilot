import path from 'node:path';
import { BrowserWindow, screen } from 'electron';
import { APP_HOST, APP_PROTOCOL } from './ipc-channels';

const preload = path.join(__dirname, 'preload.js');

const secureWebPreferences = {
  preload,
  contextIsolation: true,
  nodeIntegration: false,
  sandbox: true,
} as const;

/** Dev: Vite dev server. Packaged: the built UI served from the app:// protocol. */
export function uiUrl(route = '/'): string {
  const base = process.env.INTERVIEW_COPILOT_DEV_URL ?? `${APP_PROTOCOL}://${APP_HOST}/index.html`;
  return `${base}#${route}`;
}

export function createMainWindow(): BrowserWindow {
  const window = new BrowserWindow({
    width: 1200,
    height: 800,
    minWidth: 720,
    minHeight: 520,
    title: 'InterviewCopilot',
    webPreferences: secureWebPreferences,
  });
  void window.loadURL(uiUrl('/'));
  return window;
}

/**
 * Hidden window that captures audio (docs/atlas-gotcha/audio-capture-windows.md). Never shown; background
 * throttling is off so audio keeps flowing while the user is in the meeting app.
 */
export function createCaptureWindow(): BrowserWindow {
  const window = new BrowserWindow({
    show: false,
    width: 1,
    height: 1,
    skipTaskbar: true,
    webPreferences: {
      preload: path.join(__dirname, 'capture-preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      backgroundThrottling: false,
      autoplayPolicy: 'no-user-gesture-required',
    },
  });
  void window.loadURL(`${APP_PROTOCOL}://${APP_HOST}/capture/capture.html`);
  return window;
}

/** FR-17: semi-transparent, always-on-top answer overlay. */
export function createOverlayWindow(sessionId: string, opacity: number): BrowserWindow {
  const { workArea } = screen.getPrimaryDisplay();
  const width = 440;
  const height = 360;
  const window = new BrowserWindow({
    width,
    height,
    x: workArea.x + workArea.width - width - 24,
    y: workArea.y + 24,
    frame: false,
    transparent: true,
    resizable: true,
    skipTaskbar: true,
    alwaysOnTop: true,
    title: 'InterviewCopilot overlay',
    webPreferences: secureWebPreferences,
  });
  window.setAlwaysOnTop(true, 'floating');
  window.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: true });
  window.setOpacity(opacity);
  void window.loadURL(uiUrl(`/overlay/${encodeURIComponent(sessionId)}`));
  return window;
}
