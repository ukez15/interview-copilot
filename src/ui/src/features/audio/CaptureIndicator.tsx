import { useEffect, useState } from 'react';
import { useApp } from '@/lib/app-context';
import type { CaptureStatus } from '@/platform';

export const CAPTURE_POLL_MS = 1_000;

const LABELS: Record<CaptureStatus['state'], string> = {
  idle: 'Not listening',
  connecting: 'Connecting…',
  live: 'Listening',
  reconnecting: 'Reconnecting…',
  error: 'Capture problem',
  stopped: 'Stopped',
};

/** Shows whether both audio tracks are flowing during a live session (#14). */
export function CaptureIndicator({ active }: { active: boolean }) {
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

  if (!active || !status) {
    return null;
  }

  const ok = status.state === 'live';
  return (
    <div role="status" className="grid gap-1 rounded-[var(--radius-lg)] border border-border px-3 py-2 text-sm">
      <div className="flex items-center gap-2">
        <span aria-hidden className={`size-2 rounded-full ${ok ? 'bg-green-500' : 'bg-amber-500'}`} />
        <span className="font-medium">{LABELS[status.state]}</span>
        <span className="text-muted-foreground">
          · Interviewer {status.interviewer ? '✓' : '✗'} · Mic {status.mic ? '✓' : '✗'}
        </span>
      </div>
      {status.message && <p className="text-xs text-muted-foreground">{status.message}</p>}
    </div>
  );
}
