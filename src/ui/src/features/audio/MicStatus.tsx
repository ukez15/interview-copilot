import { CAPTURE_LABELS, useCaptureStatus } from './useCaptureStatus';

/** Shown while practice isn't taking an answer: the backend ignores the mic until the question is complete (#78). */
export const MIC_PAUSED = 'Mic ⏸ Paused until the question is complete';

/**
 * One-line mic state for mock interviews (#74), so "it can't hear me" shows instead of "Listening…" forever.
 * `paused` says the mic is heard but not used yet; a mic problem still shows, since it would block the answer.
 */
export function MicStatus({ active, paused = false }: { active: boolean; paused?: boolean }) {
  const status = useCaptureStatus(active);
  if (!status) {
    return null;
  }

  const connecting = status.state === 'connecting' || status.state === 'reconnecting';
  const heard = status.state === 'live' && status.mic;
  const text = heard
    ? paused
      ? MIC_PAUSED
      : `Mic ✓ ${CAPTURE_LABELS.live}`
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
