# Wave 1 brief — #18, #23, #12, #10, #28 (ATLAS + GOTCHA, compact)

Status: **approved by owner** (2026-10-16). Flow: Opus writes the shared contracts on `main` (step 0) →
5 Sonnet agents build in parallel, each in its own worktree and branch `issue/<n>-<slug>`, tests first,
no commits/pushes/design decisions → Opus reviews each diff, runs all gates, commits, pushes and opens one
PR per issue (owner approved push + PR for this wave; merging stays with the owner).

Every brief inherits `GOTCHA-PROJECT.md` (Context + Heuristics) and the repo's quality gates:
`dotnet format` clean, warnings-as-errors build, all tests green, coverage ≥ 75% per package, eslint +
typecheck clean, no `electron` import in `src/ui`, no secrets/audio/transcript text in logs.

## Step 0 — shared contracts (Opus, on main, before agents start)

- `Application/Events/SessionEvents.cs`: `SessionEvent(long Id, string Type, object Data)`, constants
  `transcript.segment`, `question.detected`, `answer.delta`, `answer.done`, `error`, and
  `ISessionEventPublisher { void Publish(Guid sessionId, string type, object data); }` with a no-op
  default registration so the app runs before #23 lands.
- `src/ui/src/lib/api.ts`: `updateSessionSettings(id, input)` and `getTranscript(id)` + `Transcript` types.

---

## #18 Transcription: audio WebSocket + voice-activity buffering

- **Architect.** Turn incoming frames (already received by #14's `/ws/sessions/{id}/audio`) into
  persisted, published transcript segments. Out of scope: Deepgram streaming (#20), model download UI
  (#19), question detection (#22).
- **Trace.** `IAudioFrameSink` (`TranscriptionSink`) → per-session, per-speaker voice-activity buffer →
  on a pause ≥ 600 ms (or 15 s of continuous speech) → `ISpeechToTextProvider.TranscribeAsync` →
  `TranscriptSegment` saved → `Publish(sessionId, "transcript.segment", {id, speaker, startMs, endMs, text})`.
- **Link.** Sink must never block the WebSocket receive loop: enqueue frames into a bounded per-session
  `Channel`; a background worker (hosted service) drains it, using `IServiceScopeFactory` for scoped
  repositories. STT failure → `Publish(..., "error", {message})`, keep consuming.
- **Assemble.** VAD/buffer logic as a pure class first (tests), then sink + worker, then DI.
- **Stress-test.** 10 min of silence emits nothing; overlapping speakers give independent segments
  ordered by `startMs`; buffer bounded; empty STT result emits nothing.
- **Args.** Pause 600 ms; max segment 15,000 ms; min speech 300 ms; silence threshold RMS −40 dBFS;
  timestamps = frames received per speaker × 100 ms; queue 200 frames per session (drop oldest + log
  count only). Config section `Transcription:Vad` with these defaults.

## #23 Copilot: events WebSocket for overlay and extension

- **Architect.** Fan session events out to every connected client. Out of scope: replay/resume from a
  last event id (follow-up), extension consumption (#33), UI consumption (#22).
- **Trace.** `ISessionEventPublisher` implemented by an in-memory `SessionEventHub` (singleton) →
  per-subscriber bounded channel → `/ws/sessions/{id}/events` sends JSON text frames
  `{"id":n,"type":"answer.delta","data":{...}}`. `AnswerGenerator` publishes `answer.delta` per token
  and `answer.done` (with `version`, `questionType`) — in addition to the existing SSE response.
- **Link.** Same auth as the audio socket (`access_token` query, owner check, 404/409 rules: any status
  allowed except 404 for non-owners). Slow client → drop oldest events for that client only.
- **Assemble.** Hub (pure, tests) → endpoint → AnswerGenerator publishing → integration test with two
  WebSocket clients.
- **Stress-test.** Two clients both receive every `answer.delta` in order; a disconnected client is
  removed; one slow client doesn't delay the other.
- **Args.** Per-subscriber buffer 500 events; event ids increase per session from 1; text frames only.

## #12 Documents: extract text from PDF and DOCX

- **Architect.** Owner chose **PdfPig 0.1.16** (Apache-2.0) for PDF and **DocumentFormat.OpenXml 3.5.1**
  (MIT) for DOCX. Out of scope: OCR of scanned PDFs (clear error instead), .doc, images.
- **Trace.** Upload → `IDocumentTextExtractor` chosen by extension → text → existing 10-doc rule.
- **Link.** Encrypted/corrupt file → `DomainException` "Couldn't read '<name>'"; PDF with no text layer →
  "No readable text… (scanned PDFs aren't supported yet)".
- **Assemble.** Extractors + tests using small generated fixtures (create the fixture files in tests,
  don't commit binaries > 50 KB), DI, UI `accept` + help text.
- **Args.** Extensions `.pdf`, `.docx`; existing 20 MB limit; page text joined with blank lines.

## #10 Sessions: edit settings of an existing session from the UI

- **Architect.** Edit length, framework, pinned type and session prompt on the session page; backend
  `PUT /api/sessions/{id}/settings` already exists. Editable while **Draft or Live** (takes effect on the
  next answer), read-only when Ended.
- **Assemble.** Reuse `SessionForm` in an "edit" mode (no title/mode fields), save button, success /
  error states, tests.

## #28 Transcripts: history view of transcript + answers

- **Architect.** New route `/sessions/:sessionId/transcript` showing transcript entries (speaker,
  timestamp) and suggested answers in time order, plus the existing PDF export; linked from the
  session page and from history entries of ended sessions. Uses `GET /api/sessions/{id}/transcript`.
- **Stress-test.** Empty transcript state; 1,000 entries render without freezing (simple list, no
  virtualisation needed yet).

---

## Expected overlaps (Opus resolves when merging)
`Application/DependencyInjection.cs` (#18, #23), `SessionPage.tsx` (#10, #28). Each PR is rebased on
the latest `main` before it's opened.
