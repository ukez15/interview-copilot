# Wave 2 brief — #22, #43, #19, #31, #8, #35 (ATLAS + GOTCHA, compact)

Status: **approved by owner** (2026-10-16), including click-to-download for #19. Same flow as wave 1 (`wave-1.md`): Opus lands the shared
contracts on `main` (step 0) → 6 Sonnet agents in parallel, one worktree + branch `issue/<n>-<slug>`
each, tests first, no commits/pushes/design decisions → Opus reviews every diff, runs all gates,
commits, pushes, opens one PR per issue, then merges in a safe order once CI is green.

Owner decisions (2026-10-16): Ollama model choice stored **on the backend per account**; auto-answer
**on by default, pausable**; sign-in persists with the **refresh token in the OS keychain**; #43
(transcription flush) added to this wave.

All briefs inherit `GOTCHA-PROJECT.md` and the repo gates (format, warnings-as-errors build, tests,
coverage ≥ 75% per package, eslint + typecheck, no `electron` import in `src/ui`, no secrets, audio or
transcript text in logs).

## Step 0 — shared contracts (Opus, on main, before agents start)

Schema changes go in **one** migration here, so no two agents generate EF migrations in parallel.

- **Domain:** `Session.AutoAnswer` (bool, default `true`) + `SetAutoAnswer(bool)`; new
  `UserPreferences` entity (`UserId` key, `LlmModel` nullable string ≤ 200 chars, `UpdatedAt`).
- **Persistence:** EF configs, migration `Wave2Schema`, `IUserPreferencesRepository`
  (`GetAsync(userId)`, `Upsert`) + implementation.
- **Application contracts:**
  - `ITranscriptSegmentListener { ValueTask OnSegmentAsync(TranscriptSegment segment, CancellationToken ct); }`,
    called by `TranscriptionWorker` after a segment is saved and published (no listeners = no-op).
  - `IAudioFrameSink.OnStreamEndedAsync(Guid sessionId, CancellationToken ct)` as a default no-op
    interface member.
  - `SessionEventTypes.AutoAnswerState = "autoanswer.state"`.
- **UI types:** `Session.autoAnswer`.

---

## #22 Copilot: auto-answer detected interviewer questions

