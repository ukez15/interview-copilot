import { useEffect, useRef } from 'react';
import { useNavigate } from 'react-router';
import { useApp } from '@/lib/app-context';
import { handleMeetingDetected } from './handle-meeting';

/** Renders nothing; routes the desktop's meeting-detected event (#16). Lives inside the router. */
export function MeetingListener() {
  const { api, platform } = useApp();
  const navigate = useNavigate();
  // `navigate` changes identity on every route change; the subscription must outlive that.
  const navigateRef = useRef(navigate);

  useEffect(() => {
    navigateRef.current = navigate;
  }, [navigate]);

  useEffect(
    () => platform.onMeetingDetected((event) => void handleMeetingDetected(event, { api, platform, navigate: (path) => void navigateRef.current(path) })),
    [api, platform],
  );

  return null;
}
