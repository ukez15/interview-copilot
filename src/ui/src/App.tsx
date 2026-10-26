import { HashRouter, Navigate, Route, Routes } from 'react-router';
import { MeetingListener } from '@/features/meeting/MeetingListener';
import { StartFromMeetingPage } from '@/features/meeting/StartFromMeetingPage';
import { SignInPage } from '@/features/auth/SignInPage';
import { OverlayPage } from '@/features/overlay/OverlayPage';
import { SessionPage } from '@/features/sessions/SessionPage';
import { SessionsPage } from '@/features/sessions/SessionsPage';
import { SettingsPage } from '@/features/settings/SettingsPage';
import { TranscriptPage } from '@/features/transcripts/TranscriptPage';
import { useApp } from '@/lib/app-context';

/** Hash routing works from the desktop's app:// protocol and any static web host (Mode C). */
export function App() {
  const { signedIn } = useApp();
  if (!signedIn) {
    return <SignInPage />;
  }

  return (
    <HashRouter>
      <MeetingListener />
      <Routes>
        <Route path="/" element={<SessionsPage />} />
        <Route path="/start-from-meeting" element={<StartFromMeetingPage />} />
        <Route path="/sessions/:sessionId" element={<SessionPage />} />
        <Route path="/sessions/:sessionId/transcript" element={<TranscriptPage />} />
        <Route path="/overlay/:sessionId" element={<OverlayPage />} />
        <Route path="/settings" element={<SettingsPage />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </HashRouter>
  );
}
