import { useCallback, useEffect, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Card, CardDescription, CardTitle } from '@/components/ui/card';
import { useApp } from '@/lib/app-context';
import type { PairedBrowser, PairingCode } from '@/types/api';

/** While a code is showing, check this often whether the extension has used it. */
export const PAIRING_POLL_MS = 3000;

function countdown(msLeft: number): string {
  const seconds = Math.max(0, Math.ceil(msLeft / 1000));
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}

/** Settings > Paired browsers (#33): pair the extension with a one-time code and revoke browsers. */
export function PairedBrowsersCard() {
  const { api } = useApp();
  const [browsers, setBrowsers] = useState<PairedBrowser[] | null>(null);
  const [code, setCode] = useState<PairingCode | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const [expired, setExpired] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const apply = useCallback((list: PairedBrowser[]) => {
    setBrowsers((previous) => {
      // A new browser means the code was used, so stop showing it.
      if (previous && list.length > previous.length) {
        setCode(null);
      }
      return list;
    });
  }, []);

  const refresh = useCallback(() => {
    api.listPairedBrowsers().then(apply, () => setError("Couldn't load paired browsers."));
  }, [api, apply]);

  useEffect(() => {
    api.listPairedBrowsers().then(
      (list) => setBrowsers(list),
      () => setError("Couldn't load paired browsers."),
    );
  }, [api]);

  const expiresAt = code ? Date.parse(code.expiresAt) : null;
  useEffect(() => {
    if (expiresAt === null) {
      return;
    }
    const timer = setInterval(() => {
      const current = Date.now();
      setNow(current);
      if (current >= expiresAt) {
        setCode(null);
        setExpired(true);
      }
    }, 1000);
    const poll = setInterval(refresh, PAIRING_POLL_MS);
    return () => {
      clearInterval(timer);
      clearInterval(poll);
    };
  }, [expiresAt, refresh]);

  async function pair() {
    setError(null);
    try {
      setCode(await api.createPairingCode());
      setNow(Date.now());
      setExpired(false);
    } catch {
      setError("Couldn't create a pairing code.");
    }
  }

  async function revoke(browser: PairedBrowser) {
    setError(null);
    try {
      await api.revokePairedBrowser(browser.id);
      setBrowsers((list) => list?.filter((b) => b.id !== browser.id) ?? null);
    } catch {
      setError("Couldn't revoke that browser.");
    }
  }

  return (
    <Card className="grid gap-3">
      <CardTitle>Paired browsers</CardTitle>
      <CardDescription>
        Pair the InterviewCopilot browser extension without sharing your password. Enter the code in the extension popup.
      </CardDescription>
      <Button className="w-fit" onClick={() => void pair()}>
        Pair a browser
      </Button>
      {code && expiresAt !== null && (
        <div className="grid gap-1">
          <code className="w-fit select-all rounded bg-muted px-3 py-2 text-2xl tracking-widest">{code.code}</code>
          <CardDescription>Expires in {countdown(expiresAt - now)}. It works once.</CardDescription>
        </div>
      )}
      {expired && <CardDescription>That code expired. Pair a browser again for a new one.</CardDescription>}
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
      {browsers?.length === 0 && <CardDescription>No browsers paired yet.</CardDescription>}
      <ul className="grid gap-2">
        {browsers?.map((browser) => (
          <li key={browser.id} className="flex items-center justify-between gap-2 rounded border border-border p-3">
            <div className="grid text-sm">
              <span className="font-medium">{browser.name}</span>
              <span className="text-xs text-muted-foreground">
                Paired {new Date(browser.createdAt).toLocaleString()}. Last used:{' '}
                {browser.lastUsedAt ? new Date(browser.lastUsedAt).toLocaleString() : 'never'}
              </span>
            </div>
            <Button size="sm" variant="ghost" aria-label={`Revoke ${browser.name}`} onClick={() => void revoke(browser)}>
              Revoke
            </Button>
          </li>
        ))}
      </ul>
    </Card>
  );
}
