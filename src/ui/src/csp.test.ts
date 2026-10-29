import { describe, expect, it } from 'vitest';
import html from '../index.html?raw';

function connectSources(): string[] {
  const policy = /http-equiv="Content-Security-Policy"\s+content="([^"]+)"/.exec(html)?.[1] ?? '';
  const directive = policy
    .split(';')
    .map((part) => part.trim())
    .find((part) => part.startsWith('connect-src'));
  return directive?.split(/\s+/).slice(1) ?? [];
}

describe('Content Security Policy', () => {
  it('lets the page call the local backend over HTTP and its events WebSocket (#77)', () => {
    const sources = connectSources();

    expect(sources).toContain('http://127.0.0.1:5480');
    // An http: source doesn't cover ws: under CSP Level 3, so the socket needs its own entry.
    expect(sources).toContain('ws://127.0.0.1:5480');
  });
});
