# Practice mode latency and stale speech — ATLAS + GOTCHA brief

Status: **approved by the owner with the recommendations (2026-10-26)**, issue #78, PR #79.
Permanent rules: `GOTCHA-PROJECT.md` and `CLAUDE.md`. Builds on `practice-overlay.md` (#74).

## What the owner saw (2026-10-26, test installer 76a1211)

1. "Next question" takes too long.
2. Words said ~30 s earlier, before the question had appeared, showed up as the answer.

## Why

- Mic audio is transcribed all the time, including while the model writes the next question or scores
  an answer. Whisper (`ggml-small.en`) and Ollama share the CPU, so each slows the other, and the
  transcription backlog grows. The live-answer benchmark (#24) gave 0.9 s P95 to first token on the
  same machine with the same 6,000-character document context. That points to CPU contention, not
  prompt size.
- `PracticeAnswerCollector` and `PracticePanel` accept every Candidate segment that *arrives* after
  the question is asked. A segment that was *spoken* before the question but transcribed late counts as
  the answer. Segments already carry `startMs`, their position in the audio stream (frames × 100 ms),
  so "spoken before the question" can be told apart from "transcribed late".
- `PracticeService.NextAsync` waits for the whole reply before showing anything, although
  `OllamaLlmProvider` already streams.

## User story

As a candidate in a mock interview, I want the next question to appear quickly and only what I say
after it to count as my answer, so that practice feels like a real interview.

- GIVEN a question is being generated or an answer scored, WHEN I talk, THEN none of it is
  transcribed or counted.
- GIVEN the question is shown, WHEN I answer, THEN only speech that started after the question
  appeared is shown and scored, however late transcription delivers it.
- GIVEN I click Next question, WHEN the model starts writing, THEN I see the question appear word by
  word (question 2).

---

## ATLAS

### [A] Architect
- **Purpose:** stop stale speech counting as the answer, and cut question latency by not running
  Whisper and Ollama at the same time in mock interviews.
- **Constraints:** zero-cost and local only. Live-interview and Sales/general sessions must not change:
  they keep transcribing all the time. Clean Architecture: the gate is an Application interface. The
  Deepgram path (off by default) honours the same rule.
- **Out of scope:** Whisper model choice (Settings already has the download; a smaller model is a
  separate decision), GPU support, document budget changes for scoring and live answers, text-to-speech, and pre-generating the next
  question (it would compete with Whisper while the user answers).

### [T] Trace
1. The user clicks **Next question** → `POST practice/next`.
2. `PracticeService.NextAsync` builds the prompt and streams Ollama's reply (question 2).
3. When the question is complete, `PracticeAnswerCollector.Open(sessionId, question, fromMs)` records
   `fromMs`, the session's current mic-stream position from the audio sink. **The gate opens.**
4. Mic frames now reach the speech buffer and Whisper. Before step 3 they were dropped on arrival.
5. Each Candidate segment with `startMs >= fromMs` is added to the answer and shown. Earlier ones are
   ignored by both the collector and the panel; the API returns `answerFromMs` with the question.
6. **Done answering** or 4 s of silence → `Take` → **the gate closes** → scoring runs with the CPU free.
7. A scoring failure → `Restore` re-opens the gate with the same `fromMs`, so the user can carry on.

### [L] Link
| From | To | Method | Contract | Failure mode |
|---|---|---|---|---|
| TranscriptionSink / DeepgramStreamingSink | New `IAudioCaptureGate` (Application) | in-process | `bool Accepts(sessionId, speaker)` | Sessions that aren't mock interviews: always true |
| PracticeAnswerCollector | IAudioCaptureGate | implements it | open question ⇒ accept Candidate | No open question ⇒ drop |
| PracticeAnswerCollector | Audio sink | in-process | `long PositionMs(sessionId, speaker)` | No stream yet ⇒ 0 |
| `POST practice/next` | UI | SSE, as approved in question 2: `delta` events, then `done` carrying `{question, answerFromMs}`, or `error` | adds `answerFromMs` | Checks before the stream (404/400/409) as today; a model failure mid-stream is an `error` event, not a 503 |
| PracticePanel | Events WS | existing | filters `startMs < answerFromMs` | — |

### [A] Assemble
1. Tests first: the gate drops mock-interview Candidate frames with no question open and passes them
   with one open; the collector ignores segments with `startMs < fromMs`; other modes are unaffected;
   the panel ignores early segments; `Restore` keeps `fromMs`.
2. Application: `IAudioCaptureGate`, plus collector changes (`fromMs`, gate state).
3. Audio sinks: check the gate per frame; expose the mic-stream position.
4. API: `answerFromMs` on the question response (plus streaming if question 2 says yes).
5. UI: the panel filters by `answerFromMs` (plus word-by-word question if question 2 says yes).
6. `dotnet test` (cloud CI, since the .NET SDK can't download here) and the UI checks, then
   "What we adjusted", a PR, and a fresh test installer.

### [S] Stress-test
- **Load:** while a question is generated or scored, Whisper work for the session drops to zero.
- **Data integrity:** speech spanning the moment the question appears starts before `fromMs`, so it's
  dropped. The cost is at most the first ~0.6 s pause-segment.
- **Failure:** scoring fails → `Restore` → the gate re-opens and answering continues.
- **Regression:** live interviews and auto-answer are unchanged; that's covered by tests.

---

## GOTCHA

=== GOALS ===
- In mock interviews, speech that started before the question appeared is never shown or scored.
- No mic transcription runs in a mock interview while no question is open.
- Other session modes transcribe exactly as today.
- Coverage of at least 75% per package; CI green.

=== ORCHESTRATION ===
1. Failing tests (Application, Api, UI). 2. Gate interface plus collector. 3. Sinks: gate and position.
4. API field (and streaming if approved). 5. UI filter (and streaming if approved).
6. Checks → review → PR → installer.

=== TOOLS ===
.NET 10, xunit v3, FluentAssertions 7.2.2, React 19.3.1, Vitest 5.0.5. No new packages.

=== CONTEXT ===
Clean Architecture: the gate interface lives in Application; the sinks depend on it. Ollama and
Whisper are both local and CPU-bound on the owner's laptop. Cloud sessions can't run .NET locally
(`dot.net` is blocked), so backend tests run in CI.

=== HEURISTICS ===
DO: drop frames at the sink, before buffering (yes/no). Keep live-interview behaviour byte-for-byte
unchanged (yes/no). Use stream position (`startMs`), not wall-clock time (yes/no).
DON'T: transcribe and then discard (it wastes the CPU we're trying to free) (yes/no). Change VAD
thresholds or the Whisper model (yes/no).

=== ARGS ===
- Frame length 100 ms (existing `VadOptions.FrameMs`); silence auto-finish 4,000 ms (existing).
- New response field `answerFromMs` (long, ms of mic stream) — name to confirm (question 3).

---

## Open questions for the owner

1. **Gate the mic between questions:** in mock interviews, only transcribe while a question is open.
   **Recommended: yes.** It's the fix for both problems.
2. **Show the question word by word as it's written?** It feels faster, but it changes
   `POST practice/next` to stream like live answers do. **Recommended: yes**, in the same PR. Or leave
   it out and see whether the gate alone is fast enough.
3. **Name of the new field:** `answerFromMs`. **Recommended: OK.**
4. **"Done answering" right after you stop talking:** the last few words may still be in Whisper's
   queue. Today they're lost; with the gate they'd be dropped too. Should the app wait for them, up to
   a few seconds, before scoring? **Recommended: yes, up to 5 s,** as part of this work.
5. **To size the problem,** about how many seconds did "Next question" take, and were you talking
   while it was working?

## Added after approval (2026-10-27)

6. **Document budget for question prompts.** The owner's Ollama stats (prompt read at about 41 tokens/s,
   6 s cold load) showed that the prompt's up to 6,000 document characters (~1,500 tokens, ~37 s to read)
   were the biggest cost behind the 90 s question. **Owner's decision: 2,000 characters for question
   prompts only** (`PracticePromptBuilder.QuestionDocumentBudget`). Scoring and the live assistant keep
   6,000.
7. **Local models only (owner, 2026-10-27).** The 2,000 limit applies only when the question runs on the
   local model (Ollama). A cloud model gets the full 6,000 (`QuestionDocumentBudgetFor`). Practice runs on
   Ollama only in this wave, so the cloud rule matters once practice gains cloud models.

---

## What we adjusted

- **Step 1's backend code came before its tests** (the mic gate and stream clock). That broke the tests-first
  rule. The tests followed in the same PR, and three CI rounds caught what they found: a redundant `!`
  (S8969), an ambiguous test double (CS0104), and the clock not restarting when a stream reconnects.
- **The stream clock restarts when a stream ends,** so a reconnected stream's times start at 0, as its
  segment times always have. A reconnect while a question is open can therefore drop the rest of that
  answer; you'd click Next question again. Not worth more code for a rare case.
- **Deepgram sessions aren't gated** and skip the 5 s wait: they use no local CPU, and gating would put
  Deepgram's own clock out of step.
- **Skipping a question closes it at once,** so the mic rests while the next one is written. That wasn't in
  the brief; it closes a gap the gate left.
- **The question stream's last event carries the result:** `done` holds `{question, answerFromMs}` (live
  answers send an empty `done`). Live answers and practice share one relay, `LlmEventStream`, so a model
  failure is an `error` event in both. A model failure on Next question is no longer a 503.
