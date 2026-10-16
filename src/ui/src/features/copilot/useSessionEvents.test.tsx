import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiClient } from '@/lib/api';
import { AppProvider } from '@/lib/app-context';
import { createWebPlatform, type Platform } from '@/platform';
import { FakeWebSocket } from '@/test/fakeWebSocket';
import { useSessionEvents, type SessionEvent } from './useSessionEvents';

function setup(platform: Platform = createWebPlatform()) {
  const api = new ApiClient('http://api.test', vi.fn() as unknown as typeof fetch, 'token');
  const wrapper = ({ children }: { children: ReactNode }) => (
    <AppProvider api={api} platform={platform}>
      {children}
    </AppProvider>
  );
  return { wrapper, platform };
}

async function flush() {
  await act(async () => {
    await Promise.resolve();
  });
}

describe('useSessionEvents', () => {
  beforeEach(() => {
    FakeWebSocket.reset();
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  const create = (url: string) => new FakeWebSocket(url) as unknown as WebSocket;

  it('connects with the access token and delivers parsed frames', async () => {
    const platform = createWebPlatform();
    await platform.setAuthTokens({ accessToken: 'tok en', refreshToken: 'r' });
    const { wrapper } = setup(platform);
    const events: SessionEvent[] = [];

    const { result } = renderHook(() => useSessionEvents('s1', (event) => events.push(event), create), { wrapper });
    await flush();

    expect(FakeWebSocket.last.url).toBe('ws://api.test/ws/sessions/s1/events?access_token=tok%20en');
    expect(result.current.connected).toBe(false);
    act(() => FakeWebSocket.last.open());
    expect(result.current.connected).toBe(true);
    act(() => {
      FakeWebSocket.last.emit({ id: 1, type: 'question.detected', data: { text: 'Why?' } });
      FakeWebSocket.last.emit('not json');
      FakeWebSocket.last.emit({ id: 2, data: {} });
      FakeWebSocket.last.emit(42);
    });
    expect(events).toEqual([{ id: 1, type: 'question.detected', data: { text: 'Why?' } }]);
  });

  it('reconnects with 0.5 s, 1 s, 2 s and then 4 s delays, resetting after a successful open', async () => {
    const platform = createWebPlatform();
    await platform.setAuthTokens({ accessToken: 't', refreshToken: 'r' });
    const { wrapper } = setup(platform);
    renderHook(() => useSessionEvents('s1', () => undefined, create), { wrapper });
    await flush();

    const expectReconnectAfter = async (ms: number) => {
      const before = FakeWebSocket.instances.length;
      act(() => FakeWebSocket.last.drop());
      await act(async () => {
        await vi.advanceTimersByTimeAsync(ms - 1);
      });
      expect(FakeWebSocket.instances).toHaveLength(before);
      await act(async () => {
        await vi.advanceTimersByTimeAsync(1);
      });
      expect(FakeWebSocket.instances).toHaveLength(before + 1);
    };

    await expectReconnectAfter(500);
    await expectReconnectAfter(1_000);
    await expectReconnectAfter(2_000);
    await expectReconnectAfter(4_000);
    await expectReconnectAfter(4_000);

    act(() => FakeWebSocket.last.open());
    await expectReconnectAfter(500);
  });

  it('retries without a socket while there is no auth token', async () => {
    const { wrapper } = setup();
    renderHook(() => useSessionEvents('s1', () => undefined, create), { wrapper });
    await flush();
    expect(FakeWebSocket.instances).toHaveLength(0);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(500);
    });

    expect(FakeWebSocket.instances).toHaveLength(0);
  });

  it('closes the socket and stops reconnecting on unmount', async () => {
    const platform = createWebPlatform();
    await platform.setAuthTokens({ accessToken: 't', refreshToken: 'r' });
    const { wrapper } = setup(platform);
    const { unmount } = renderHook(() => useSessionEvents('s1', () => undefined, create), { wrapper });
    await flush();
    const socket = FakeWebSocket.last;

    unmount();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(10_000);
    });

    expect(socket.closed).toBe(true);
    expect(FakeWebSocket.instances).toHaveLength(1);
  });

  it('does not open a socket when unmounted before the token resolves', async () => {
    const platform = createWebPlatform();
    await platform.setAuthTokens({ accessToken: 't', refreshToken: 'r' });
    const { wrapper } = setup(platform);
    const { unmount } = renderHook(() => useSessionEvents('s1', () => undefined, create), { wrapper });

    unmount();
    await flush();

    expect(FakeWebSocket.instances).toHaveLength(0);
  });

  it('uses the global WebSocket by default', async () => {
    vi.stubGlobal('WebSocket', FakeWebSocket);
    try {
      const platform = createWebPlatform();
      await platform.setAuthTokens({ accessToken: 't', refreshToken: 'r' });
      const { wrapper } = setup(platform);
      renderHook(() => useSessionEvents('s1', () => undefined), { wrapper });
      await flush();

      expect(FakeWebSocket.instances).toHaveLength(1);
    } finally {
      vi.unstubAllGlobals();
    }
  });
});
