import { useEffect, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Card, CardDescription, CardTitle } from '@/components/ui/card';
import { useApp } from '@/lib/app-context';

/** Settings > Privacy: where local data lives, what is in it and how to delete it. */
export function PrivacyCard() {
  const { api, platform } = useApp();
  const [folder, setFolder] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.getStorage().then(
      (info) => setFolder(info.dataDirectory),
      () => setError("Couldn't load the data folder."),
    );
  }, [api]);

  async function open() {
    setError(null);
    try {
      await platform.openDataFolder();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Couldn't open the data folder.");
    }
  }

  return (
    <Card className="grid gap-3">
      <CardTitle>Privacy</CardTitle>
      <CardDescription>
        Everything stays on this computer: your account, sessions, transcripts, documents and the downloaded speech model.
      </CardDescription>
      {folder && (
        <div className="flex items-center gap-2">
          <code className="select-all break-all rounded bg-muted px-2 py-1 text-sm">{folder}</code>
          {platform.kind === 'desktop' && (
            <Button size="sm" variant="ghost" onClick={() => void open()}>
              Open folder
            </Button>
          )}
        </div>
      )}
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
      <CardDescription>To delete everything, quit InterviewCopilot, then delete this folder.</CardDescription>
    </Card>
  );
}
