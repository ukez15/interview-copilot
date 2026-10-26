import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { fakeApi, json, renderWithApp, sampleSession } from '@/test/helpers';
import type { Session } from '@/types/api';
import { DeleteSessionButton } from './DeleteSessionButton';

function mount(session: Session = { ...sampleSession, status: 'Ended' }, reply: () => Response = () => new Response(null, { status: 204 })) {
  const { api, fetchImpl } = fakeApi(reply);
  const onDeleted = vi.fn();
  renderWithApp(<DeleteSessionButton session={session} onDeleted={onDeleted} />, { api });
  const deletes = () => fetchImpl.mock.calls.filter(([, init]) => init?.method === 'DELETE');
  return { onDeleted, deletes };
}

describe('DeleteSessionButton', () => {
  it('asks before deleting and says what will be lost', async () => {
    const view = mount();

    await userEvent.click(screen.getByRole('button', { name: 'Delete' }));

    const dialog = await screen.findByRole('alertdialog');
    expect(dialog).toHaveTextContent('Delete this session?');
    expect(dialog).toHaveTextContent(
      '“Acme onsite” and its documents, transcript and practice attempts will be permanently deleted. This can\'t be undone.',
    );
    expect(view.deletes()).toHaveLength(0);
  });

  it('deletes nothing when cancelled', async () => {
    const view = mount();

    await userEvent.click(screen.getByRole('button', { name: 'Delete' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Cancel' }));

    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
    expect(view.deletes()).toHaveLength(0);
    expect(view.onDeleted).not.toHaveBeenCalled();
  });

  it('deletes the session once confirmed', async () => {
    const view = mount();

    await userEvent.click(screen.getByRole('button', { name: 'Delete' }));
    const dialog = await screen.findByRole('alertdialog');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Delete' }));

    await waitFor(() => expect(view.onDeleted).toHaveBeenCalledWith('s1'));
    expect(view.deletes()).toHaveLength(1);
    expect(String(view.deletes()[0]?.[0])).toBe('http://api.test/api/sessions/s1');
  });

  it('keeps the dialog open with the reason when the delete fails', async () => {
    const view = mount(undefined, () => json({ detail: 'End the session before deleting it.' }, 409));

    await userEvent.click(screen.getByRole('button', { name: 'Delete' }));
    const dialog = await screen.findByRole('alertdialog');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Delete' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('End the session before deleting it.');
    expect(view.onDeleted).not.toHaveBeenCalled();
  });

  it('is disabled while the session is live, and says why', () => {
    mount({ ...sampleSession, status: 'Live' });

    const button = screen.getByRole('button', { name: 'Delete' });
    expect(button).toBeDisabled();
    expect(button).toHaveAttribute('title', 'End the session before deleting it.');
  });
});
