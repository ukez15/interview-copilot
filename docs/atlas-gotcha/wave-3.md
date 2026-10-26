# Wave 3 brief — #30, #26, #33, #16, #20, #24 (ATLAS + GOTCHA, compact)

Status: **approved by owner** (2026-10-16), including both proposed items. Same flow as waves 1–2 (`wave-1.md`, `wave-2.md`): Opus lands the
shared contracts on `main` (step 0), then 6 Sonnet agents work in parallel. Each agent gets one worktree
under `C:\Users\ukez1\Projects\ic-wt\<n>` and a branch `issue/<n>-<slug>`. Agents write tests first and
never commit, push or make design decisions. Opus then reviews every diff, runs all gates, commits,
pushes and opens one PR per issue. Once CI is green, Opus merges them in the order at the end of this brief.

**Not in this wave:**
- #36 (first beta) comes after this wave. It needs these features plus a fresh-user check on Windows and macOS.
- #15 (macOS capture) needs a Mac.

**Owner decisions (2026-10-16):**
- **BYOK keys** reach the backend **in memory, per live session**. They are never stored or logged, and
  are cleared when the session ends or the backend restarts.
- **Practice** scoring starts on a **"Done answering" click or after 4 s of silence**, whichever comes first.
- **Pairing** uses a **single-use 6-digit code, valid 5 minutes**. The extension then holds a scoped token
  that lasts until revoked from a "Paired browsers" list.
- **Auto-start on a meeting** creates a **new session copied from the most recent one**, titled
  `<App> meeting – <date time>`, and starts capture.

All briefs inherit `GOTCHA-PROJECT.md` and the repo gates:
- format; build with warnings as errors; tests; coverage ≥ 75% per package;
- eslint and typecheck; no `electron` import in `src/ui`;
- no secrets, audio, transcript text or keys in logs.
- Zero-cost rule: every paid provider stays **off by default**, and nothing leaves the machine unless the
  user picked a cloud provider and supplied a key.

Agent rules carried over from wave 2:
- Never run `dotnet format` in fix mode on code that doesn't compile.
- Run `dotnet test` in Release.
- Never write code through PowerShell strings.

## Step 0: shared contracts (Opus, on main, before agents start)

Schema changes all go into **one** migration here, so no two agents generate EF migrations in parallel.

- **Domain:**
  - `UserPreferences` gains `LlmProvider` (nullable, ≤ 50) and `SpeechToTextProvider` (nullable, ≤ 50).
    Null means the configured default.
  - New `PairedBrowser` entity:
    - `Id`, `UserId`, `Name` (≤ 100), `TokenHash`;
    - `CreatedAt`, `LastUsedAt?`, `RevokedAt?`;
    - a `Revoke(now)` method.
- **Persistence:** EF configs, a migration named `Wave3Schema`, and `IPairedBrowserRepository`
  (`Add`, `FindByTokenHashAsync`, `ListAsync(userId)`, `GetAsync(id)`) with its implementation.
- **Application contracts:**
  - `ISessionKeyStore` (singleton, in memory) with `Store(sessionId, provider, key)`,
    `TryGet(sessionId, provider)` and `Clear(sessionId)`, plus a basic `ConcurrentDictionary`
    implementation. `SessionService.EndAsync` calls `Clear`.
  - `PUT /api/sessions/{id}/keys/{provider}` takes the body `{key}`. It is allowed only for the owner and
    only while the session is Live, and returns 204.
  - `ProviderNames` gains `Anthropic = "anthropic"`, `OpenAi = "openai"` and `Google = "google"`.
  - `ILlmProviderResolver.Resolve(name)` returns the `ILlmProvider`. `AnswerGenerator` and
    `AutoAnswerService` use it with the account's `LlmProvider`, and for a cloud provider they take the
    key from `ISessionKeyStore`, with a per-request header key as the fallback for manual answers.
  - `ProviderNames.KeyedProviders` lists the providers that take a key (deepgram, anthropic, openai,
    google). `PUT …/keys/{provider}` refuses any other name, blank keys and keys over 512 characters.
  - `PreferencesDto` gains `LlmProvider`, `DefaultLlmProvider`, `SpeechToTextProvider` and
    `DefaultSpeechToTextProvider`.
  - New endpoints `PUT /api/preferences/llm {provider, model}` and
    `PUT /api/preferences/speech-to-text {provider}`. Both accept only providers that `ProviderCatalog`
    lists, so #30 and #20 enable their choices by adding to the catalog. The old `PUT /api/preferences {llmModel}` stays.
  - **Provider registration pattern:** each chat provider is a typed `HttpClient` of its own class
    (`services.AddHttpClient<OllamaLlmProvider>(…)`) and is exposed with
    `services.AddTransient<ILlmProvider>(sp => sp.GetRequiredService<OllamaLlmProvider>())`. Never
    `AddHttpClient<ILlmProvider, X>`, because every such client would share one named client and its settings.
  - Test doubles for the default LLM now report `Name => ProviderNames.Ollama`.
  - The authorization policy name `Policies.ExtensionScope` (Api/Infrastructure) is declared but not yet used.
