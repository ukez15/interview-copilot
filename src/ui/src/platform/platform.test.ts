import { describe, expect, it, vi } from 'vitest';
import { createWebPlatform, DEFAULT_SETTINGS, detectPlatform, type DesktopBridge } from './index';

const status = { state: 'live' as const, interviewer: true, mic: true, message: null };

function fakeBridge(): DesktopBridge {
  return {
    getAuthToken: vi.fn().mockResolvedValue('t'),
    setAuthTokens: vi.fn().mockResolvedValue(undefined),
    refreshAuth: vi.fn().mockResolvedValue('new'),
    getApiKey: vi.fn().mockResolvedValue('k'),
    setApiKey: vi.fn().mockResolvedValue(undefined),
    deleteApiKey: vi.fn().mockResolvedValue(undefined),
    setOverlayOpacity: vi.fn().mockResolvedValue(undefined),
    showOverlay: vi.fn().mockResolvedValue(undefined),
    hideOverlay: vi.fn().mockResolvedValue(undefined),
    getSettings: vi.fn().mockResolvedValue(DEFAULT_SETTINGS),
    setSettings: vi.fn().mockResolvedValue(DEFAULT_SETTINGS),
    startCapture: vi.fn().mockResolvedValue(status),
    stopCapture: vi.fn().mockResolvedValue(status),
    getCaptureStatus: vi.fn().mockResolvedValue(status),
    openDataFolder: vi.fn().mockResolvedValue(undefined),
  };
}

describe('platform', () => {
  it('uses the desktop bridge when the preload exposed it', async () => {
    const bridge = fakeBridge();
    const platform = detectPlatform({ interviewCopilot: bridge } as unknown as Window);

    expect(platform.kind).toBe('desktop');
    await platform.getAuthToken();
    await platform.setAuthTokens({ accessToken: 'x', refreshToken: 'y' });
    expect(await platform.refreshAuth()).toBe('new');
    await platform.getApiKey('deepgram');
    await platform.setApiKey('deepgram', 'k');
    await platform.deleteApiKey('deepgram');
    await platform.setOverlayOpacity(0.5);
    await platform.showOverlay('s1');
    await platform.hideOverlay();
    await platform.getSettings();
    await platform.setSettings({ autoStartOnMeeting: true });
    await platform.startCapture('s1');
    await platform.stopCapture();
    expect(await platform.getCaptureStatus()).toEqual(status);
    await platform.openDataFolder();
    expect(bridge.openDataFolder).toHaveBeenCalled();

    expect(bridge.setAuthTokens).toHaveBeenCalledWith({ accessToken: 'x', refreshToken: 'y' });
    expect(bridge.startCapture).toHaveBeenCalledWith('s1');
    expect(bridge.stopCapture).toHaveBeenCalled();
    expect(bridge.setApiKey).toHaveBeenCalledWith('deepgram', 'k');
    expect(bridge.showOverlay).toHaveBeenCalledWith('s1');
    expect(bridge.setSettings).toHaveBeenCalledWith({ autoStartOnMeeting: true });
  });

  it('falls back to a web platform that never stores API keys', async () => {
    const platform = detectPlatform({} as Window);

    expect(platform.kind).toBe('web');
    await expect(platform.setApiKey('openai', 'k')).rejects.toThrow('desktop app');
    expect(await platform.getApiKey('openai')).toBeNull();
    await expect(platform.openDataFolder()).rejects.toThrow('Opening folders needs the desktop app.');
  });

  it('keeps web settings and token in memory', async () => {
    const platform = createWebPlatform();

    await platform.setAuthTokens({ accessToken: 'abc', refreshToken: 'ref' });
    const updated = await platform.setSettings({ overlayOpacity: 0.5 });
    await platform.deleteApiKey('x');
    await platform.setOverlayOpacity(0.5);
    await platform.showOverlay('s');
    await platform.hideOverlay();

    expect(await platform.getAuthToken()).toBe('abc');
    expect(await platform.refreshAuth()).toBeNull();
    await platform.setAuthTokens(null);
    expect(await platform.getAuthToken()).toBeNull();
    expect(updated).toEqual({ overlayOpacity: 0.5, autoStartOnMeeting: false, micDeviceId: null });
    expect(await platform.getSettings()).toEqual(updated);
  });
});
