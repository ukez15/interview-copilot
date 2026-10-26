import { spawn, type ChildProcess } from 'node:child_process';
import path from 'node:path';
import { BACKEND_URL } from './ipc-channels';

export const MAX_RESTARTS = 3;
export const HEALTH_TIMEOUT_MS = 30_000;
export const HEALTH_POLL_MS = 500;

/** Where the published backend lives inside a packaged app (electron-builder extraResources). */
export function packagedBackendPath(resourcesPath: string, platform: NodeJS.Platform = process.platform): string {
  const executable = platform === 'win32' ? 'InterviewCopilot.Api.exe' : 'InterviewCopilot.Api';
  return path.join(resourcesPath, 'backend', executable);
}

export const PROBE_TIMEOUT_MS = 2_000;

/** One /health check; anything but an OK answer within the timeout counts as "not running". */
export async function isHealthy(
  fetchImpl: (url: string, init?: { signal?: AbortSignal }) => Promise<{ ok: boolean }> = (url, init) => fetch(url, init),
): Promise<boolean> {
  try {
    return (await fetchImpl(`${BACKEND_URL}/health`, { signal: AbortSignal.timeout(PROBE_TIMEOUT_MS) })).ok;
  } catch {
    return false;
  }
}

export type BackendSource = 'reused' | 'spawned' | 'external';

/**
 * #36: a packaged app starts its bundled backend unless one already answers on the port (a second copy
 * of the app, or a dev backend). A reused backend isn't ours, so the app must not stop it on quit.
 * In development the backend always runs separately (`pnpm dev:backend`).
 */
export async function ensureBackend(deps: {
  packaged: boolean;
  probe: () => Promise<boolean>;
  spawnBackend: () => void;
}): Promise<BackendSource> {
  if (!deps.packaged) {
    return 'external';
  }
  if (await deps.probe()) {
    return 'reused';
  }
  deps.spawnBackend();
  return 'spawned';
}

/** Polls /health until the backend answers or the timeout passes. */
export async function waitForHealthy(
  fetchImpl: (url: string) => Promise<{ ok: boolean }> = (url) => fetch(url),
  timeoutMs = HEALTH_TIMEOUT_MS,
  pollMs = HEALTH_POLL_MS,
  now: () => number = Date.now,
  sleep: (ms: number) => Promise<void> = (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
): Promise<boolean> {
  const deadline = now() + timeoutMs;
  while (now() < deadline) {
    try {
      if ((await fetchImpl(`${BACKEND_URL}/health`)).ok) {
        return true;
      }
    } catch {
      // not up yet
    }
    await sleep(pollMs);
  }
  return false;
}

type Spawn = (command: string, args: string[], options: { stdio: 'ignore'; windowsHide: boolean }) => ChildProcess;

/**
 * Starts the bundled backend and restarts it up to MAX_RESTARTS times if it crashes
 * (atlas/desktop.md). In development the backend is started separately (`pnpm dev:backend`).
 */
export class BackendProcess {
  private child: ChildProcess | null = null;
  private restarts = 0;
  private stopping = false;

  constructor(
    private readonly executable: string,
    private readonly onFatal: (message: string) => void,
    private readonly spawnImpl: Spawn = spawn,
  ) {}

  get restartCount(): number {
    return this.restarts;
  }

  start(): void {
    this.stopping = false;
    this.child = this.spawnImpl(this.executable, ['--urls', BACKEND_URL], { stdio: 'ignore', windowsHide: true });
    this.child.once('exit', (code) => this.handleExit(code));
  }

  stop(): void {
    this.stopping = true;
    this.child?.kill();
    this.child = null;
  }

  private handleExit(code: number | null): void {
    if (this.stopping) {
      return;
    }
    if (this.restarts >= MAX_RESTARTS) {
      this.onFatal(
        `The InterviewCopilot backend stopped (exit code ${code ?? 'unknown'}) and couldn't be restarted. ` +
          'Is port 5480 already in use by another program?',
      );
      return;
    }
    this.restarts += 1;
    this.start();
  }
}
