import { useEffect, useState } from 'react';
import { useParams } from 'react-router';
import { LiveAssistant } from '@/features/copilot/LiveAssistant';
import { PracticePanel } from '@/features/practice/PracticePanel';
import { useApp } from '@/lib/app-context';
import type { Session } from '@/types/api';

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
      <div className="h-full overflow-auto rounded-xl border border-border bg-background/95 p-3 shadow-lg [-webkit-app-region:drag]">
        <div className="[-webkit-app-region:no-drag]">
          {view?.mode === 'MockInterview' && <PracticePanel sessionId={sessionId} live={view.status === 'Live'} />}
          {view && view.mode !== 'MockInterview' && <LiveAssistant sessionId={sessionId} compact autoAnswer={view.autoAnswer} />}
        </div>
      </div>
    </div>
  );
}
