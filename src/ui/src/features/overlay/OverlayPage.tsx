import { useEffect, useState } from 'react';
import { useParams } from 'react-router';
import { LiveAssistant } from '@/features/copilot/LiveAssistant';
import { useApp } from '@/lib/app-context';

/**
 * Rendered inside the desktop app's transparent, always-on-top overlay window (FR-17).
 * Window opacity is applied by the desktop shell from the opacity setting.
 */
export function OverlayPage() {
  const { sessionId = '' } = useParams();
  const { api } = useApp();
  // The overlay has no session object, so it reads the auto-answer setting once before showing the panel.
  const [autoAnswer, setAutoAnswer] = useState<boolean | null>(null);

  useEffect(() => {
    let cancelled = false;
    api
      .getSession(sessionId)
      .then((session) => !cancelled && setAutoAnswer(session.autoAnswer))
      .catch(() => !cancelled && setAutoAnswer(true));
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
          {autoAnswer !== null && <LiveAssistant sessionId={sessionId} compact autoAnswer={autoAnswer} />}
        </div>
      </div>
    </div>
  );
}
