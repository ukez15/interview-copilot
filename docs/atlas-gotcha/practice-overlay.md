# Practice mode in the overlay — ATLAS + GOTCHA brief

Status: **draft, waiting for the owner's OK.** No code until then.
Permanent rules: `GOTCHA-PROJECT.md` and `CLAUDE.md`. This brief only adds what's specific to this change.

## User story

As a candidate practising for an interview, I want the mock interview to run in the transparent
always-on-top overlay, the same window the live copilot uses, so that I can practise the same way
I'll use the app in a real call. I also want to see straight away whether the app can hear me.

- GIVEN a Mock interview session, WHEN I click Start session, THEN the overlay opens with the practice
  panel (Next question, Done answering, my live answer, the score) and not the answer assistant.
- GIVEN the overlay is practising, WHEN I speak, THEN I can see the mic is live and my words appear
  under "Your answer".
- GIVEN the mic isn't working, WHEN I'm in the overlay, THEN it tells me why (mic missing or blocked)
  instead of showing "Listening…" forever.
- Live interview and Sales/general sessions behave exactly as today.

---

## ATLAS

### [A] Architect

- **Purpose:** run practice inside the existing overlay window (FR-11 + FR-17), and make "it can't
  hear me" diagnosable.
- **Users:** the candidate, on Windows today. macOS is mic-only too (#15), so the same UI applies.
- **Constraints:** reuse the overlay window as it is: transparent, frameless, always on top, 440×360,
  resizable, opacity from settings. `src/ui` must not import Electron; go through `src/ui/src/platform/`.
  Coverage of at least 75% per package. Zero-cost: no new services.
- **Tech decisions (already made):** React + Tailwind + shadcn/ui in `src/ui`, the existing
  `PracticePanel`, the `/overlay/:sessionId` route, `platform.showOverlay` / `getCaptureStatus`, and
  the session events WebSocket.
- **Out of scope:**
  - A new window, or any change to the overlay's size, position, frame or opacity behaviour.
  - Changes to scoring, the prompts or the backend practice API.
  - Spoken questions (text-to-speech) and mic-level metering in dB (see question 3).
  - Changing the speech-detection threshold. Only if the owner's test shows that's the cause, and
    then as its own issue.
  - The browser extension overlay (Mode B). Practice stays desktop-only.

### [T] Trace

1. The user opens a Mock interview session and clicks **Start session**.
2. The UI calls `startLiveSession`. The backend marks the session Live and the desktop starts
   **mic-only** capture (already the case for mock interviews).
3. The UI calls `platform.showOverlay(sessionId)`, and the overlay loads `/overlay/:sessionId`.
4. `OverlayPage` reads the session (it already does, for `autoAnswer`). **New:** if
   `mode === 'MockInterview'` it renders the compact practice panel; otherwise `LiveAssistant` as now.
5. The practice panel polls `platform.getCaptureStatus()` every second and shows the mic state. On
   `mic: false` or `state: 'error'` it shows the capture message.
6. **Next question** → `POST /practice/next` → the question is shown → "Listening…".
7. Mic audio → backend speech detection → Whisper → `transcript.segment` (speaker Candidate) on the
   events WebSocket → shown under "Your answer".
8. **Done answering**, or 4 s of silence → `POST /practice/finish` → score and feedback shown.
9. **End session** (main window) → capture stops and the overlay hides, as today.

Failure points: step 3 (overlay doesn't open), step 5 (no mic), step 7 (speech too quiet, or Whisper
returns nothing), step 8 (scoring fails; the backend keeps the answer so the user can retry, as today).

### [L] Link

| From | To | Method | Contract | Failure mode |
|---|---|---|---|---|
| OverlayPage | Backend | `GET /api/sessions/{id}` (existing) | `Session.mode`, `autoAnswer` | Falls back to `LiveAssistant`, as today |
| Practice panel | Backend | `POST practice/next`, `practice/finish`, `GET practice/attempts` (existing) | unchanged | Inline error, retry allowed |
| Practice panel | Events WS | `useSessionEvents` (existing) | `transcript.segment` {speaker, text} | Reconnects (existing) |
| Practice panel | Desktop | `platform.getCaptureStatus()` (existing) | `CaptureStatus` | Web platform returns idle; shown as "not listening" |

No new endpoints, IPC channels or contracts.

### [A] Assemble

1. **Tests first:** an OverlayPage test (MockInterview → practice panel, other modes → `LiveAssistant`)
   and practice panel tests (compact layout, mic status shown, mic-error message shown).
2. Add a `compact` prop to `PracticePanel`: tighter spacing, no "Previous attempts" list in the
   overlay, and a scrollable body for a 440×360 window.
3. Add a small mic status line to the panel, reusing the `CaptureIndicator` polling.
4. `OverlayPage` branches on `session.mode`.
5. Main window: what the session page shows for a mock interview while it's live (question 1).
6. Run lint, typecheck and tests for `ui` locally; `desktop` is unchanged.
7. "What we adjusted" review, then a PR. The owner runs the manual test on Windows.

### [S] Stress-test

- **Failure, no mic:** the overlay shows "Mic ✗" plus the capture message within 1 poll (1 s).
- **Failure, quiet speech:** if the mic shows ✓ but no answer text appears within ~5 s of speaking,
  the owner checks the Transcript page; no Candidate lines points to speech detection (threshold).
- **Failure, overlay closed:** practice continues in the main window if it's kept there (question 1).
- **Data integrity:** one practice panel instance per session view. If both windows show practice,
  only the user's click finishes an attempt; the 4 s silence auto-finish must not fire twice
  (question 1 decides whether this risk exists).
- **Security:** nothing new crosses a process boundary; the overlay uses the same sandboxed preload.

---

## GOTCHA

=== GOALS ===
- In a Mock interview session, Start session opens the overlay with the practice flow: Next
  question, Done answering, live answer text, score and feedback.
- The overlay shows whether the mic is live, and why not when it isn't.
- Live interview and Sales/general sessions are unchanged; their overlay still shows `LiveAssistant`.
- `ui` coverage stays at or above 75%, and lint, typecheck and tests are clean.

=== ORCHESTRATION ===
1. Write the failing tests (OverlayPage mode branch; compact practice panel; mic status and error).
2. `PracticePanel`: `compact` prop plus the mic status line.
3. `OverlayPage`: branch on `session.mode`.
4. Session page main-window change (per the answer to question 1).
5. Run lint, typecheck and tests for `ui`; fix anything they flag.
6. "What we adjusted" review → PR (`Closes #<issue>`), with manual test steps for the owner.

=== TOOLS ===
React 19.3.1, React Router 8.5.0, Tailwind 4.3.3, shadcn/ui, Vitest 5.0.5, Testing Library 16.3.3,
all already in `src/ui/package.json`. No new dependencies.

=== CONTEXT ===
- UI code lives in `src/ui` and reaches the desktop only through `src/ui/src/platform/`.
- The overlay window is created by `createOverlayWindow` in `src/desktop/src/windows.ts`; it isn't changed.
- Mock interviews already capture the mic only (`capture-controller.ts`, `micOnly`).
- Cloud sessions can't run Electron or use a mic; the owner verifies on Windows.

=== HEURISTICS ===
DO:
- Reuse `PracticePanel`, `useSessionEvents` and the `CaptureIndicator` polling. Is there no second
  copy of the practice logic? (yes/no)
- Make the overlay practice usable at 440×360 without horizontal scrolling. (yes/no)
- Write the tests before the code. (yes/no)
DON'T:
- Don't create a new BrowserWindow or change overlay window options. (yes/no)
- Don't import `electron` in `src/ui`. (yes/no)
- Don't change backend practice endpoints, scoring or speech-detection settings. (yes/no)
- Don't let the 4 s silence timer finish the same attempt twice. (yes/no)

=== ARGS ===
- Overlay window: 440×360 (existing), route `/overlay/:sessionId` (existing)
- Silence auto-finish: `PRACTICE_SILENCE_MS = 4_000` (existing)
- Mic status poll: `CAPTURE_POLL_MS = 1_000` (existing)
- Speech-detection threshold: −40 dBFS (existing, **not changed** here)
- UI wording: see question 4

---

## Open questions for the owner

1. **Main window during practice:** (a) **Recommended:** the overlay runs the practice, and the
   session page shows only "Practising in the overlay" plus the previous attempts and their scores.
   (b) Both windows show the full practice panel. (c) No change to the main window.
2. **"Previous attempts" in the overlay:** **Recommended:** leave it out to keep the overlay small,
   since the main window keeps the history. Or show the last attempt's score only.
3. **Mic indicator:** **Recommended:** a status line ("Mic ✓ Listening" / "Mic ✗ + reason"),
   reusing what we have. Or also a live level bar (needs a new audio-level signal from the capture
   window, so more work and out of scope above).
4. **Wording:** keep the current labels ("Next question", "Done answering", "Your answer",
   "Listening…", "Thinking of a question…", "Scoring your answer…")? Recommended: yes.
5. **Issue:** I'll open "Practice: run mock interviews in the overlay" (`layer:frontend`,
   `type:feature`, `priority:high`, `module:practice`, milestone v0.2 Core). OK?
