import { CAPTURE_LABELS, useCaptureStatus } from './useCaptureStatus';

/** One-line mic state for mock interviews (#74), so "it can't hear me" shows instead of "Listening…" forever. */
export function MicStatus({ active }: { active: boolean }) {
  const status = useCaptureStatus(active);
  if (!status) {
    return null;
  }

  const connecting = status.state === 'connecting' || status.state === 'reconnecting';
  const heard = status.state === 'live' && status.mic;
  const text = heard
    ? `Mic ✓ ${CAPTURE_LABELS.live}`
    : connecting
      ? CAPTURE_LABELS[status.state]
      : `Mic ✗ ${status.message ?? CAPTURE_LABELS[status.state]}`;

  return (
    <p role="status" className="flex items-center gap-2 text-sm">
      <span aria-hidden className={`size-2 shrink-0 rounded-full ${heard ? 'bg-green-500' : 'bg-amber-500'}`} />
      <span className="break-words">{text}</span>
    </p>
  );
}
