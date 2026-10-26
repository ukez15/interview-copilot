import { promises as fs } from 'node:fs';
import path from 'node:path';

export interface DesktopSettings {
  overlayOpacity: number;
  /** FR-16: off by default — the app asks before it starts listening. */
  autoStartOnMeeting: boolean;
  /** Microphone to capture; null = the OS default device. */
  micDeviceId: string | null;
}

export const DEFAULT_SETTINGS: DesktopSettings = { overlayOpacity: 0.85, autoStartOnMeeting: false, micDeviceId: null };
const MAX_DEVICE_ID_LENGTH = 256;
export const OPACITY_RANGE = { min: 0.3, max: 1 } as const;

export function clampOpacity(value: unknown): number {
  const number = typeof value === 'number' && Number.isFinite(value) ? value : DEFAULT_SETTINGS.overlayOpacity;
  return Math.min(OPACITY_RANGE.max, Math.max(OPACITY_RANGE.min, number));
}

/** Validates untrusted input (a settings file or an IPC payload) into safe settings. */
export function sanitize(input: unknown, base: DesktopSettings = DEFAULT_SETTINGS): DesktopSettings {
  const value = (typeof input === 'object' && input !== null ? input : {}) as Partial<Record<keyof DesktopSettings, unknown>>;
  return {
    overlayOpacity: 'overlayOpacity' in value ? clampOpacity(value.overlayOpacity) : base.overlayOpacity,
    autoStartOnMeeting: typeof value.autoStartOnMeeting === 'boolean' ? value.autoStartOnMeeting : base.autoStartOnMeeting,
    micDeviceId: 'micDeviceId' in value ? deviceId(value.micDeviceId) : base.micDeviceId,
  };
}

function deviceId(value: unknown): string | null {
  return typeof value === 'string' && value.length > 0 && value.length <= MAX_DEVICE_ID_LENGTH ? value : null;
}

/** Settings persisted as JSON in the app's user-data folder. */
export class SettingsStore {
  private cache: DesktopSettings | null = null;

  constructor(private readonly filePath: string) {}

  static inDirectory(directory: string): SettingsStore {
    return new SettingsStore(path.join(directory, 'settings.json'));
  }

  async get(): Promise<DesktopSettings> {
    if (this.cache) {
      return this.cache;
    }
    try {
      this.cache = sanitize(JSON.parse(await fs.readFile(this.filePath, 'utf8')));
    } catch {
      this.cache = { ...DEFAULT_SETTINGS };
    }
    return this.cache;
  }

  async update(patch: unknown): Promise<DesktopSettings> {
    const next = sanitize(patch, await this.get());
    await fs.mkdir(path.dirname(this.filePath), { recursive: true });
    await fs.writeFile(this.filePath, JSON.stringify(next, null, 2), 'utf8');
    this.cache = next;
    return next;
  }
}
