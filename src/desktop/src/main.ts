import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { app, BrowserWindow, desktopCapturer, dialog, ipcMain, net, protocol, safeStorage, session, webContents, type IpcMainInvokeEvent } from 'electron';
import { BackendProcess, packagedBackendPath, waitForHealthy } from './backend-process';
import { refreshAccessToken } from './auth-refresh';
import { AuthStore } from './auth-store';
import { CaptureController } from './capture-controller';
import { APP_HOST, APP_PROTOCOL, BACKEND_URL, CaptureChannels, IpcChannels } from './ipc-channels';
import { KeyStore } from './key-store';
import { decide, detectMeetingApp, listProcessNames } from './meeting-detection';
import { clampOpacity, SettingsStore } from './settings-store';
import { createCaptureWindow, createMainWindow, createOverlayWindow } from './windows';

const MEETING_POLL_MS = 5_000;

protocol.registerSchemesAsPrivileged([{ scheme: APP_PROTOCOL, privileges: { standard: true, secure: true, supportFetchAPI: true } }]);

let mainWindow: BrowserWindow | null = null;
let overlayWindow: BrowserWindow | null = null;
let authToken: string | null = null;
let lastMeeting: string | null = null;
let backend: BackendProcess | null = null;

const settings = SettingsStore.inDirectory(app.getPath('userData'));
const auth = AuthStore.inDirectory(safeStorage, app.getPath('userData'));
const keys = KeyStore.inDirectory(safeStorage, app.getPath('userData'));
const capture = new CaptureController({
  createWindow: () => {
    const window = createCaptureWindow();
    return {
      webContentsId: window.webContents.id,
      destroy: () => window.destroy(),
      isDestroyed: () => window.isDestroyed(),
      onClosed: (listener) => window.on('closed', listener),
    };
  },
  getToken: () => authToken,
  getMicDeviceId: async () => (await settings.get()).micDeviceId,
  backendUrl: BACKEND_URL,
});

/** Only our own pages may call the bridge. */
function fromTrustedFrame(event: IpcMainInvokeEvent): boolean {
  return isTrustedUrl(event.senderFrame?.url ?? '');
}

function handle(channel: string, listener: (...args: unknown[]) => unknown): void {
  ipcMain.handle(channel, (event, ...args) => {
    if (!fromTrustedFrame(event)) {
      throw new Error('Untrusted sender.');
    }
    return listener(...args);
  });
}

// Sign-in persistence (#8): the access token lives in memory; only the refresh token is stored (encrypted).
let refreshing: Promise<string | null> | null = null;

function refreshAuthToken(): Promise<string | null> {
  refreshing ??= refreshAccessToken(auth, BACKEND_URL)
    .then((token) => (authToken = token))
    .finally(() => (refreshing = null));
  return refreshing;
}

function registerAuthIpc(): void {
  // At startup there is no access token yet: restore the sign-in from the stored refresh token.
  handle(IpcChannels.getAuthToken, () => authToken ?? refreshAuthToken());
  handle(IpcChannels.refreshAuth, () => refreshAuthToken());
  handle(IpcChannels.setAuthTokens, async (tokens) => {
    const value = tokens as { accessToken?: unknown; refreshToken?: unknown } | null;
    if (!value || typeof value.accessToken !== 'string' || typeof value.refreshToken !== 'string') {
      authToken = null;
      await auth.clear();
      return;
    }
    authToken = value.accessToken;
    // Without a usable OS keychain the sign-in simply lasts until the app closes.
    await auth.set(value.refreshToken).catch(() => undefined);
  });
}

function registerIpc(): void {
  registerAuthIpc();
  handle(IpcChannels.getApiKey, (provider) => keys.get(String(provider)));
  handle(IpcChannels.setApiKey, (provider, key) => keys.set(String(provider), String(key)));
  handle(IpcChannels.deleteApiKey, (provider) => keys.delete(String(provider)));
  handle(IpcChannels.getSettings, () => settings.get());
  handle(IpcChannels.setSettings, (patch) => settings.update(patch));
  handle(IpcChannels.setOverlayOpacity, (opacity) => overlayWindow?.setOpacity(clampOpacity(opacity)));
  handle(IpcChannels.showOverlay, async (sessionId) => {
    overlayWindow?.close();
    overlayWindow = createOverlayWindow(String(sessionId), (await settings.get()).overlayOpacity);
    overlayWindow.on('closed', () => (overlayWindow = null));
  });
  handle(IpcChannels.hideOverlay, () => {
    overlayWindow?.close();
    overlayWindow = null;
  });
  handle(IpcChannels.startCapture, (sessionId) => capture.start(String(sessionId)));
  handle(IpcChannels.stopCapture, () => capture.stop());
  handle(IpcChannels.getCaptureStatus, () => capture.status);

  // Capture-window channels: only that window's webContents is accepted (it receives the token).
  ipcMain.handle(CaptureChannels.getConfig, (event) => capture.configFor(event.sender.id));
  ipcMain.handle(CaptureChannels.reportStatus, (event, status: unknown) => capture.report(event.sender.id, status));
}

