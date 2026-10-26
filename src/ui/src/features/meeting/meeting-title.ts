const PARTS = new Intl.DateTimeFormat('en-US', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });

/** "<App> meeting – dd MMM HH:mm" in the user's local time, e.g. "Zoom meeting – 16 Oct 14:05". */
export function meetingTitle(app: string, when: Date): string {
  const part = (type: Intl.DateTimeFormatPartTypes) => PARTS.formatToParts(when).find((p) => p.type === type)?.value ?? '';
  return `${app} meeting – ${part('day')} ${part('month')} ${part('hour')}:${part('minute')}`;
}
