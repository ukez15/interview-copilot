export const INVALID_LINK = 'Enter a link that starts with http:// or https://.';
export const UNREADABLE = "We couldn't read a job description from that page. Paste the text instead.";

/** An editable job description: what the user sees before it is saved as a `.txt` document. */
export interface JobDraft {
  name: string;
  text: string;
}

/** The link's http(s) URL, or null when it isn't one. */
export function parseLink(value: string): URL | null {
  try {
    const url = new URL(value.trim());
    return url.protocol === 'http:' || url.protocol === 'https:' ? url : null;
  } catch {
    return null;
  }
}

/** The trimmed name with `.txt` appended unless it already ends with it (any case); empty when the name is blank. */
export function normaliseTxtName(name: string): string {
  const trimmed = name.trim();
  if (!trimmed) {
    return '';
  }
  return /\.txt$/i.test(trimmed) ? trimmed : `${trimmed}.txt`;
}
