// AudioWorklet processor: downmixes each 128-sample render block and posts batches to the capture
// window, which resamples and frames them. Runs on the audio thread; keep it allocation-light.

declare const sampleRate: number;
declare function registerProcessor(name: string, processor: unknown): void;
declare class AudioWorkletProcessor {
  readonly port: MessagePort;
}

const BATCH_SAMPLES = 2048;

class PcmTapProcessor extends AudioWorkletProcessor {
  private batch = new Float32Array(BATCH_SAMPLES);
  private filled = 0;

  constructor() {
    super();
    this.port.postMessage({ type: 'rate', sampleRate });
  }

  process(inputs: Float32Array[][]): boolean {
    const channels = inputs[0];
    const first = channels?.[0];
    if (!channels || !first) {
      return true;
    }
    for (let i = 0; i < first.length; i++) {
      let sum = 0;
      for (const channel of channels) {
        sum += channel[i] ?? 0;
      }
      this.batch[this.filled++] = sum / channels.length;
      if (this.filled === BATCH_SAMPLES) {
        this.port.postMessage({ type: 'samples', samples: this.batch }, [this.batch.buffer]);
        this.batch = new Float32Array(BATCH_SAMPLES);
        this.filled = 0;
      }
    }
    return true;
  }
}

registerProcessor('pcm-tap', PcmTapProcessor);
