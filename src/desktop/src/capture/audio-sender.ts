import { backoffDelay } from './backoff.js';
import { FrameBuffer } from './frame-buffer.js';

export interface SocketLike {
  readyState: number;
  binaryType: string;
  onopen: (() => void) | null;
  onclose: ((event: { code: number }) => void) | null;
  send(data: Uint8Array): void;
  close(code?: number): void;
}

export type SenderStatus = 'connecting' | 'live' | 'reconnecting' | 'error' | 'stopped';

const OPEN = 1;
/** Server closes with 1008 (policy) / 1003 (bad data) mean "don't retry". */
const FATAL_CLOSE_CODES = new Set([1003, 1008]);
/** 5 s of audio for both speakers at 10 frames/s each. */
export const DEFAULT_BUFFER_FRAMES = 100;

export interface AudioSenderOptions {
  url: string;
  createSocket: (url: string) => SocketLike;
  onStatus: (status: SenderStatus) => void;
  bufferFrames?: number;
}

/** Streams frames over one WebSocket, buffering ≤ 5 s and reconnecting with backoff after drops. */
export class AudioSender {
  private socket: SocketLike | null = null;
  private readonly buffer: FrameBuffer;
  private attempt = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private stopped = false;
  private dropped = 0;

  constructor(private readonly options: AudioSenderOptions) {
    this.buffer = new FrameBuffer(options.bufferFrames ?? DEFAULT_BUFFER_FRAMES);
  }

  get droppedFrames(): number {
    return this.dropped;
  }

  start(): void {
    this.stopped = false;
    this.options.onStatus('connecting');
    this.connect();
  }

  send(frame: Uint8Array): void {
    if (this.stopped) {
      return;
    }
    if (this.socket?.readyState === OPEN) {
      this.socket.send(frame);
    } else {
      this.dropped += this.buffer.push(frame);
    }
  }

  stop(): void {
    this.stopped = true;
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
    this.socket?.close(1000);
    this.socket = null;
    this.buffer.drain();
    this.options.onStatus('stopped');
  }

  private connect(): void {
    const socket = this.options.createSocket(this.options.url);
    socket.binaryType = 'arraybuffer';
    socket.onopen = () => {
      this.attempt = 0;
      for (const frame of this.buffer.drain()) {
        socket.send(frame);
      }
      this.options.onStatus('live');
    };
    socket.onclose = (event) => {
      if (this.stopped || this.socket !== socket) {
        return;
      }
      if (FATAL_CLOSE_CODES.has(event.code)) {
        this.stopped = true;
        this.options.onStatus('error');
        return;
      }
      this.options.onStatus('reconnecting');
      this.timer = setTimeout(() => this.connect(), backoffDelay(this.attempt++));
    };
    this.socket = socket;
  }
}