function isTrustedUrl(url: string): boolean {
  return url.startsWith(`${APP_PROTOCOL}://${APP_HOST}/`) || (!!process.env.INTERVIEW_COPILOT_DEV_URL && url.startsWith(process.env.INTERVIEW_COPILOT_DEV_URL));
}

/** Only our pages get mic/screen permissions; system audio goes only to the capture window (Windows loopback). */
function configureMediaAccess(): void {
  const media = new Set(['media', 'display-capture']);
  session.defaultSession.setPermissionRequestHandler((_contents, permission, callback, details) =>
    callback(isTrustedUrl(details.requestingUrl) && (media.has(permission) || permission === 'clipboard-sanitized-write')),
  );
  session.defaultSession.setPermissionCheckHandler((_contents, permission, requestingOrigin) =>
    media.has(permission) && isTrustedUrl(`${requestingOrigin}/`),
  );
  session.defaultSession.setDisplayMediaRequestHandler((request, callback) => {
    const contents = request.frame ? webContents.fromFrame(request.frame) : undefined;
    if (!contents || !capture.isCaptureWindow(contents.id)) {
      callback({});
      return;
    }
    void desktopCapturer.getSources({ types: ['screen'] }).then((sources) => {
      const screen = sources[0];
      // Electron supports 'loopback' (system audio) on Windows; the video track is stopped by the capture window.
      callback(screen ? { video: screen, audio: 'loopback' } : {});
    });
  });
}

function serveUi(): void {
  const uiRoot = app.isPackaged ? path.join(process.resourcesPath, 'ui') : path.resolve(__dirname, '../../ui/dist');
  const captureRoot = path.join(__dirname, 'renderer');
  protocol.handle(APP_PROTOCOL, (request) => {
    const { pathname } = new URL(request.url);
    const root = pathname.startsWith('/capture/') ? captureRoot : uiRoot;
    const filePath = path.normalize(path.join(root, decodeURIComponent(pathname)));
    if (!filePath.startsWith(root)) {
      return new Response('Not found', { status: 404 });
    }
    return net.fetch(pathToFileURL(filePath).toString());
  });
}

async function startBackend(): Promise<void> {
  if (app.isPackaged) {
    backend = new BackendProcess(packagedBackendPath(process.resourcesPath), (message) => dialog.showErrorBox('InterviewCopilot', message));
    backend.start();
  }
  if (!(await waitForHealthy())) {
    dialog.showErrorBox(
      'InterviewCopilot',
      app.isPackaged
        ? "The local backend didn't start. Check that port 5480 is free and restart the app."
        : 'Backend not reachable on http://127.0.0.1:5480. Start it with: pnpm dev:backend',
    );
  }
}

/** FR-16: ask before listening when a meeting app starts (unless auto-start is on). */
function watchForMeetings(): void {
  setInterval(() => {
    void (async () => {
      const meeting = detectMeetingApp(await listProcessNames().catch(() => []));
      const decision = decide(lastMeeting, meeting, (await settings.get()).autoStartOnMeeting);
      lastMeeting = meeting;
      if (decision === 'ignore' || !mainWindow) {
        return;
      }
      if (decision === 'ask') {
        const { response } = await dialog.showMessageBox(mainWindow, {
          type: 'question',
          buttons: ['Start InterviewCopilot', 'Not now'],
          defaultId: 0,
          cancelId: 1,
          message: `${meeting} meeting detected. Start InterviewCopilot?`,
          detail: 'It will listen to the meeting audio on this computer. Make sure transcription is allowed in this conversation.',
        });
        if (response !== 0) {
          return;
        }
      }
      mainWindow.show();
      mainWindow.focus();
    })();
  }, MEETING_POLL_MS);
}

function openMainWindow(): void {
  if (mainWindow) {
    mainWindow.show();
    mainWindow.focus();
    return;
  }
  mainWindow = createMainWindow();
  mainWindow.on('closed', () => (mainWindow = null));
}

// One instance only; launching again (e.g. after closing the main window mid-call) brings it back.
if (!app.requestSingleInstanceLock()) {
  app.quit();
}
app.on('second-instance', () => openMainWindow());

void app.whenReady().then(async () => {
  serveUi();
  configureMediaAccess();
  registerIpc();
  openMainWindow();
  await startBackend();
  watchForMeetings();
});

app.on('window-all-closed', () => {
  capture.stop();
  backend?.stop();
  app.quit();
});
