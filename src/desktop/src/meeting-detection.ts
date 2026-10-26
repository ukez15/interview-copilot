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

export async function listProcessNames(platform: NodeJS.Platform = process.platform): Promise<string[]> {
  if (platform === 'win32') {
    const { stdout } = await execFileAsync('tasklist', ['/fo', 'csv', '/nh'], { windowsHide: true });
    return parseTasklist(stdout);
  }
  const { stdout } = await execFileAsync('ps', ['-axo', 'comm=']);
  return parsePs(stdout);
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
