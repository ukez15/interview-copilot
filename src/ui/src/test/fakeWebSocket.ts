/** Stand-in for WebSocket so tests never open a real socket. Drive it with open/message/drop. */
export class FakeWebSocket {
  static instances: FakeWebSocket[] = [];

  onopen: (() => void) | null = null;
  onmessage: ((event: { data: string }) => void) | null = null;
  onclose: (() => void) | null = null;
  onerror: (() => void) | null = null;
  closed = false;

  constructor(readonly url: string) {
    FakeWebSocket.instances.push(this);
  }

  static reset(): void {
    FakeWebSocket.instances = [];
  }

  static get last(): FakeWebSocket {
    const socket = FakeWebSocket.instances.at(-1);
    if (!socket) {
      throw new Error('No socket was opened.');
    }
    return socket;
  }

  open(): void {
    this.onopen?.();
  }

  emit(frame: unknown): void {
    this.onmessage?.({ data: typeof frame === 'string' ? frame : JSON.stringify(frame) });
  }

  /** The server or network dropped the connection. */
  drop(): void {
    this.closed = true;
    this.onclose?.();
  }

  close(): void {
    this.closed = true;
    this.onclose?.();
  }
}
