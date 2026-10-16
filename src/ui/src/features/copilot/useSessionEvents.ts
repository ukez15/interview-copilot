import { useEffect, useRef, useState } from 'react';
import { useApp } from '@/lib/app-context';

/** One frame from `/ws/sessions/{id}/events`: `{"id","type","data"}`. */
export interface SessionEvent {
  id: number;
  type: string;
  data: unknown;
}

/** Reconnect delays: 0.5 s, 1 s, 2 s, then every 4 s. */
export const RECONNECT_DELAYS_MS: readonly number[] = [500, 1_000, 2_000];
export const RECONNECT_MAX_DELAY_MS = 4_000;

export type SocketFactory = (url: string) => WebSocket;

const defaultFactory: SocketFactory = (url) => new WebSocket(url);

function parseEvent(raw: unknown): SessionEvent | null {
  if (typeof raw !== 'string') {
    return null;
  }
  try {
    const frame = JSON.parse(raw) as Partial<SessionEvent> | null;
    if (frame && typeof frame === 'object' && typeof frame.type === 'string') {
      return { id: typeof frame.id === 'number' ? frame.id : 0, type: frame.type, data: frame.data };
    }
  } catch {
    // Not JSON: ignore the frame.
  }
  return null;
}

/**
 * Subscribes to a session's events channel (#22/#23). Reconnects with backoff when the socket drops and
 * closes it on unmount. `onEvent` can change between renders without reconnecting.
 */
export function useSessionEvents(
  sessionId: string,
  onEvent: (event: SessionEvent) => void,
  createSocket: SocketFactory = defaultFactory,
): { connected: boolean } {
  const { api, platform } = useApp();
  const [connected, setConnected] = useState(false);
  const handler = useRef(onEvent);
  const factory = useRef(createSocket);

  useEffect(() => {
    handler.current = onEvent;
    factory.current = createSocket;
  });

  useEffect(() => {
    let disposed = false;
    let attempt = 0;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let socket: WebSocket | undefined;

    const scheduleRetry = () => {
      const delay = RECONNECT_DELAYS_MS[attempt] ?? RECONNECT_MAX_DELAY_MS;
      attempt += 1;
      timer = setTimeout(() => void connect(), delay);
    };

    const connect = async () => {
      const token = await platform.getAuthToken();
      if (disposed) {
        return;
      }
      if (!token) {
        scheduleRetry();
        return;
      }
      const ws = factory.current(`${api.eventsUrl(sessionId)}?access_token=${encodeURIComponent(token)}`);
      socket = ws;
      ws.onopen = () => {
        attempt = 0;
        setConnected(true);
      };
      ws.onmessage = (message: MessageEvent) => {
        const event = parseEvent(message.data);
        if (event) {
          handler.current(event);
        }
      };
      ws.onclose = () => {
        setConnected(false);
        if (!disposed) {
          scheduleRetry();
        }
      };
    };

    void connect();
    return () => {
      disposed = true;
      clearTimeout(timer);
      if (socket) {
        socket.onclose = null;
        socket.close();
      }
    };
  }, [api, platform, sessionId]);

  return { connected };
}
