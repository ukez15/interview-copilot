import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { ConfirmDialog } from '@/components/ui/confirm-dialog';
import { useApp } from '@/lib/app-context';
import type { Session } from '@/types/api';

/** Shown on the disabled button; matches the backend's 409 message. */
export const END_BEFORE_DELETING = 'End the session before deleting it.';

/**
 * Deletes a session after the user confirms (#80). Disabled while the session is live; a failed delete keeps
 * the dialog open with the reason, so nothing looks deleted that wasn't.
 */
export function DeleteSessionButton({ session, onDeleted }: { session: Session; onDeleted: (sessionId: string) => void }) {
  const { api } = useApp();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const live = session.status === 'Live';

  async function confirm() {
    setBusy(true);
    setError(null);
    try {
      await api.deleteSession(session.id);
      setOpen(false);
      onDeleted(session.id);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not delete the session.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <Button
        variant="outline"
        size="sm"
        disabled={live}
        title={live ? END_BEFORE_DELETING : undefined}
        onClick={() => {
          setError(null);
          setOpen(true);
        }}
      >
        Delete
      </Button>
      <ConfirmDialog
        open={open}
        onOpenChange={setOpen}
        title="Delete this session?"
        description={`“${session.title}” and its documents, transcript and practice attempts will be permanently deleted. This can't be undone.`}
        confirmLabel="Delete"
        onConfirm={() => void confirm()}
        busy={busy}
        error={error}
      />
    </>
  );
}
