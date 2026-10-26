# Delete sessions — ATLAS + GOTCHA brief

Status: **approved by the owner with the recommendations (2026-10-26)**, issue #80.
Permanent rules: `GOTCHA-PROJECT.md` and `CLAUDE.md`.

## User story

As a user, I want to delete sessions I no longer need, so my list stays tidy and old interview
material (resume, JD, transcripts) doesn't stay on my machine.

- GIVEN a session that isn't live, WHEN I delete it and confirm, THEN it disappears from my list along
  with its documents, transcript, suggested answers and practice attempts.
- GIVEN I click Delete, WHEN the confirmation appears and I cancel, THEN nothing is deleted.
- GIVEN someone else's session id, WHEN they try to delete it, THEN they get 404 and nothing changes.

---

## ATLAS

### [A] Architect
- **Purpose:** let users permanently remove a session and everything in it.
- **Constraints:** local-only data (SQLite). Owner-only. Deletion is permanent, so it always asks for
  confirmation first. Mode C must stay possible: the API is the only path, and the UI goes through `api.ts`.
- **Already decided:** EF Core cascades from `Session` to documents, transcript segments, answers and
  practice attempts (`EntityConfigurations.cs`), so one `DELETE` removes it all.
- **Out of scope:** bulk delete, a trash/undo bin, automatic clean-up of old sessions, and deleting the
  account (questions 3–4 cover the first two).

### [T] Trace
1. The user clicks **Delete** (question 1 decides where).
2. A confirmation dialog opens (question 2 covers the wording) → **Cancel** stops here.
3. `DELETE /api/sessions/{id}` → `SessionService.DeleteAsync(owner, id)`.
4. 404 if it's not the owner's session; 409 if it's live (question 5).
5. The session is removed, SQLite cascades the rest, and the in-memory practice state for it is dropped.
6. The UI removes it from the list, or goes back to the list from the session page.

### [L] Link
| From | To | Method | Contract | Failure mode |
|---|---|---|---|---|
| UI | API | `DELETE /api/sessions/{id}` (new) | 204 No Content | 404 not yours / 409 live, shown inline |
| SessionService | ISessionRepository | `Remove(session)` (new) + `SaveChanges` | — | Exceptions bubble up as 500 |
| SessionService | PracticeAnswerCollector | `Take(id)` | drop any open question | — |

### [A] Assemble
1. Tests first (Domain/Application, API integration on in-memory SQLite, UI).
2. `ISessionRepository.Remove`, `SessionService.DeleteAsync`, endpoint.
3. UI: `api.deleteSession`, a confirmation dialog, and the Delete buttons.
4. Checks → "What we adjusted" → PR.

### [S] Stress-test
- **Data integrity:** an integration test proves documents, segments, answers and attempts are gone
  after the delete. A failed `SaveChanges` leaves everything in place.
- **Security:** ownership is checked before deleting. Another user's id returns 404, never 403, so it
  doesn't reveal that the session exists.
- **Failure:** the backend is down → an inline error, and the list is unchanged.

---

## GOTCHA

=== GOALS ===
- A confirmed delete removes the session and all its data; a cancelled one changes nothing.
- Only the owner can delete; live sessions follow question 5.
- Coverage of at least 75% per package; CI green.

=== ORCHESTRATION ===
1. Failing tests. 2. Repository + service + endpoint. 3. UI API call, dialog, buttons. 4. Checks → review → PR.

=== TOOLS ===
.NET 10, EF Core (existing), xunit v3, FluentAssertions 7.2.2, React 19.3.1, shadcn/ui (dialog, to
add if the repo doesn't have one; question 6), Vitest 5.0.5.

=== CONTEXT ===
SQLite cascades already configured. Clean Architecture: the endpoint calls `SessionService` only.

=== HEURISTICS ===
DO: always confirm before deleting (yes/no). Check ownership before anything else (yes/no). Remove
in-memory state for the session (yes/no).
DON'T: soft-delete or hide rows (yes/no). Delete without confirmation (yes/no). Add bulk delete (yes/no).

=== ARGS ===
- Route `DELETE /api/sessions/{sessionId}` → 204. Errors: 404 (not found or not yours), 409 (live).

---

## Open questions for the owner

1. **Where's the Delete button?** **Recommended:** on each row of the Sessions list *and* on the
   session page. Or only one of the two.
2. **Confirmation wording.** **Recommended:** title "Delete this session?", body "“{title}” and its
   documents, transcript and practice attempts will be permanently deleted. This can't be undone.",
   buttons "Cancel" and "Delete".
3. **Bulk delete** (select several, or "delete everything older than …")? **Recommended: not now.**
4. **Undo / trash bin?** **Recommended: no.** It's a permanent delete with a confirmation.
5. **Live sessions:** **Recommended:** Delete is disabled while live ("End the session before deleting
   it"). Or it ends the session and deletes it in one go.
6. **Dialog component:** **Recommended:** add shadcn/ui's `alert-dialog` (Radix, MIT, free) if the repo
   doesn't have a dialog yet. It's a new dependency, so it needs your OK.
7. **Issue:** "Sessions: delete a session and all its data" (`layer:backend`, `type:feature`,
   `priority:medium`, `module:sessions`, milestone v0.3 Supporting). OK?
