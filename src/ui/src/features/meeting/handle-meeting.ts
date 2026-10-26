import type { ApiClient } from '@/lib/api';
import type { MeetingDetectedEvent, Platform } from '@/platform';
import { startLiveSession } from '@/features/sessions/start-live';
import { meetingTitle } from './meeting-title';

const CAPTURE_BUSY = new Set(['connecting', 'live', 'reconnecting']);

export const pickerPath = (app: string) => `/start-from-meeting?app=${encodeURIComponent(app)}`;

export interface MeetingDeps {
  api: ApiClient;
  platform: Platform;
  navigate: (path: string) => void;
  now?: () => Date;
}

/**
 * #16: the desktop saw a meeting and the user agreed (or auto-start is on). Without auto-start, or when
 * there is nothing to copy, the user picks the session; with it, the most recent session is copied and started.
 * A second detection while capture is already running is ignored.
 */
export async function handleMeetingDetected(event: MeetingDetectedEvent, { api, platform, navigate, now = () => new Date() }: MeetingDeps): Promise<void> {
  const status = await platform.getCaptureStatus().catch(() => null);
  if (status && CAPTURE_BUSY.has(status.state)) {
    return;
  }
  if (!event.autoStart) {
    navigate(pickerPath(event.app));
    return;
  }
  try {
    const sessions = await api.listSessions();
    const latest = sessions.reduce<(typeof sessions)[number] | null>(
      (best, candidate) => (!best || Date.parse(candidate.createdAt) > Date.parse(best.createdAt) ? candidate : best),
      null,
    );
    if (!latest) {
      navigate(pickerPath(event.app));
      return;
    }
    const created = await api.createSession({
      title: meetingTitle(event.app, now()),
      mode: latest.mode,
      answerLength: latest.answerLength,
      framework: latest.framework,
      pinnedQuestionType: latest.pinnedQuestionType,
      sessionPrompt: latest.sessionPrompt,
      copyFromSessionId: latest.id,
    });
    await startLiveSession(api, platform, created.id);
    await platform.showOverlay(created.id);
    navigate(`/sessions/${created.id}`);
  } catch {
    navigate(pickerPath(event.app));
  }
}
