import { describe, expect, it } from 'vitest';
import { INVALID_LINK, UNREADABLE, normaliseTxtName, parseLink } from './job-link';

describe('parseLink', () => {
  it('accepts http and https links and trims whitespace', () => {
    expect(parseLink('https://jobs.test/1')?.href).toBe('https://jobs.test/1');
    expect(parseLink('  http://jobs.test/a?b=1 \n')?.href).toBe('http://jobs.test/a?b=1');
  });

  it('rejects other schemes, plain text and empty input', () => {
    expect(parseLink('ftp://jobs.test/1')).toBeNull();
    expect(parseLink('file:///c:/x')).toBeNull();
    expect(parseLink('javascript:alert(1)')).toBeNull();
    expect(parseLink('Senior engineer at Acme')).toBeNull();
    expect(parseLink('jobs.test/1')).toBeNull();
    expect(parseLink('')).toBeNull();
  });
});

describe('normaliseTxtName', () => {
  it('trims and appends .txt', () => {
    expect(normaliseTxtName('  Role at Acme ')).toBe('Role at Acme.txt');
  });

  it('keeps an existing .txt extension in any case', () => {
    expect(normaliseTxtName('JD.txt')).toBe('JD.txt');
    expect(normaliseTxtName(' JD.TXT ')).toBe('JD.TXT');
  });

  it('returns an empty string for a blank name so callers can pick a fallback', () => {
    expect(normaliseTxtName('')).toBe('');
    expect(normaliseTxtName('   ')).toBe('');
  });
});

describe('messages', () => {
  it('match the backend wording', () => {
    expect(INVALID_LINK).toBe('Enter a link that starts with http:// or https://.');
    expect(UNREADABLE).toBe("We couldn't read a job description from that page. Paste the text instead.");
  });
});
