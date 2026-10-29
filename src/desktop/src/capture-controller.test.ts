import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { describe, expect, it, vi } from 'vitest';
import { CaptureController, type CaptureWindowLike } from './capture-controller';
import { audioSocketUrl } from './capture/status';
import { CaptureChannels } from './ipc-channels';
import { sanitize } from './settings-store';

const SESSION = '3f2b8c1e-0d4a-4c8e-9f1a-2b3c4d5e6f70';

function fakeWindow(id: number) {
  let closed: (() => void) | null = null;
  let destroyed = false;
  const window: CaptureWindowLike & { close(): void } = {
    webContentsId: id,
    destroy: vi.fn(() => {
      destroyed = true;
      closed?.();
    }),
    isDestroyed: () => destroyed,
    onClosed: (listener) => (closed = listener),
    close: () => {
      destroyed = true;
      closed?.();
    },
  };
  return window;
}

function sessionLookup(mode: string): typeof fetch {
  return vi.fn(() => Promise.resolve(new Response(JSON.stringify({ id: SESSION, mode }), { status: 200 }))) as unknown as typeof fetch;
}

function setup(token: string | null = 'tok', fetchImpl: typeof fetch = sessionLookup('LiveInterview')) {
  const windows: ReturnType<typeof fakeWindow>[] = [];
  const controller = new CaptureController({
    createWindow: () => {
      const window = fakeWindow(windows.length + 10);
      windows.push(window);
      return window;
    },
    getToken: () => token,
    getMicDeviceId: () => Promise.resolve('usb-headset'),
    backendUrl: 'http://127.0.0.1:5480',
    fetchImpl,
  });
  return { controller, windows };
}

describe('CaptureController', () => {
  it('starts one hidden window per session and gives the config only to that window', async () => {
    const { controller, windows } = setup();

    expect((await controller.start(SESSION)).state).toBe('connecting');

    expect(controller.configFor(10)).toEqual({ sessionId: SESSION, token: 'tok', micDeviceId: 'usb-headset', backendUrl: 'http://127.0.0.1:5480', micOnly: false });
    expect(() => controller.configFor(99)).toThrow('Not the capture window');
    expect(controller.isCaptureWindow(10)).toBe(true);
    expect(windows).toHaveLength(1);
  });

  it('captures only the microphone for mock interview sessions, after looking the session up with the token', async () => {
    const lookup = sessionLookup('MockInterview');
    const { controller } = setup('tok', lookup);

    await controller.start(SESSION);

    expect(controller.configFor(10).micOnly).toBe(true);
    expect(lookup).toHaveBeenCalledWith(`http://127.0.0.1:5480/api/sessions/${SESSION}`, { headers: { Authorization: 'Bearer tok' } });
  });

  it('falls back to normal capture when the session lookup fails', async () => {
    const failing = vi.fn(() => Promise.reject(new Error('offline'))) as unknown as typeof fetch;
    const notFound = vi.fn(() => Promise.resolve(new Response('{}', { status: 404 }))) as unknown as typeof fetch;

    const first = setup('tok', failing);
    await first.controller.start(SESSION);
    const second = setup('tok', notFound);
    await second.controller.start(SESSION);

    expect(first.controller.configFor(10).micOnly).toBe(false);
    expect(second.controller.configFor(10).micOnly).toBe(false);
  });

  it('knows which session it is capturing, so the app can end it on quit (#84)', async () => {
    const { controller } = setup();
    expect(controller.liveSessionId).toBeNull();

    await controller.start(SESSION);
    expect(controller.liveSessionId).toBe(SESSION);

    controller.stop();
    expect(controller.liveSessionId).toBeNull();
  });

  it('accepts status only from the capture window and sanitizes it', async () => {
    const { controller } = setup();
    await controller.start(SESSION);

    controller.report(99, { state: 'live', interviewer: true, mic: true, message: null });
    expect(controller.status.state).toBe('connecting');

    controller.report(10, { state: 'live', interviewer: true, mic: 'yes', message: 'x'.repeat(500) });
    expect(controller.status).toEqual({ state: 'live', interviewer: true, mic: false, message: 'x'.repeat(300) });

    controller.report(10, { state: 'hacked' });
    expect(controller.status.state).toBe('error');
    controller.report(10, null);
    expect(controller.status).toEqual({ state: 'error', interviewer: false, mic: false, message: null });
  });

  it('replaces the previous capture when starting again, and stops cleanly', async () => {
    const { controller, windows } = setup();
    await controller.start(SESSION);
    await controller.start(SESSION);

    expect(windows[0]!.destroy).toHaveBeenCalled();
    expect(controller.isCaptureWindow(10)).toBe(false);
    expect(controller.isCaptureWindow(11)).toBe(true);

    expect(controller.stop().state).toBe('stopped');
    expect(() => controller.configFor(11)).toThrow();
    expect(controller.stop().state).toBe('stopped');
  });

  it('reports an error when the capture window dies on its own', async () => {
    const { controller, windows } = setup();
    await controller.start(SESSION);

    windows[0]!.close();

    expect(controller.status).toMatchObject({ state: 'error', message: expect.stringContaining('stopped unexpectedly') });
    expect(controller.isCaptureWindow(10)).toBe(false);
  });

  it('refuses to start without sign-in or with a malformed session id', async () => {
    await expect(setup(null).controller.start(SESSION)).rejects.toThrow('Sign in');
    await expect(setup().controller.start('../../etc')).rejects.toThrow('Invalid session id');
  });
});

describe('capture contract helpers', () => {
  it('builds the audio socket URL with an escaped token', () => {
    expect(audioSocketUrl({ sessionId: SESSION, token: 'a b+c', micDeviceId: null, backendUrl: 'http://127.0.0.1:5480', micOnly: false })).toBe(
      `ws://127.0.0.1:5480/ws/sessions/${SESSION}/audio?access_token=a%20b%2Bc`,
    );
  });

  it('keeps the mic setting valid', () => {
    expect(sanitize({ micDeviceId: 'abc' }).micDeviceId).toBe('abc');
    expect(sanitize({ micDeviceId: '' }).micDeviceId).toBeNull();
    expect(sanitize({ micDeviceId: 42 }).micDeviceId).toBeNull();
    expect(sanitize({}).micDeviceId).toBeNull();
  });

  it('capture preload uses exactly the capture channels', async () => {
    const source = await readFile(path.join(import.meta.dirname, 'capture-preload.ts'), 'utf8');
    const used = [...source.matchAll(/invoke\('([^']+)'/g)].map((m) => m[1]).sort();

    expect(used).toEqual(Object.values(CaptureChannels).sort());
  });
});
