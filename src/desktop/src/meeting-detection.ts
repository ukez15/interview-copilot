import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);

/** Desktop meeting apps we recognise by process name (lower-case, without .exe). */
export const MEETING_APPS: Record<string, string> = {
  zoom: 'Zoom',
  'zoom.us': 'Zoom',
  'ms-teams': 'Microsoft Teams',
  teams: 'Microsoft Teams',
  webex: 'Webex',
  ciscowebexstart: 'Webex',
  atmgr: 'Webex',
  slack: 'Slack',
  skype: 'Skype',
  discord: 'Discord',
};

export function normalizeProcessName(name: string): string {
  const base = name.trim().split(/[\\/]/).pop() ?? '';
  return base.toLowerCase().replace(/\.(exe|app)$/, '');
}

/** Returns the meeting app's display name if one is running, otherwise null. */
export function detectMeetingApp(processNames: readonly string[]): string | null {
  for (const name of processNames) {
    const app = MEETING_APPS[normalizeProcessName(name)];
    if (app) {
      return app;
    }
  }
  return null;
}

/** Parses `tasklist /fo csv /nh` output: "Image Name","PID",... per line. */
export function parseTasklist(output: string): string[] {
  return output
    .split(/\r?\n/)
    .map((line) => /^"([^"]+)"/.exec(line)?.[1])
    .filter((name): name is string => Boolean(name));
}

/** Parses `ps -axo comm=` output: one executable path per line. */
export function parsePs(output: string): string[] {
  return output
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean);
}

/** Store (packaged) meeting apps, by the package family name Windows uses in the consent store. */
export const MEETING_PACKAGES: Record<string, string> = {
  MSTeams_8wekyb3d8bbwe: 'Microsoft Teams',
};

/** Per-app microphone use that Windows records for its "app is using your microphone" indicator. */
export const MICROPHONE_CONSENT_STORE =
  'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\microphone';

export interface MicrophoneUse {
  /** Package family name, or the exe path with `#` for `\` for desktop (NonPackaged) apps. */
  key: string;
  inUse: boolean;
}

/**
 * Parses `reg query <consent store> /s` output. An app holds the mic while LastUsedTimeStop is 0;
 * keys without usage timestamps (never used, or the NonPackaged folder itself) are skipped.
 */
export function parseConsentStore(output: string): MicrophoneUse[] {
  const uses: MicrophoneUse[] = [];
  let key = '';
  let start: bigint | null = null;
  let stop: bigint | null = null;
  const flush = (): void => {
    if (key && start !== null && stop !== null) {
      uses.push({ key, inUse: start > 0n && stop === 0n });
    }
  };
  for (const line of output.split(/\r?\n/)) {
    if (line.startsWith('HKEY_')) {
      flush();
      key = line.trim().split('\\').pop() ?? '';
      start = stop = null;
      continue;
    }
    const [, name, hex] = /^\s+(LastUsedTimeStart|LastUsedTimeStop)\s+REG_QWORD\s+(0x[0-9a-f]+)/i.exec(line) ?? [];
    if (name === 'LastUsedTimeStart' && hex) {
      start = BigInt(hex);
    } else if (name && hex) {
      stop = BigInt(hex);
    }
  }
  flush();
  return uses;
}

export function meetingAppForConsentKey(key: string): string | null {
  if (key.includes('#')) {
    return MEETING_APPS[normalizeProcessName(key.split('#').pop() ?? '')] ?? null;
  }
  return MEETING_PACKAGES[key] ?? null;
}

/** A meeting is on while its app holds the mic and is still running (a crash can leave a stale entry). */
export function detectActiveMeeting(processNames: readonly string[], consentStoreOutput: string): string | null {
  const running = new Set(processNames.map((name) => MEETING_APPS[normalizeProcessName(name)]).filter(Boolean));
  for (const use of parseConsentStore(consentStoreOutput)) {
    const app = use.inUse ? meetingAppForConsentKey(use.key) : null;
    if (app && running.has(app)) {
      return app;
    }
  }
  return null;
}

export type RunCommand = (command: string, args: string[]) => Promise<string>;

const runCommand: RunCommand = async (command, args) => (await execFileAsync(command, args, { windowsHide: true })).stdout;

/**
 * The meeting in progress, or null. Windows: a meeting app holding the mic. macOS has no equivalent
 * signal without a native helper yet, so it still uses "meeting app running". If anything can't be
 * read, there's no prompt: a missed prompt beats a false one.
 */
export async function currentMeeting(platform: NodeJS.Platform = process.platform, run: RunCommand = runCommand): Promise<string | null> {
  try {
    if (platform === 'win32') {
      const [tasks, consentStore] = await Promise.all([
        run('tasklist', ['/fo', 'csv', '/nh']),
        run('reg', ['query', MICROPHONE_CONSENT_STORE, '/s']),
      ]);
      return detectActiveMeeting(parseTasklist(tasks), consentStore);
    }
    return detectMeetingApp(parsePs(await run('ps', ['-axo', 'comm='])));
  } catch {
    return null;
  }
}

export type MeetingDecision = 'ignore' | 'ask' | 'start';

/**
 * FR-16 / NFR-12: when a meeting newly appears, ask the user unless they enabled auto-start.
 * A meeting that was already seen (or dismissed) doesn't trigger again until it ends.
 */
export function decide(previous: string | null, current: string | null, autoStart: boolean): MeetingDecision {
  if (!current || current === previous) {
    return 'ignore';
  }
  return autoStart ? 'start' : 'ask';
}

/** Sent to the renderer (IpcEvents.meetingDetected) once the user agreed, or auto-start applies. */
export interface MeetingDetectedEvent {
  app: string;
  autoStart: boolean;
}

/**
 * NFR-12: the renderer is only told to go ahead after the user clicked "Start InterviewCopilot"
 * (autoStart false: it shows a picker) or when auto-start is on (autoStart true).
 */
export function meetingEventFor(decision: MeetingDecision, app: string, userAccepted: boolean): MeetingDetectedEvent | null {
  if (decision === 'start') {
    return { app, autoStart: true };
  }
  return decision === 'ask' && userAccepted ? { app, autoStart: false } : null;
}