- **UI:**
  - `Preferences` gains `llmProvider`, `defaultLlmProvider`, `speechToTextProvider` and
    `defaultSpeechToTextProvider`.
  - `ApiClient` gains `setLlmChoice(provider, model)`, `setSpeechToTextProvider(provider)` and
    `setSessionKey(sessionId, provider, key)`.
  - A new helper `features/sessions/start-live.ts` exports
    `startLiveSession(api, platform, sessionId)`. It calls `api.startSession` and then
    `platform.startCapture`, and `SessionPage` switches to it. #30 adds the key hand-off inside it, and
    #16 calls it.

---

## #30 Providers: BYOK adapters for Anthropic, OpenAI and Google

- **Architect.** Let the user pick a cloud model and stream answers from it with their own key. The key is
  never logged and never stored by the backend. Ollama stays the default.
  - Out of scope: tool use, images, cost tracking, and cloud models in practice mode (follow-up).
- **Trace.**
  1. `AnthropicLlmProvider` (Messages API, SSE), `OpenAiLlmProvider` (Chat Completions, SSE) and
     `GoogleLlmProvider` (Gemini `streamGenerateContent`, SSE) implement `ILlmProvider`. Each uses a
     typed `HttpClient` with the base URL from config.
  2. `GET /api/providers/{provider}/models`, with the key in the `X-Llm-Api-Key` header, calls the
     provider's list-models API and returns `{id, displayName}`. A wrong key returns 401 with an
     actionable message.
  3. `PUT /api/preferences` accepts `llmProvider` + `llmModel`. The catalog lists the three cloud
     providers as `RequiresApiKey: true`.
  4. `startLiveSession` reads the key with `platform.getApiKey(provider)` and, when there is one, sends it
     with `PUT /keys/{provider}` before capture starts.
  5. UI, under Settings → AI model:
     - a provider select;
     - a key field, with save and delete through the existing keychain bridge;
     - a "Load models" button that fills the model dropdown.
- **Proposed (needs your OK):**
  - The model lists come **from each provider's API**, not a hard-coded list, so they never go stale.
  - There is no default cloud model; you pick one.
- **Stress-test:**
  - a 401 or 429 from a provider becomes a readable error on the answer card;
  - a key that is missing mid-session gets the message "Add your key in Settings";
  - a test asserts the key never appears in logs, the DB or exception messages;
  - cancelling stops the HTTP stream.
- **Args:**
  - config `Llm:Anthropic|OpenAi|Google:BaseUrl`;
  - list-models timeout 10 s;
  - answer timeout 60 s, as for auto-answer.

## #26 Practice: the mock interviewer asks, listens, scores and gives feedback

- **Architect.** In a `MockInterview` session the app asks a question (generated from the documents), you
  answer aloud through the mic, and it scores you 1–10 with saved feedback. `PracticePromptBuilder` and
  `PracticeAttempt` already exist.
  - Out of scope: spoken (TTS) questions, follow-up questions that build on your answer, and practice
    analytics across sessions.
- **Trace.**
  1. `POST /api/sessions/{id}/practice/next` returns `{question}`, using the session's documents and the
     questions already asked.
  2. Candidate segments that arrive while a practice question is open are collected by a
     `PracticeAnswerCollector : ITranscriptSegmentListener`.
  3. `POST /api/sessions/{id}/practice/finish` scores the collected answer, or the UI sends it when it
     detects 4 s of silence (no new candidate segment). Scoring uses `ScoreAnswer` and `ParseFeedback`,
     with one retry if the format is wrong. The result is saved as a `PracticeAttempt` and returned.
  4. `GET /api/sessions/{id}/practice/attempts` lists the attempts.
  5. UI: a practice panel replaces `LiveAssistant` in mock sessions. It shows the "Next question" button,
     the question, your live transcript, a "Done answering" button and the score card with feedback.
     Attempts are listed below the panel and are included in the transcript PDF.
