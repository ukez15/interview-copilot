import { EventEmitter } from 'node:events';
import { mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import type { ChildProcess } from 'node:child_process';
import { describe, expect, it, vi } from 'vitest';
import { BackendProcess, ensureBackend, isHealthy, MAX_RESTARTS, packagedBackendPath, waitForHealthy } from './backend-process';
import { IpcChannels, IpcEvents } from './ipc-channels';
import { KeyStore, type SecretCipher } from './key-store';
import { decide, detectMeetingApp, meetingEventFor, normalizeProcessName, parsePs, parseTasklist } from './meeting-detection';
import { clampOpacity, DEFAULT_SETTINGS, sanitize, SettingsStore } from './settings-store';

const tempDir = () => mkdtemp(path.join(tmpdir(), 'ic-desktop-'));

/** Reversible stand-in for safeStorage. */
function fakeCipher(available = true): SecretCipher {
  return {
    isEncryptionAvailable: () => available,
    encryptString: (text) => Buffer.from(`enc:${text}`),
    decryptString: (buffer) => buffer.toString().replace(/^enc:/, ''),
  };
}

describe('settings', () => {
  it('clamps opacity and validates untrusted input', () => {
    expect(clampOpacity(0.1)).toBe(0.3);
    expect(clampOpacity(5)).toBe(1);
    expect(clampOpacity('x')).toBe(0.85);
    expect(sanitize(null)).toEqual(DEFAULT_SETTINGS);
    expect(sanitize({ overlayOpacity: 0.5, autoStartOnMeeting: 'yes' })).toEqual({ overlayOpacity: 0.5, autoStartOnMeeting: false, micDeviceId: null });
  });

  it('defaults to 85% opacity and ask-first, then persists updates', async () => {
    const dir = await tempDir();
    const store = SettingsStore.inDirectory(dir);

    expect(await store.get()).toEqual({ overlayOpacity: 0.85, autoStartOnMeeting: false, micDeviceId: null });
    await store.update({ autoStartOnMeeting: true, micDeviceId: 'usb-mic' });

    expect(await SettingsStore.inDirectory(dir).get()).toEqual({ overlayOpacity: 0.85, autoStartOnMeeting: true, micDeviceId: 'usb-mic' });
  });

  it('recovers from a corrupt settings file', async () => {
    const dir = await tempDir();
    await writeFile(path.join(dir, 'settings.json'), '{not json');

    expect(await SettingsStore.inDirectory(dir).get()).toEqual(DEFAULT_SETTINGS);
  });
});

describe('KeyStore', () => {
  it('stores only ciphertext and round-trips keys', async () => {
    const dir = await tempDir();
    const store = KeyStore.inDirectory(fakeCipher(), dir);

    await store.set('deepgram', '  dg-secret  ');
    const onDisk = await readFile(path.join(dir, 'keys.json'), 'utf8');

    expect(onDisk).not.toContain('dg-secret');
    expect(await store.get('deepgram')).toBe('dg-secret');
    await store.delete('deepgram');
    expect(await store.get('deepgram')).toBeNull();
  });

  it('refuses to save without OS encryption, and rejects bad input', async () => {
    const dir = await tempDir();

    await expect(KeyStore.inDirectory(fakeCipher(false), dir).set('openai', 'k')).rejects.toThrow('keychain');
    await expect(KeyStore.inDirectory(fakeCipher(), dir).set('openai', ' ')).rejects.toThrow('empty');
    await expect(KeyStore.inDirectory(fakeCipher(), dir).get('../etc')).rejects.toThrow('Unknown provider');
    expect(await KeyStore.inDirectory(fakeCipher(false), dir).get('openai')).toBeNull();
  });

  it('treats an unreadable key file as empty', async () => {
    const dir = await tempDir();
    await writeFile(path.join(dir, 'keys.json'), 'null');

    expect(await KeyStore.inDirectory(fakeCipher(), dir).get('openai')).toBeNull();
  });
});

describe('meeting detection', () => {
  it('normalises process names across platforms', () => {
    expect(normalizeProcessName('C:\\Program Files\\Zoom\\Zoom.exe')).toBe('zoom');
    expect(normalizeProcessName('/Applications/zoom.us.app')).toBe('zoom.us');
  });

  it('recognises meeting apps', () => {
    expect(detectMeetingApp(['explorer.exe', 'ms-teams.exe'])).toBe('Microsoft Teams');
    expect(detectMeetingApp(['/usr/bin/zsh', '/Applications/zoom.us.app/Contents/MacOS/zoom.us'])).toBe('Zoom');
    expect(detectMeetingApp(['notepad.exe'])).toBeNull();
  });

  it('parses tasklist and ps output', () => {
    expect(parseTasklist('"Zoom.exe","123","Console","1","10,000 K"\r\n"System","4"\r\n\r\n')).toEqual(['Zoom.exe', 'System']);
    expect(parsePs('  /sbin/launchd\n/Applications/Slack.app/Contents/MacOS/Slack\n\n')).toEqual([
      '/sbin/launchd',
      '/Applications/Slack.app/Contents/MacOS/Slack',
    ]);
  });

  it('asks first, starts only when auto-start is on, and never repeats for the same meeting', () => {
    expect(decide(null, 'Zoom', false)).toBe('ask');
    expect(decide(null, 'Zoom', true)).toBe('start');
    expect(decide('Zoom', 'Zoom', false)).toBe('ignore');
    expect(decide('Zoom', null, false)).toBe('ignore');
  });
});

describe('preload bridge', () => {
  it('uses exactly the channels the main process handles', async () => {
    const source = await readFile(path.join(import.meta.dirname, 'preload.ts'), 'utf8');
    const used = [...source.matchAll(/invoke\('([^']+)'/g)].map((m) => m[1]).sort();

    expect(used).toEqual(Object.values(IpcChannels).sort());
  });

  it('listens to exactly the events the main process sends', async () => {
    const source = await readFile(path.join(import.meta.dirname, 'preload.ts'), 'utf8');
    const listened = [...source.matchAll(/ipcRenderer\.on\('([^']+)'/g)].map((m) => m[1]).sort();

    expect(listened).toEqual(Object.values(IpcEvents).sort());
  });
});

describe('meeting event', () => {
  it('tells the renderer to show the picker after the user accepted the prompt', () => {
    expect(meetingEventFor('ask', 'Zoom', true)).toEqual({ app: 'Zoom', autoStart: false });
  });

  it('sends nothing when the user declined the prompt', () => {
    expect(meetingEventFor('ask', 'Zoom', false)).toBeNull();
  });

  it('asks the renderer to start straight away only when auto-start decided so', () => {
    expect(meetingEventFor('start', 'Webex', false)).toEqual({ app: 'Webex', autoStart: true });
  });

  it('sends nothing for an ignored meeting', () => {
    expect(meetingEventFor('ignore', 'Zoom', true)).toBeNull();
  });
});

describe('backend process', () => {
  it('finds the packaged executable per OS', () => {
    expect(packagedBackendPath('/res', 'win32')).toBe(path.join('/res', 'backend', 'InterviewCopilot.Api.exe'));
    expect(packagedBackendPath('/res', 'darwin')).toBe(path.join('/res', 'backend', 'InterviewCopilot.Api'));
  });

  it('waits until /health answers', async () => {
    const fetchImpl = vi.fn().mockRejectedValueOnce(new Error('down')).mockResolvedValueOnce({ ok: false }).mockResolvedValueOnce({ ok: true });

    expect(await waitForHealthy(fetchImpl, 10_000, 1, Date.now, () => Promise.resolve())).toBe(true);
    expect(fetchImpl).toHaveBeenCalledWith('http://127.0.0.1:5480/health');
  });

  it('probes /health once and treats errors and non-OK answers as down', async () => {
    expect(await isHealthy(() => Promise.resolve({ ok: true }))).toBe(true);
    expect(await isHealthy(() => Promise.resolve({ ok: false }))).toBe(false);
    expect(await isHealthy(() => Promise.reject(new Error('refused')))).toBe(false);
  });

  it('reuses a backend that is already healthy instead of spawning a second one (#36)', async () => {
    const spawnBackend = vi.fn();

    expect(await ensureBackend({ packaged: true, probe: () => Promise.resolve(true), spawnBackend })).toBe('reused');
    expect(spawnBackend).not.toHaveBeenCalled();
  });

  it('spawns the bundled backend when nothing answers on the port', async () => {
    const spawnBackend = vi.fn();

    expect(await ensureBackend({ packaged: true, probe: () => Promise.resolve(false), spawnBackend })).toBe('spawned');
    expect(spawnBackend).toHaveBeenCalledTimes(1);
  });

  it('never spawns in development, where the backend runs separately', async () => {
    const spawnBackend = vi.fn();
    const probe = vi.fn();

    expect(await ensureBackend({ packaged: false, probe, spawnBackend })).toBe('external');
    expect(spawnBackend).not.toHaveBeenCalled();
    expect(probe).not.toHaveBeenCalled();
  });

  it('gives up after the timeout', async () => {
    let time = 0;
    const result = await waitForHealthy(
      () => Promise.resolve({ ok: false }),
      1_000,
      500,
      () => time,
      (ms) => {
        time += ms;
        return Promise.resolve();
      },
    );

    expect(result).toBe(false);
  });

  it('restarts a crashed backend up to the limit, then reports', () => {
    const children: EventEmitter[] = [];
    const spawnImpl = vi.fn(() => {
      const child = Object.assign(new EventEmitter(), { kill: vi.fn() });
      children.push(child);
      return child as unknown as ChildProcess;
    });
    const onFatal = vi.fn();
    const backend = new BackendProcess('api.exe', onFatal, spawnImpl);

    backend.start();
    for (let i = 0; i <= MAX_RESTARTS; i++) {
      children.at(-1)!.emit('exit', 1);
    }

    expect(spawnImpl).toHaveBeenCalledWith('api.exe', ['--urls', 'http://127.0.0.1:5480'], { stdio: 'ignore', windowsHide: true });
    expect(backend.restartCount).toBe(MAX_RESTARTS);
    expect(onFatal).toHaveBeenCalledWith(expect.stringContaining('port 5480'));
  });

  it('does not restart after a deliberate stop', () => {
    const child = Object.assign(new EventEmitter(), { kill: vi.fn() });
    const spawnImpl = vi.fn(() => child as unknown as ChildProcess);
    const backend = new BackendProcess('api.exe', vi.fn(), spawnImpl);

    backend.start();
    backend.stop();
    child.emit('exit', 0);

    expect(child.kill).toHaveBeenCalled();
    expect(spawnImpl).toHaveBeenCalledTimes(1);
  });
});
