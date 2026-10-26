/** Bounded FIFO for frames waiting on a reconnect. When full, the oldest frame is dropped. */
export class FrameBuffer {
  private frames: Uint8Array[] = [];

  constructor(private readonly capacity: number) {
    if (!(capacity > 0)) {
      throw new Error('Capacity must be positive.');
    }
  }

  get size(): number {
    return this.frames.length;
  }

  /** Returns how many frames were dropped to make room (0 or 1). */
  push(frame: Uint8Array): number {
    this.frames.push(frame);
    if (this.frames.length > this.capacity) {
      this.frames.shift();
      return 1;
    }
    return 0;
  }

  drain(): Uint8Array[] {
    const frames = this.frames;
    this.frames = [];
    return frames;
  }
}