- **Architect.** When the interviewer asks a question in a live session with `AutoAnswer = true`, stream
  an answer to every client without any typing. Out of scope: merging a question split across several
  segments (follow-up), cloud models (the BYOK key reaches the backend in #30; auto-answer uses the
  account's Ollama model until then).
- **Trace.**
  1. `AutoAnswerService : ITranscriptSegmentListener` receives an **Interviewer** segment.
  2. If `QuestionDetector.IsQuestion(text)` and the session is Live with AutoAnswer on, publish
     `question.detected` `{segmentId, text}`.
  3. Run `AnswerGenerator` (it already publishes `answer.delta` / `answer.done`).
  4. A newer question cancels the in-flight answer for that session.
  5. `PUT /api/sessions/{id}/auto-answer {enabled}` toggles it and publishes `autoanswer.state {enabled}`.
- **UI.**
  - New `useSessionEvents(sessionId)` hook: a WebSocket to `/ws/sessions/{id}/events?access_token=…`
    using `platform.getAuthToken()`, reconnecting with backoff (0.5 s, 1 s, 2 s, then 4 s).
  - `LiveAssistant` (session page + overlay) shows the detected question and the streaming
    auto-answer through the existing `AnswerCard`. Regenerate/Shorter/Longer keep working (they use the
    existing manual endpoint).
  - A Pause / Resume auto-answer button.
- **Stress-test.**
  - Two questions 1 s apart: the first answer stops, the second streams.
  - The candidate's own speech never triggers an answer.
  - Paused: questions are still detected, but no answer runs.
  - Events-socket drop: reconnect.
- **Args.** Backend uses `CancellationTokenSource` per session; answer timeout 60 s; reconnect backoff as above.

## #43 Transcription: flush and clean up when a session's audio ends

- **Architect.** When the audio socket closes, transcribe the speech still buffered and release the
  session's pipeline. Out of scope: idle timeouts.
- **Trace.**
  1. `AudioEndpoints` calls `IAudioFrameSink.OnStreamEndedAsync` for every sink when the socket
     closes, for any reason.
  2. `TranscriptionSink` completes that session's queue.
  3. The worker drains it, calls a new `VoiceActivityBuffer.Flush()` per speaker (respecting the 300 ms
     minimum), transcribes, saves and publishes, then removes the pipeline.
  4. A new stream for the same session creates a fresh pipeline.
- **Stress-test.**
  - End mid-sentence → one final segment.
  - End during silence → nothing.
  - Reconnect after an end → works.
  - 100 sessions started and ended → no pipelines left.

## #19 Transcription: Whisper model download with progress

- **Architect.** Show whether the speech model is present and let the user download it with visible
  progress, instead of a silent ~466 MB download on first use. Out of scope: choosing other model
  sizes (follow-up), resuming partial downloads.
- **Trace.**
  1. `WhisperModelManager` (Infrastructure, singleton) has states Missing / Downloading(bytes, total) /
     Ready / Failed(message). It owns the download, and `WhisperSpeechToTextProvider` uses it.
  2. `GET /api/providers/whisper/model` returns the state; `POST /api/providers/whisper/model/download`
     starts it.
  3. UI: a "Speech model" card in Settings plus a banner on the sessions page when the model is
     missing, both with a progress bar polled every 1 s, Download / Retry buttons and the size.
- **Deviation from the issue text (needs your OK):** the issue says progress shows when the app opens.
  This brief shows the banner on open but **starts the download only on a click**, so ~466 MB is never
  fetched without consent.
- **Args.** Download to `<models>/<name>.download`, then rename; poll 1 s.

## #31 Providers: choose any pulled Ollama model in Settings

- **Architect.** List the models installed in Ollama and let the user pick one; it is saved per account
  and used for manual and automatic answers. Out of scope: pulling models from the UI.
- **Trace.**
  1. `GET /api/providers/ollama/models` proxies Ollama `GET /api/tags` and returns names, sizes and
     modified dates. If Ollama is unreachable, it returns 503 with an actionable message.
  2. `GET` / `PUT /api/preferences` `{llmModel}` uses `IUserPreferencesRepository`.
  3. `AnswerGenerator` uses the account's `LlmModel` when set, otherwise `Llm:Model` from config.
  4. UI: a model dropdown in Settings → AI model.
- **Args.** Ollama base URL from `Llm:Ollama:BaseUrl`; HTTP timeout 5 s for the list call.

## #8 Auth: persist sign-in across restarts

- **Architect.** Stay signed in across restarts without storing the password. Out of scope: multiple
  accounts on one machine.
- **Trace.**
  1. `POST /api/auth/refresh {refreshToken}` returns new tokens (ASP.NET bearer refresh-token
     validation), or 401 if it has expired.
  2. The desktop main process stores the refresh token encrypted with `safeStorage` (like `KeyStore`),
     never in plain text. On startup it refreshes; on success the UI starts signed in.
  3. The UI refreshes once on a 401 mid-session, then falls back to the sign-in page.
  4. A new **Sign out** action (Settings) clears both tokens.
- **Bridge change.** `setAuthToken(token)` becomes `setAuthTokens({ accessToken, refreshToken } | null)`,
  plus `refreshAuth()`. The web platform keeps tokens in memory only.
- **Args.** Default ASP.NET token lifetimes (1 h access, 14 days refresh); stored file `auth.json` in userData.

## #35 Settings: show where local data lives

- **Architect.** A Privacy card in Settings shows the data folder path, explains what's in it and how to
  delete it, and has an "Open folder" button (desktop only).
- **Trace.**
  1. `GET /api/storage` returns `{dataDirectory}` (the backend's resolved folder).
  2. Bridge `openDataFolder()`: the main process asks the backend for the path itself (it never takes a
     path from the renderer) and calls `shell.openPath`.
  3. The web platform shows the path only.

---

## Expected overlaps (Opus resolves when merging)
- `SettingsPage.tsx` (#19, #31, #8, #35): each adds its own card component, with a one-line change in
  the page.
- Desktop `main.ts` / `preload.ts` / `ipc-channels.ts` (#8, #35).
- `TranscriptionWorker` (#43 changes its internals; step 0 adds the listener call).
- `ProviderEndpoints` (#19, #31).

**Merge order:** #43 → #22 → #31 → #19 → #8 → #35, rebasing as needed.
