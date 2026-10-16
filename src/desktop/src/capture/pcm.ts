// Audio contract (docs/atlas-gotcha/audio-capture-windows.md): 16 kHz mono PCM16 LE, 100 ms frames,
// each prefixed with one speaker byte. Pure functions so they run in the capture window and in tests.

export const TARGET_SAMPLE_RATE = 16_000;
export const FRAME_MS = 100;
export const FRAME_SAMPLES = (TARGET_SAMPLE_RATE * FRAME_MS) / 1000;
/** Speaker byte + PCM16 payload. */
export const FRAME_BYTES = 1 + FRAME_SAMPLES * 2;

export const SPEAKER_INTERVIEWER = 0;
export const SPEAKER_CANDIDATE = 1;
export type SpeakerByte = typeof SPEAKER_INTERVIEWER | typeof SPEAKER_CANDIDATE;

/** Averages channels into one mono channel. */
export function downmix(channels: readonly Float32Array[]): Float32Array {
  const first = channels[0];
  if (!first) {
    return new Float32Array(0);
  }
  if (channels.length === 1) {
    return first;
  }
  const mono = new Float32Array(first.length);
  for (const channel of channels) {
    for (let i = 0; i < mono.length; i++) {
      mono[i]! += (channel[i] ?? 0) / channels.length;
    }
  }
  return mono;
}

/** Converts [-1, 1] floats to signed 16-bit samples, clamping out-of-range values. */
export function floatToPcm16(samples: Float32Array): Int16Array {
  const pcm = new Int16Array(samples.length);
  for (let i = 0; i < samples.length; i++) {
    const s = Math.max(-1, Math.min(1, samples[i]!));
    pcm[i] = s < 0 ? Math.round(s * 0x8000) : Math.round(s * 0x7fff);
  }
  return pcm;
}

/**
 * Streaming linear-interpolation resampler to 16 kHz. Keeps its position across chunks so long
 * sessions don't drift. Speech recognition doesn't need a higher-quality filter at 16 kHz.
 */
export class Resampler {
  private readonly step: number;
  private position = 0;

  constructor(inputRate: number) {
    if (!(inputRate > 0)) {
      throw new Error('Input sample rate must be positive.');
    }
    this.step = inputRate / TARGET_SAMPLE_RATE;
  }

  process(input: Float32Array): Float32Array {
    const output: number[] = [];
    // position is measured in input samples, relative to the start of this chunk.
    while (this.position < input.length) {
      const index = Math.floor(this.position);
      const fraction = this.position - index;
      const a = input[index]!;
      const b = index + 1 < input.length ? input[index + 1]! : a;
      output.push(a + (b - a) * fraction);
      this.position += this.step;
    }
    this.position -= input.length;
    return Float32Array.from(output);
  }
}

/** One wire frame: [speaker][PCM16 little-endian...]. */
export function encodeFrame(speaker: SpeakerByte, pcm: Int16Array): Uint8Array {
  const frame = new Uint8Array(1 + pcm.length * 2);
  frame[0] = speaker;
  const view = new DataView(frame.buffer);
  for (let i = 0; i < pcm.length; i++) {
    view.setInt16(1 + i * 2, pcm[i]!, true);
  }
  return frame;
}

/** Collects 16 kHz samples and emits exact 100 ms frames for one speaker. */
export class FrameAssembler {
  private readonly pending = new Float32Array(FRAME_SAMPLES);
  private filled = 0;

  constructor(
    private readonly speaker: SpeakerByte,
    private readonly emit: (frame: Uint8Array) => void,
  ) {}

  push(samples: Float32Array): void {
    let offset = 0;
    while (offset < samples.length) {
      const take = Math.min(FRAME_SAMPLES - this.filled, samples.length - offset);
      this.pending.set(samples.subarray(offset, offset + take), this.filled);
      this.filled += take;
      offset += take;
      if (this.filled === FRAME_SAMPLES) {
        this.emit(encodeFrame(this.speaker, floatToPcm16(this.pending)));
        this.filled = 0;
      }
    }
  }
}
