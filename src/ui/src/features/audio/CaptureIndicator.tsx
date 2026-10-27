import { CAPTURE_LABELS, useCaptureStatus } from './useCaptureStatus';

export { CAPTURE_POLL_MS } from './useCaptureStatus';

/** Shows whether both audio tracks are flowing during a live session (#14). */
export function CaptureIndicator({ active }: { active: boolean }) {
  const status = useCaptureStatus(active);
  if (!status) {
    return null;
  }

  const ok = status.state === 'live';
  return (
    <div role="status" className="grid gap-1 rounded-[var(--radius-lg)] border border-border px-3 py-2 text-sm">
      <div className="flex items-center gap-2">
        <span aria-hidden className={`size-2 rounded-full ${ok ? 'bg-green-500' : 'bg-amber-500'}`} />
        <span className="font-medium">{CAPTURE_LABELS[status.state]}</span>
        <span className="text-muted-foreground">
          · Interviewer {status.interviewer ? '✓' : '✗'} · Mic {status.mic ? '✓' : '✗'}
        </span>
      </div>
      {status.message && <p className="text-xs text-muted-foreground">{status.message}</p>}
    </div>
  );
}
