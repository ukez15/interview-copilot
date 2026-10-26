import type { StatusResponse } from '../shared/messages';

export function renderStatus(doc: Document, status: StatusResponse): void {
  doc.getElementById('pair')!.hidden = status.paired;
  doc.getElementById('paired')!.hidden = !status.paired;
  doc.getElementById('error')!.textContent = status.error ?? '';
  const live = status.sessions.map((s) => s.title).join(', ');
  doc.getElementById('sessions')!.textContent = live
    ? `Live: ${live}. Open your meeting tab to see answers there.`
    : 'No live session. Start one in the desktop app.';
}
