# Practice on cloud models — ATLAS + GOTCHA brief

Status: **awaiting the owner's approval**, issue #82.
Permanent rules: `GOTCHA-PROJECT.md` and `CLAUDE.md`.

## User story

As a user who picked a cloud model in Settings, I want mock interviews to use it too, so questions
and scores come from the model I chose, not only from the local one.

- GIVEN I chose a cloud model (Anthropic, OpenAI or Google) and saved its key, WHEN I start a mock
  interview and ask for a question, THEN the question and the score come from that model.
- GIVEN the key is missing or rejected, WHEN I ask for a question, THEN I see the provider's message
  (for example "Add your Anthropic API key in Settings.") and nothing falls back silently.
- GIVEN a cloud model, WHEN a question is written, THEN it gets the full 6,000-character document
  budget. The 2,000 limit stays for the local model only.
- GIVEN I left the model on Ollama (the default), WHEN I practise, THEN nothing changes and nothing
  leaves my machine.

---

## ATLAS

### [A] Architect
- **Purpose:** practice follows the chat model chosen in Settings, as live answers already do.
- **Constraints:** zero-cost by default: Ollama stays the default, cloud is opt-in BYOK. Keys stay in
  the OS keychain and reach the backend only through the existing per-session hand-off (#30), held in
  memory and cleared when the session ends. Clean Architecture: `PracticeService` talks to
  `ILlmProviderResolver` and `ISessionKeyStore` only.
- **Already in place:** starting a session already hands the chosen chat provider's key to the backend
  (`start-live.ts`), so the overlay needs no change. `AnswerGenerator` already picks provider, model
  and key this way. The budget rule `QuestionDocumentBudgetFor(provider)` is already in PR #79.
- **Privacy:** with a cloud model, practice sends that provider the selected document excerpts, the
  questions already asked, and the text of the spoken answer (never audio). That's what live answers
  already send, and it only happens once the user has chosen a cloud model.
- **Out of scope:** a separate model choice for practice, speech-to-text changes, new providers.

### [T] Trace
1. The user starts a mock interview → the app hands the chosen provider's key to the session (today).
2. **Next question** → `PracticeService.NextAsync` reads the owner's chat provider and model, the same
   way `AnswerGenerator` does.
3. The session's key for that provider goes into the `LlmRequest`; the provider streams the question.
4. **Done answering** → the score uses the same provider, model and key.
5. A missing or rejected key → the provider's message as the stream's `error` event (question) or the
   existing 503 with that message (score). The question stays answerable after a scoring failure, as today.

### [L] Link
| From | To | Method | Contract | Failure mode |
|---|---|---|---|---|
| PracticeService | IUserPreferencesRepository | owner's `LlmProvider` / `LlmModel` | unchanged | none set → configured default (Ollama) |
| PracticeService | ISessionKeyStore | `TryGet(session, provider)` | unchanged | no key → the provider's "Add your key" message |
| PracticeService | ILlmProviderResolver | `Resolve(provider)` | unchanged | unknown provider → as live answers today |
| UI | API | `practice/next`, `practice/finish` | unchanged | provider message shown inline, retry allowed |

### [A] Assemble
1. Tests first (Application unit tests; one API test with a fake cloud provider).
2. Move the provider/model/key choice out of `AnswerGenerator` into one shared helper, so live answers and
   practice can't disagree (question 3).
3. `PracticeService` uses it for both the question and the score; drop "Ollama only in this wave".
4. Checks → "What we adjusted" → PR.

### [S] Stress-test
- **Zero cost:** with no preference set, the provider is Ollama and the request carries no key (test).
- **Security:** the key never appears in logs, events, errors or the saved attempt (test on the saved
  attempt and the error text).
- **Failure:** a key added in Settings after the session started isn't in the session yet → "Add your
  key" until the session is restarted. Same as live answers today.

---

## GOTCHA

=== GOALS ===
- Practice uses the owner's chosen chat provider and model, with the session's key.
- No silent fallback; Ollama-default behaviour unchanged.
- Coverage of at least 75% per package; CI green.

=== ORCHESTRATION ===
1. Failing tests. 2. Shared provider choice. 3. PracticeService. 4. Checks → review → PR.

=== TOOLS ===
.NET 10, xunit v3, FluentAssertions 7.2.2. No new packages. No UI change.

=== CONTEXT ===
BYOK cloud providers from #30 (`CloudLlmProviderBase`, `InMemorySessionKeyStore`). Backend tests run in
CI only (the .NET SDK download is blocked in cloud sessions).

=== HEURISTICS ===
DO: reuse the live-answer choice of provider, model and key (yes/no). Keep Ollama the default (yes/no).
DON'T: fall back to another provider without telling the user (yes/no). Log or store the key (yes/no).

=== ARGS ===
- Question document budget: 2,000 for Ollama, 6,000 for cloud (`QuestionDocumentBudgetFor`, already in #79).

---

## Open questions for the owner

1. **Which model does practice use?** **Recommended:** the same chat model chosen in Settings for live
   answers. Or add a separate "Practice model" setting (more UI, more to keep in sync).
2. **Missing or rejected key:** **Recommended:** show the provider's message and stop, with no fallback
   to Ollama. Or quietly fall back to Ollama (but then the user can't tell which model scored them).
3. **Share the choice with live answers:** **Recommended:** move it into one helper used by both, so a
   fix to one applies to both. It touches `AnswerGenerator` too, but its behaviour stays the same.
4. **Show which model is in use in the overlay?** **Recommended: not now.** Live answers don't show it
   either; it could be a follow-up for both.
5. **Delivery:** **Recommended:** a separate PR after #79 is merged, on branch `issue/82-practice-cloud-models`,
   so #79 can be tested and released on its own.