- **Stress-test:**
  - finishing with an empty answer returns a readable "No answer heard" error, and no attempt is saved;
  - an unparseable model reply twice in a row gives a readable error;
  - capture in mock mode records only the mic, so there are no interviewer frames.
- **Args:** 4 s silence; 1 format retry; Ollama only in this wave.

## #33 Overlay: pair the extension with the desktop app

- **Architect.** The extension gets a scoped token without your password.
  - Out of scope: pairing across machines, and a QR code.
- **Trace.**
  1. `POST /api/pairing/codes`, for the signed-in owner, returns a 6-digit code (cryptographically random)
     valid for 5 minutes. Codes live in memory only, and issuing a new code replaces the old one.
  2. `POST /api/pairing/claim {code, name}` is anonymous and rate-limited to 5 tries a minute per IP.
     It creates a `PairedBrowser`, returns a random 32-byte token once, and stores only its SHA-256 hash.
  3. A new `PairingToken` authentication scheme reads the `X-Pairing-Token` header (or the
     `pairing_token` query for the events WebSocket) and sets the `scope=extension` claim. The
     `ExtensionScope` policy allows only these: listing live sessions, `POST …/answers`, and the events
     WebSocket. Every other endpoint rejects pairing tokens.
  4. Desktop Settings gets a "Paired browsers" card with "Pair a browser" (shows the code and a
     countdown) and a list of paired browsers with Revoke buttons.
  5. Extension: the popup swaps username/password for a code field, stores the token in
     `chrome.storage.local`, and asks you to re-pair when it gets a 401.
- **Stress-test:**
  - an expired or reused code returns 400;
  - brute force is limited by the rate limit plus the 5-minute window;
  - a revoked token gets 401 on its next call;
  - a pairing token on `/api/documents` gets 403.
- **Args:** code TTL 5 min; rate limit 5/min/IP; token 32 bytes, base64url.

## #16 Audio: start a session from the meeting-detected prompt

- **Architect.** When a meeting is detected and you click **Start**, you pick an existing session or create
  one, and capture begins. Nothing starts without your click unless auto-start is on.
  - Out of scope: detecting browser-based meetings (Meet in a tab), and telling which meeting is which.
- **Trace.**
  1. `main.ts` already prompts. On Start, or on auto-start, it sends a new IPC event `meeting:detected`
     `{app, autoStart}` to the renderer.
  2. The preload exposes `onMeetingDetected(cb)` on the bridge, and the web platform makes it a no-op.
  3. The UI routes to `/start-from-meeting`. It lists Draft and Live sessions with a "Use this" button,
     plus a "New session" form, and choosing one calls `startLiveSession`.
  4. With auto-start on, the UI skips the picker and calls `POST /api/sessions` with
     `copyFromSessionId` set to the most recent session. That copies the mode, length, framework, pinned
     type, prompt and documents. The title is `<App> meeting – <date time>` (local time,
     `dd MMM HH:mm`), and `startLiveSession` follows.
  5. With no previous session, auto-start falls back to the picker.
- **Stress-test:**
  - a second detection while a session is Live is ignored;
  - clicking "Not now" leaves nothing started;
  - copying a session with 10 documents copies all 10.
- **Args:** reuse `MEETING_POLL_MS`; the title format is as above.

## #20 Transcription: Deepgram streaming behind the existing flag

- **Architect.** When Deepgram is enabled in config **and** chosen in Settings **and** the session has a
  key, audio streams to Deepgram over a WebSocket and its final results become transcript segments.
  It stays off by default.
  - Out of scope: diarization (the speaker byte already tells who is speaking), and other cloud STT providers.
