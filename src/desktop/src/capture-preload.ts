import { contextBridge, ipcRenderer } from 'electron';

// Preload for the hidden capture window only. Sandboxed preloads can only require 'electron',
// so channel names are inlined; desktop.test.ts checks they match ipc-channels.ts.
contextBridge.exposeInMainWorld('captureBridge', {
  getConfig: () => ipcRenderer.invoke('capture:get-config'),
  reportStatus: (status: unknown) => ipcRenderer.invoke('capture:report-status', status),
});
