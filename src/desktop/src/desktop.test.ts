import { EventEmitter } from 'node:events';
import { mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import type { ChildProcess } from 'node:child_process';
import { describe, expect, it, vi } from 'vitest';
import { BackendProcess, ensureBackend, isHealthy, MAX_RESTARTS, packagedBackendPath, waitForHealthy } from './backend-process';
import { IpcChannels, IpcEvents } from './ipc-channels';
import { KeyStore, type SecretCipher } from './key-store';
import {
  currentMeeting,
  decide,
  detectActiveMeeting,
  detectMeetingApp,
  meetingAppForConsentKey,
  meetingEventFor,
  normalizeProcessName,
  parseConsentStore,
  parsePs,
  parseTasklist,
} from './meeting-detection';
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

  // Trimmed `reg query ...\ConsentStore\microphone /s` output, as captured on Windows 11.
  const consentStore = (teamsStop: string, zoomStop: string): string =>
    [
      'HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\microphone',
      '    Value    REG_SZ    Allow',
      '',
      'HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\microphone\\MSTeams_8wekyb3d8bbwe',
      '    Value    REG_SZ    Allow',
      '    LastUsedTimeStart    REG_QWORD    0x1dd4ad266ed738d',
      `    LastUsedTimeStop    REG_QWORD    ${teamsStop}`,
      '',
      'HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\microphone\\Microsoft.WindowsCamera_8wekyb3d8bbwe',
      '    Value    REG_SZ    Allow',
      '',
      'HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\microphone\\NonPackaged',
      '',
      'HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\microphone\\NonPackaged\\C:#Users#me#AppData#Roaming#Zoom#bin#Zoom.exe',
      '    LastUsedTimeStart    REG_QWORD    0x1dd5381a205fe4c',
      `    LastUsedTimeStop    REG_QWORD    ${zoomStop}`,
      '',
      'HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\microphone\\NonPackaged\\C:#Users#me#AppData#Local#Programs#interview-copilot#InterviewCopilot.exe',
      '    LastUsedTimeStart    REG_QWORD    0x1dd5381a205fe4c',
      '    LastUsedTimeStop    REG_QWORD    0x0',
      '',
    ].join('\r\n');

  it('parses which apps hold the microphone from the consent store', () => {
    expect(parseConsentStore(consentStore('0x0', '0x1dd5381dd2f75e3'))).toEqual([
      { key: 'MSTeams_8wekyb3d8bbwe', inUse: true },
      { key: 'C:#Users#me#AppData#Roaming#Zoom#bin#Zoom.exe', inUse: false },
      { key: 'C:#Users#me#AppData#Local#Programs#interview-copilot#InterviewCopilot.exe', inUse: true },
    ]);
    expect(parseConsentStore('')).toEqual([]);
  });

  it('maps consent store keys to meeting apps', () => {
    expect(meetingAppForConsentKey('MSTeams_8wekyb3d8bbwe')).toBe('Microsoft Teams');
    expect(meetingAppForConsentKey('C:#Program Files#Slack#slack.exe')).toBe('Slack');
    expect(meetingAppForConsentKey('C:#Program Files#Google#Chrome#Application#chrome.exe')).toBeNull();
    expect(meetingAppForConsentKey('Claude_pzs8sxrjxfjjc')).toBeNull();
  });

  it('reports a meeting only while its app holds the mic and is running', () => {
    const running = ['explorer.exe', 'ms-teams.exe', 'Zoom.exe'];
    expect(detectActiveMeeting(running, consentStore('0x0', '0x1dd5381dd2f75e3'))).toBe('Microsoft Teams');
    expect(detectActiveMeeting(running, consentStore('0x1dd4ad281fbb0c1', '0x0'))).toBe('Zoom');
    // Teams open in the background, no call: the beta's false prompt.
    expect(detectActiveMeeting(running, consentStore('0x1dd4ad281fbb0c1', '0x1dd5381dd2f75e3'))).toBeNull();
    // Stale "in use" left behind by a crash: the process is gone.
    expect(detectActiveMeeting(['explorer.exe'], consentStore('0x0', '0x0'))).toBeNull();
    // Our own capture holds the mic, but isn't a meeting app.
    expect(detectActiveMeeting(['InterviewCopilot.exe'], consentStore('0x1', '0x1'))).toBeNull();
  });

  it('uses the mic signal on Windows and the process list elsewhere', async () => {
    const run = vi.fn(async (command: string) =>
      command === 'tasklist' ? '"ms-teams.exe","1"\r\n' : consentStore('0x0', '0x1'),
    );
    expect(await currentMeeting('win32', run)).toBe('Microsoft Teams');
    expect(run).toHaveBeenCalledWith('reg', ['query', expect.stringContaining('ConsentStore\\microphone'), '/s']);

    const macRun = vi.fn(async () => '/Applications/zoom.us.app/Contents/MacOS/zoom.us\n');
    expect(await currentMeeting('darwin', macRun)).toBe('Zoom');
    expect(macRun).toHaveBeenCalledWith('ps', ['-axo', 'comm=']);
  });

  it('prompts for nothing when the consent store or process list cannot be read', async () => {
    const regFails = async (command: string) => {
      if (command === 'reg') throw new Error('access denied');
      return '"ms-teams.exe","1"\r\n';
    };
    expect(await currentMeeting('win32', regFails)).toBeNull();
    expect(await currentMeeting('darwin', async () => Promise.reject(new Error('ps missing')))).toBeNull();
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

    expect(spawnImpl).toHaveBeenCalledWith('api.exe', ['--urls', 'http://127.0.0.1:5480'], expect.objectContaining({ stdio: 'ignore', windowsHide: true }));
    expect(backend.restartCount).toBe(MAX_RESTARTS);
    expect(onFatal).toHaveBeenCalledWith(expect.stringContaining('port 5480'));
  });

  it('starts the backend in its own folder so it finds its appsettings.json (#75)', () => {
    const spawnImpl = vi.fn(() => Object.assign(new EventEmitter(), { kill: vi.fn() }) as unknown as ChildProcess);
    const executable = packagedBackendPath(path.join('install', 'resources'), 'win32');
    const backend = new BackendProcess(executable, vi.fn(), spawnImpl);

    backend.start();

    expect(spawnImpl).toHaveBeenCalledWith(executable, expect.any(Array), expect.objectContaining({ cwd: path.join('install', 'resources', 'backend') }));
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
