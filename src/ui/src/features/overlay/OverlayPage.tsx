import { useEffect, useRef, useState } from 'react';
import { useParams } from 'react-router';
import { LiveAssistant } from '@/features/copilot/LiveAssistant';
import { PracticePanel } from '@/features/practice/PracticePanel';
import { useApp } from '@/lib/app-context';
import type { Session } from '@/types/api';
import { useAutoScroll } from './useAutoScroll';

type OverlayView = Pick<Session, 'mode' | 'status' | 'autoAnswer'>;

/** Used when the session can't be read: the answer assistant with auto-answer on, as before #74. */
const FALLBACK: OverlayView = { mode: 'LiveInterview', status: 'Live', autoAnswer: true };

/**
 * Rendered inside the desktop app's transparent, always-on-top overlay window (FR-17). Mock interviews run their
 * practice here (#74); every other mode shows the answer assistant.
 * Window opacity is applied by the desktop shell from the opacity setting.
 */
export function OverlayPage() {
  const { sessionId = '' } = useParams();
  const { api } = useApp();
  // The overlay has no session object, so it reads the session once before choosing what to show.
  const [view, setView] = useState<OverlayView | null>(null);
  const scroller = useRef<HTMLDivElement>(null);
  // A mock interviewee may scroll up to re-read the question; on a live call the newest answer always matters most.
  const practising = view?.mode === 'MockInterview';
  useAutoScroll(scroller, practising ? 'follow' : 'always');

  useEffect(() => {
    let cancelled = false;
    api
      .getSession(sessionId)
      .then((session) => !cancelled && setView(session))
      .catch(() => !cancelled && setView(FALLBACK));
    return () => {
      cancelled = true;
    };
  }, [api, sessionId]);

  useEffect(() => {
    document.documentElement.classList.add('overlay');
    return () => document.documentElement.classList.remove('overlay');
  }, []);

  return (
    <div className="h-screen p-2">
      <div
        ref={scroller}
        className="h-full overflow-auto rounded-xl border border-border bg-background/95 p-3 shadow-lg [-webkit-app-region:drag]"
      >
        <div className="[-webkit-app-region:no-drag]">
          {view && practising && <PracticePanel sessionId={sessionId} live={view.status === 'Live'} />}
          {view && !practising && <LiveAssistant sessionId={sessionId} compact autoAnswer={view.autoAnswer} />}
        </div>
      </div>
    </div>
  );
}