- **The 2,000 budget (question 6) and "local models only" (question 7)** were added after approval, from the
  owner's Ollama stats.
- **Not verifiable in the cloud session:** the speed-up and the 5 s wait need the owner's check on Windows
  with Ollama and the mic. The backend tests ran in CI only.

---

## Changes after the owner's second test (2026-10-27, test installer e67b4bf)

Status: **awaiting the owner's OK.** No code until then.

What the owner saw: questions now come quickly, word by word. But scoring started while they were still
answering (once after about 12 words). "(mumbles)" appeared as their answer, and words they'd said
(such as "microservices") only showed up after scoring.

Causes:
1. **Auto-finish counts gaps in text, not silence.** The overlay scores once 4 s pass without a new
   Candidate segment. A segment only exists after a 0.6 s pause (or 15 s of non-stop talking), and Whisper
   then needs a few seconds on the CPU, so a 4 s gap between segments is normal mid-answer.
2. **The overlay stops listening once scoring starts,** but the backend's 5 s last-words wait still adds
   segments. They're scored without ever being shown.
3. **Whisper labels noise** as "(mumbles)", "[BLANK_AUDIO]", "(music)" and so on, and nothing filters them.
4. **The mic is muted while the question is written** (by design), but the overlay doesn't say so.

Proposed changes:
- **A. Silence means no voice.** The backend already knows from the voice detector when the candidate last
  spoke. Once a question is open, if 6 s pass with no voice *and* everything said has been transcribed, it
  sends one new session event (`practice.silence`), and the overlay scores. The 4 s text timer goes.
  "Done answering" still works at any time, with its 5 s last-words wait.
- **B. Show every word that's scored.** The overlay keeps adding live lines while scoring, then shows the
  exact scored text, as today.
- **C. Drop Whisper's noise labels.** A segment whose whole text is a bracketed label, like "(mumbles)" or
  "[BLANK_AUDIO]", is discarded before it's saved or shown, in every mode.
- **D. Say when the mic listens.** While the question is being written, the overlay shows "The mic
  listens once the question is complete."

Out of scope: the speech model's accuracy (model size, mic level). That's a separate issue if needed.
