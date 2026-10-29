import { useEffect, useState } from 'react';
import { useApp } from '@/lib/app-context';
import type { CaptureStatus } from '@/platform';

export const CAPTURE_POLL_MS = 1_000;

export const CAPTURE_LABELS: Record<CaptureStatus['state'], string> = {
  idle: 'Not listening',
  connecting: 'Connecting…',
  live: 'Listening',
  reconnecting: 'Reconnecting…',
  error: 'Capture problem',
  stopped: 'Stopped',
};

/** Polls the desktop capture status every {@link CAPTURE_POLL_MS} while active; null until the first answer. */
export function useCaptureStatus(active: boolean): CaptureStatus | null {
  const { platform } = useApp();
  const [status, setStatus] = useState<CaptureStatus | null>(null);

  useEffect(() => {
    if (!active) {
      return;
    }
    let cancelled = false;
    const poll = () =>
      void platform.getCaptureStatus().then((next) => {
        if (!cancelled) {
          setStatus(next);
        }
      });
    poll();
    const timer = setInterval(poll, CAPTURE_POLL_MS);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [active, platform]);

  return active ? status : null;
}
