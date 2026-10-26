import { contextBridge, ipcRenderer } from 'electron';

// Sandboxed preloads can only require 'electron', so channel names are inlined here.
// desktop.test.ts checks they match ipc-channels.ts.
contextBridge.exposeInMainWorld('interviewCopilot', {
  getAuthToken: () => ipcRenderer.invoke('auth:get-token'),
  setAuthTokens: (tokens: { accessToken: string; refreshToken: string } | null) => ipcRenderer.invoke('auth:set-tokens', tokens),
  refreshAuth: () => ipcRenderer.invoke('auth:refresh'),
  getApiKey: (provider: string) => ipcRenderer.invoke('keys:get', provider),
  setApiKey: (provider: string, key: string) => ipcRenderer.invoke('keys:set', provider, key),
  deleteApiKey: (provider: string) => ipcRenderer.invoke('keys:delete', provider),
  setOverlayOpacity: (opacity: number) => ipcRenderer.invoke('overlay:set-opacity', opacity),
  showOverlay: (sessionId: string) => ipcRenderer.invoke('overlay:show', sessionId),
  hideOverlay: () => ipcRenderer.invoke('overlay:hide'),
  getSettings: () => ipcRenderer.invoke('settings:get'),
  setSettings: (patch: unknown) => ipcRenderer.invoke('settings:set', patch),
  startCapture: (sessionId: string) => ipcRenderer.invoke('capture:start', sessionId),
  stopCapture: () => ipcRenderer.invoke('capture:stop'),
  getCaptureStatus: () => ipcRenderer.invoke('capture:status'),
  openDataFolder: () => ipcRenderer.invoke('storage:open-folder'),
  onMeetingDetected: (callback: (event: { app: string; autoStart: boolean }) => void) => {
    const listener = (_event: unknown, payload: { app: string; autoStart: boolean }) => callback(payload);
    ipcRenderer.on('meeting:detected', listener);
    return () => {
      ipcRenderer.removeListener('meeting:detected', listener);
    };
  },
});
