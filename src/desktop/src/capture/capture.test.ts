import { describe, expect, it, vi } from 'vitest';
import { AudioSender, type SocketLike } from './audio-sender.js';
import { backoffDelay } from './backoff.js';
import { FrameBuffer } from './frame-buffer.js';
import {
  FRAME_BYTES,
  FRAME_SAMPLES,
  FrameAssembler,
  Resampler,
  SPEAKER_CANDIDATE,
  SPEAKER_INTERVIEWER,
  TARGET_SAMPLE_RATE,
  downmix,
  encodeFrame,
  floatToPcm16,
} from './pcm.js';

describe('pcm', () => {
  it('uses the agreed contract: 16 kHz, 100 ms frames, 3,200 payload bytes', () => {
    expect(TARGET_SAMPLE_RATE).toBe(16_000);
    expect(FRAME_SAMPLES).toBe(1_600);
    expect(FRAME_BYTES).toBe(3_201);
    expect([SPEAKER_INTERVIEWER, SPEAKER_CANDIDATE]).toEqual([0, 1]);
  });

  it('downmixes channels by averaging', () => {
    expect(Array.from(downmix([new Float32Array([1, 0.5]), new Float32Array([0, 0.5])]))).toEqual([0.5, 0.5]);
    expect(Array.from(downmix([new Float32Array([0.25])]))).toEqual([0.25]);
    expect(downmix([])).toHaveLength(0);
  });

  it('converts float samples to clamped PCM16', () => {
    expect(Array.from(floatToPcm16(new Float32Array([0, 1, -1, 2, -2, 0.5])))).toEqual([0, 32767, -32768, 32767, -32768, 16384]);
  });

  it('resamples 48 kHz to 16 kHz across chunk boundaries without drift', () => {
    const resampler = new Resampler(48_000);
    let total = 0;
    for (let i = 0; i < 100; i++) {
      total += resampler.process(new Float32Array(128).fill(0.5)).length;
    }

    expect(total).toBeGreaterThanOrEqual(4266);
    expect(total).toBeLessThanOrEqual(4267);
  });

  it('interpolates linearly and passes through at 16 kHz', () => {
    expect(Array.from(new Resampler(32_000).process(new Float32Array([0, 1, 0, 1])))).toEqual([0, 0]);
    expect(Array.from(new Resampler(16_000).process(new Float32Array([0.1, 0.2])))).toEqual([
      expect.closeTo(0.1, 5),
      expect.closeTo(0.2, 5),
    ]);
    expect(() => new Resampler(0)).toThrow();
  });

  it('encodes a frame as speaker byte + little-endian PCM16', () => {
    const frame = encodeFrame(SPEAKER_CANDIDATE, new Int16Array([1, -2]));

    expect(Array.from(frame)).toEqual([1, 1, 0, 0xfe, 0xff]);
  });

  it('assembles exact 100 ms frames from arbitrary chunk sizes', () => {
    const frames: Uint8Array[] = [];
    const assembler = new FrameAssembler(SPEAKER_INTERVIEWER, (f) => frames.push(f));

    assembler.push(new Float32Array(1000).fill(0.5));
    expect(frames).toHaveLength(0);
    assembler.push(new Float32Array(2500).fill(0.5));

    expect(frames).toHaveLength(2);
    expect(frames.every((f) => f.length === FRAME_BYTES && f[0] === SPEAKER_INTERVIEWER)).toBe(true);
  });
});

describe('FrameBuffer', () => {
  it('keeps at most its capacity, dropping the oldest', () => {
    const buffer = new FrameBuffer(2);
    const a = new Uint8Array([1]);
    const b = new Uint8Array([2]);
    const c = new Uint8Array([3]);

    expect(buffer.push(a)).toBe(0);
    buffer.push(b);
    expect(buffer.push(c)).toBe(1);

    expect(buffer.drain()).toEqual([b, c]);
    expect(buffer.size).toBe(0);
    expect(() => new FrameBuffer(0)).toThrow();
  });
});

describe('backoff', () => {
  it('follows 0.5 s, 1 s, 2 s, then every 4 s', () => {
    expect([0, 1, 2, 3, 10].map(backoffDelay)).toEqual([500, 1000, 2000, 4000, 4000]);
  });
});

class FakeSocket implements SocketLike {
  static instances: FakeSocket[] = [];
  readyState = 0;
  sent: Uint8Array[] = [];
  onopen: (() => void) | null = null;
  onclose: ((event: { code: number }) => void) | null = null;
  binaryType = '';

  constructor(readonly url: string) {
    FakeSocket.instances.push(this);
  }

  send(data: Uint8Array): void {
    this.sent.push(data);
  }

  close(): void {
    this.readyState = 3;
  }

  open(): void {
    this.readyState = 1;
    this.onopen?.();
  }

  drop(code = 1006): void {
    this.readyState = 3;
    this.onclose?.({ code });
  }
}

describe('AudioSender', () => {
  function setup() {
    FakeSocket.instances = [];
    vi.useFakeTimers();
    const statuses: string[] = [];
    const sender = new AudioSender({
      url: 'ws://127.0.0.1:5480/ws/sessions/s1/audio?access_token=t',
      createSocket: (url) => new FakeSocket(url),
      onStatus: (status) => statuses.push(status),
      bufferFrames: 3,
    });
    return { sender, statuses };
  }

  it('buffers until open, then flushes in order and sends live', () => {
    const { sender, statuses } = setup();
    sender.start();
    sender.send(new Uint8Array([1]));
    sender.send(new Uint8Array([2]));

    FakeSocket.instances[0]!.open();
    sender.send(new Uint8Array([3]));

    expect(FakeSocket.instances[0]!.binaryType).toBe('arraybuffer');
    expect(FakeSocket.instances[0]!.sent.map((f) => f[0])).toEqual([1, 2, 3]);
    expect(statuses).toEqual(['connecting', 'live']);
    vi.useRealTimers();
  });

  it('reconnects with backoff after a drop and replays the bounded buffer', () => {
    const { sender, statuses } = setup();
    sender.start();
    FakeSocket.instances[0]!.open();
    FakeSocket.instances[0]!.drop();
    for (const n of [4, 5, 6, 7]) sender.send(new Uint8Array([n]));

    vi.advanceTimersByTime(499);
    expect(FakeSocket.instances).toHaveLength(1);
    vi.advanceTimersByTime(1);
    FakeSocket.instances[1]!.open();

    expect(FakeSocket.instances[1]!.sent.map((f) => f[0])).toEqual([5, 6, 7]);
    expect(sender.droppedFrames).toBe(1);
    expect(statuses).toEqual(['connecting', 'live', 'reconnecting', 'live']);
    vi.useRealTimers();
  });

  it('stops for good on stop() and on a policy close from the server', () => {
    const { sender, statuses } = setup();
    sender.start();
    FakeSocket.instances[0]!.drop(1008);
    vi.advanceTimersByTime(10_000);

    expect(FakeSocket.instances).toHaveLength(1);
    expect(statuses.at(-1)).toBe('error');

    sender.stop();
    sender.send(new Uint8Array([9]));
    expect(FakeSocket.instances[0]!.sent).toHaveLength(0);
    expect(statuses.at(-1)).toBe('stopped');
    vi.useRealTimers();
  });
});
