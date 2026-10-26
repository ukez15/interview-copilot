import { renderStatus } from './popup/render';
import type { Request, StatusResponse } from './shared/messages';

const send = (request: Request) => chrome.runtime.sendMessage(request) as Promise<StatusResponse>;

document.getElementById('pair')!.addEventListener('submit', (event) => {
  event.preventDefault();
  const input = document.getElementById('code') as HTMLInputElement;
  const code = input.value.trim();
  input.value = '';
  void send({ kind: 'pair', code }).then((status) => renderStatus(document, status));
});

document.getElementById('unpair')!.addEventListener('click', () => {
  void send({ kind: 'unpair' }).then((status) => renderStatus(document, status));
});

void send({ kind: 'status' }).then((status) => renderStatus(document, status));
