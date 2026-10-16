import { useState } from 'react';
import { Card, CardDescription, CardTitle } from '@/components/ui/card';
import { useApp } from '@/lib/app-context';
import type { Session, SessionSettingsInput } from '@/types/api';
import { SessionForm } from './SessionForm';

/** #10: edit answer settings while a session is Draft or Live; nothing to edit once Ended. */
export function SessionSettingsCard({ session, onSaved }: { session: Session; onSaved: (session: Session) => void }) {
  const { api } = useApp();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  if (session.status === 'Ended') {
    return null;
  }

  async function save(input: SessionSettingsInput) {
    setBusy(true);
    setError(null);
    setSaved(false);
    try {
      const { answerLength, framework, pinnedQuestionType, sessionPrompt } = input;
      onSaved(await api.updateSessionSettings(session.id, { answerLength, framework, pinnedQuestionType, sessionPrompt }));
      setSaved(true);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save settings.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card className="grid gap-4">
      <div>
        <CardTitle>Answer settings</CardTitle>
        <CardDescription>Changes apply to the next answer.</CardDescription>
      </div>
      <SessionForm
        variant="edit"
        busy={busy}
        initial={{ title: session.title, mode: session.mode, answerLength: session.answerLength, framework: session.framework, pinnedQuestionType: session.pinnedQuestionType, sessionPrompt: session.sessionPrompt }}
        onSubmit={(input) => void save(input)}
      />
      {saved && <p className="text-sm text-muted-foreground">Settings saved. They apply to the next answer.</p>}
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
    </Card>
  );
}
