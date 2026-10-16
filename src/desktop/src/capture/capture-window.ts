// Runs in the hidden, sandboxed capture window (no Node). Captures system audio (loopback, granted by the
// main process's display-media handler) and the mic, and streams 16 kHz PCM16 frames to the backend.
import { AudioSender, type SocketLike } from './audio-sender.js';
import { FrameAssembler, Resampler, SPEAKER_CANDIDATE, SPEAKER_INTERVIEWER, type SpeakerByte } from './pcm.js';
import { audioSocketUrl, type CaptureConfig, type CaptureState, type CaptureStatus } from './status.js';

interface CaptureBridge {
  getConfig(): Promise<CaptureConfig>;
  reportStatus(status: CaptureStatus): Promise<void>;
}

declare global {
  interface Window {
    captureBridge: CaptureBridge;
  }
}

const status: CaptureStatus = { state: 'connecting', interviewer: false, mic: false, message: null };

function report(patch: Partial<CaptureStatus>): void {
  Object.assign(status, patch);
  void window.captureBridge.reportStatus({ ...status });
}

function tap(context: AudioContext, stream: MediaStream, speaker: SpeakerByte, sender: AudioSender): void {
  const source = context.createMediaStreamSource(stream);
  const node = new AudioWorkletNode(context, 'pcm-tap');
  let resampler = new Resampler(context.sampleRate);
  const assembler = new FrameAssembler(speaker, (frame) => sender.send(frame));
  node.port.onmessage = (event: MessageEvent<{ type: 'rate'; sampleRate: number } | { type: 'samples'; samples: Float32Array }>) => {
    if (event.data.type === 'rate') {
      resampler = new Resampler(event.data.sampleRate);
    } else {
      assembler.push(resampler.process(event.data.samples));
    }
  };
  // Muted sink keeps the worklet pulled without playing anything back.
  const mute = context.createGain();
  mute.gain.value = 0;
  source.connect(node).connect(mute).connect(context.destination);
}

async function systemAudio(): Promise<MediaStream> {
  // Electron's display-media handler answers with the screen + 'loopback' audio (Windows).
  const stream = await navigator.mediaDevices.getDisplayMedia({ audio: true, video: true });
  for (const track of stream.getVideoTracks()) {
    track.stop();
  }
  if (stream.getAudioTracks().length === 0) {
    throw new Error('No system audio track.');
  }
  return stream;
}

async function microphone(deviceId: string | null): Promise<MediaStream> {
  return navigator.mediaDevices.getUserMedia({
    audio: {
      deviceId: deviceId ? { exact: deviceId } : undefined,
      channelCount: 1,
      echoCancellation: true,
      noiseSuppression: true,
    },
  });
}

async function main(): Promise<void> {
  const config = await window.captureBridge.getConfig();
  const sender = new AudioSender({
    url: audioSocketUrl(config),
    createSocket: (url) => new WebSocket(url) as unknown as SocketLike,
    onStatus: (state: CaptureState) =>
      report({
        state,
        message: state === 'error' ? 'The backend refused the audio stream. Is the session still live?' : status.message,
      }),
  });

  const context = new AudioContext();
  await context.audioWorklet.addModule('pcm-worklet.js');
  sender.start();

  try {
    tap(context, await systemAudio(), SPEAKER_INTERVIEWER, sender);
    report({ interviewer: true });
  } catch {
    report({ interviewer: false, message: "Couldn't capture system audio, so the other side of the call won't be transcribed." });
  }

  try {
    const mic = await microphone(config.micDeviceId);
    tap(context, mic, SPEAKER_CANDIDATE, sender);
    for (const track of mic.getAudioTracks()) {
      track.onended = () => report({ mic: false, message: 'Microphone disconnected. The interviewer is still being captured.' });
    }
    report({ mic: true });
  } catch {
    report({ mic: false, message: 'Microphone unavailable. The interviewer is still being captured.' });
  }

  if (!status.interviewer && !status.mic) {
    sender.stop();
    report({ state: 'error', message: 'No audio could be captured. Check Windows sound and microphone privacy settings.' });
  }
}

void main();
