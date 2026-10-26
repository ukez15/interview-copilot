/** A readable name for this browser, shown in the desktop app's "Paired browsers" list. */
export function browserName(userAgent: string): string {
  // Order matters: Edge and Chrome both say "Chrome", Edge adds "Edg/".
  if (/Edg\//.test(userAgent)) {
    return 'Edge';
  }
  if (/Firefox\//.test(userAgent)) {
    return 'Firefox';
  }
  if (/Chrome\//.test(userAgent)) {
    return 'Chrome';
  }
  return 'Browser';
}