- **Trace.**
  1. A `DeepgramStreamingSink : IAudioFrameSink` keeps one Deepgram socket per session and speaker
     (`wss://…/v1/listen?encoding=linear16&sample_rate=16000&channels=1&model=…&interim_results=false`).
  2. Final transcripts are saved and published as `TranscriptSegment`s through the same path the
     Whisper worker uses (listeners included, so auto-answer works).
  3. `OnStreamEndedAsync` sends `CloseStream` and disposes the socket.
  4. Sink routing: when the session's chosen STT provider is Deepgram, Whisper's `TranscriptionSink`
     skips that session.
  5. Settings: a speech-to-text provider select that only shows Deepgram when config enables it, using
     the existing keychain bridge for the key.
- **Stress-test:**
  - no key → one readable `error` event, and Whisper is not silently used instead;
  - a socket drop → reconnect once, then an `error` event;
  - a test asserts the key never appears in logs.
- **Args:** `KeepAlive` every 8 s; reconnect once after 1 s.

## #24 Copilot: latency benchmark against NFR-05

- **Architect.** A repeatable benchmark that asks 20 questions through the real HTTP API and records P50
  and P95 time-to-first-token in `docs/benchmarks/latency.md`.
  - Out of scope: STT latency and CI gating, since CI runners aren't the reference hardware.
- **Trace.**
  1. A console project `src/backend/tools/InterviewCopilot.Benchmark` is added to the solution and
     excluded from coverage.
  2. It reads `questions.json`, which holds 20 questions across the 8 question types.
  3. It registers or signs in a throwaway account, creates a session, and asks each question over SSE.
  4. It times each question until the first `delta` and writes a markdown table with hardware info (CPU,
     RAM, OS, model).
  5. The agent builds and unit-tests the timing and statistics logic. **You run it** on the reference
     machine with Ollama installed, and Opus commits the results.
- **Proposed (needs your OK):** the project name and location above.
- **Args:** 20 questions; one warm-up question that isn't counted; the default model `llama3.2:3b`.

---

## Expected overlaps (Opus resolves them when merging)

- `SettingsPage.tsx` (#30, #33, #20): each issue adds its own card, with a one-line change in the page.
- `ProviderEndpoints` / `ProviderCatalog` (#30, #20).
- `Program.cs` (#33 auth scheme and rate limiter, #30 HTTP clients, #20 sink registration).
- `SessionEndpoints` / `SessionService` (#16 `copyFromSessionId`, #26 practice routes in their own file).
- Desktop `main.ts` / `preload.ts` / `ipc-channels.ts` (#16 only).
- `start-live.ts` (#30 adds the key hand-off; #16 only calls it).

**Merge order:** #30 → #20 → #26 → #33 → #16 → #24, merging main into each branch as needed.

---

## What we adjusted (review, 2026-10-26)

All six issues merged: #30 (PR #50), #20 (#53), #26 (#54), #33 (#55), #16 (#56), #24 (#57). #24 stays open
until the owner runs the benchmark on the reference machine and commits `docs/benchmarks/latency.md`.

- **Goals / Args.** As briefed, with these agent choices kept:
  - #33: a pairing token on a non-extension endpoint gets 401, not 403, because the default policy is
    bearer-only, so the token simply doesn't authenticate.
  - #20: one Deepgram reconnect per stream; a 403 is treated like a 401. The old batch Deepgram
    provider was removed, so Whisper is always the batch provider.
  - #16: copies also carry the AutoAnswer flag; failed auto-starts fall back to the picker.
- **Context.** `ISessionKeyStore.Set` became `Store`, because of the CA1716 keyword rule. The UI's
  `ApiClient` no longer treats a 401 that carries a problem `detail` (a rejected cloud key) as an
  expired sign-in.
- **Orchestration.** Agents were cut off once by a usage limit and resumed from their saved state. All
  merges needed main merged in first, and the conflicts were all keep-both.
- **Heuristics / Tools.**
  - Windows Smart App Control escalated from blocking single builds to blocking the QuestPDF native
    DLL and the test executables. The owner turned it off.
  - Three flaky tests were fixed on main: the 1,000-entry transcript render, the Deepgram socket
    handshake race, and the auto-answer cancellation race.
  - Run vitest coverage as `pnpm exec vitest run --coverage`. `pnpm test -- --coverage` passes the
    flag as a file filter.
- **Open for the owner.** Review the UI wording the agents chose (settings cards, practice panel,
  pairing popup) and whether the OpenAI model list should be filtered to chat models.
