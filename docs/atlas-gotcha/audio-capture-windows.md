# ATLAS + GOTCHA brief — Audio capture on Windows (#14)

Status: **approved by owner** (2026-10-16). Decisions already made: include a minimal backend receiver;
default mic plus a picker in Settings; capture runs in a hidden capture window.

## ATLAS

### [A] Architect
- **Purpose:** stream two labelled tracks to the backend during a live session: the interviewer (system audio) and the candidate (mic).
- **Constraints:** Windows 10 22H2+/11; capture starts only from a user action; no audio written to disk; low CPU next to Whisper + Ollama.
- **Tech:** Electron 44 `session.setDisplayMediaRequestHandler` answering `getDisplayMedia` with `audio: 'loopback'` (Windows-only per Electron docs), `getUserMedia` for the mic, Web Audio `AudioWorklet`, browser `WebSocket`; ASP.NET Core WebSockets on the backend.
- **Out of scope:** macOS (#15); voice-activity buffering and transcription (Transcription issue); starting from the meeting prompt (#16); echo cancellation; per-app capture (loopback captures all system sound, including notifications).

### [T] Trace
1. User starts a session → UI calls `platform.startCapture(sessionId)` → main process creates the hidden capture window.
2. The capture window calls `getDisplayMedia({ audio: true, video: true })`. Main answers with the primary screen source and `audio: 'loopback'`; the video track is stopped immediately.
3. The capture window calls `getUserMedia({ audio: { deviceId } })` with the mic chosen in Settings (default device if none).
4. An AudioWorklet per track downmixes to mono, resamples to 16 kHz and converts to PCM16 LE.
5. Every 100 ms a frame `[speaker:1 byte][PCM16 × 1600 samples]` is sent on `ws://127.0.0.1:5480/ws/sessions/{id}/audio?access_token=…`.
6. The backend receiver authenticates, checks session ownership and `Live` status, validates frames, and counts bytes per speaker (exposed for status; no transcription yet).
7. User ends the session → `platform.stopCapture()` → tracks stop, socket closes, window is destroyed.

### [L] Link
| From | To | Method | Contract | Failure mode |
|------|----|--------|----------|--------------|
| UI | Main | IPC `capture:start` / `capture:stop` / `capture:status` | sessionId string | not in a trusted frame → rejected |
| Main | Capture window | window creation + query params | sessionId, mic deviceId, token via IPC (not in URL) | window crash → status `error`, UI shows retry |
| Capture window | Backend | WebSocket, binary | frame = 1 + 3,200 bytes; speaker 0 = Interviewer, 1 = Candidate | drop → buffer ≤ 5 s, reconnect with backoff; overflow drops oldest + warns |
| Backend | — | `/ws/sessions/{id}/audio` | bearer token as `access_token` query (browsers can't set WS headers) | bad token 401; not owner 404; not live 409; malformed frame → close 1003 |

### [A] Assemble
1. Pure modules with tests first: `pcm.ts` (downmix, resample, float→PCM16, frame encode), `frame-buffer.ts` (bounded 5 s queue), `reconnect.ts` (backoff schedule).
2. Backend receiver endpoint + frame validator + integration tests.
3. Capture window (HTML + preload-free script, sandboxed) + AudioWorklet processor.
4. Main-process wiring: display-media handler, capture window lifecycle, IPC.
5. UI: start/stop capture with session, status pill, mic picker in Settings.

### [S] Stress-test
- 60-minute session: memory flat (bounded buffer, no growing arrays).
- Socket killed mid-call → reconnect ≤ 5 s, buffered frames sent in order.
- Mic unplugged → interviewer track continues, status shows "mic unavailable".
- CPU for capture < 3% on reference hardware (manual check).
- Token never appears in logs; a frame from a non-owner session is refused.

## GOTCHA

=== GOALS ===
During a live session on Windows, both tracks reach the backend as correctly labelled 16 kHz PCM16 frames every 100 ms, survive socket drops with ≤ 5 s recovery, and the user can pick the mic.

=== ORCHESTRATION ===
The Assemble order above; one commit per step on branch `issue/14-audio-capture-windows`.

=== TOOLS ===
Electron 44.7.2, Web Audio AudioWorklet, WebSocket, ASP.NET Core 10 WebSockets, Vitest 5, xUnit v3.

=== CONTEXT ===
`GOTCHA-PROJECT.md`; audio contract in `atlas/transcription-pipeline.md`.

=== HEURISTICS ===
DO: sandbox the capture window (contextIsolation, no nodeIntegration); keep encoding logic pure and tested; validate every frame on the backend; ask before capture (user action only).
DON'T: write audio to disk; log tokens or audio; import electron in `src/ui`; send audio anywhere but 127.0.0.1.

=== ARGS ===
| Arg | Value |
|-----|-------|
| Sample rate / channels / format | 16,000 Hz / mono / PCM16 LE |
| Frame duration / payload | 100 ms / 3,200 bytes (+1 speaker byte) |
| Speaker bytes | 0 = Interviewer, 1 = Candidate |
| Reconnect buffer | 5 s (50 frames per speaker) |
| Backoff | 0.5 s, 1 s, 2 s, then every 4 s |
| WebSocket path | `/ws/sessions/{id}/audio` |
| Max inbound message | 3,201 bytes |
| Settings key | `micDeviceId` (null = Windows default) |

## What we adjusted (post-build review, 2026-10-16)

**Verified:** 130 backend tests (98.9% lines), 36 desktop / 43 UI / 13 extension tests, lint, typecheck,
pre-commit. An end-to-end smoke test on Windows 11 ran the compiled capture window against the real
backend. Both tracks reached the backend in about 4 s: 118,400 interviewer bytes and 108,800 mic
bytes, in 71 frames of exactly 3,201 bytes.

- **Goals:** met. Not measured yet: the CPU budget (< 3%) and the 60-minute memory check. Both stay on
  the manual release checklist.
- **Orchestration:** followed the Assemble order. The work was committed in four logical commits rather
  than one per step.
- **Tools:** as listed. Additions:
  - The capture window's scripts are built by a second tsconfig (`tsconfig.capture.json`, ES modules)
    into `dist-electron/renderer/capture`, served from `app://interview-copilot/capture/`.
  - `vitest.config.ts` was renamed to `.mts`, because the desktop package is CommonJS.
- **Context:** respected. The capture window gets its token over IPC, and only its own webContents can
  read it. The WebSocket URL carries the token in the query string, because browsers can't set
  WebSocket headers. ASP.NET request logging stays at Warning, so the query string isn't logged.
- **Heuristics:** followed. Beyond the brief, for safety:
  - Mic and screen permissions are granted only to the app's own pages.
  - Loopback is handed only to the capture window.
  - The mic uses Chromium's built-in echo cancellation and noise suppression (a constraint flag, not a
    custom implementation).
- **Args:** all used. Two new values:
  - The UI polls capture status every 1 s (`CAPTURE_POLL_MS`).
  - `GET /api/sessions/{id}/audio/stats` exposes the per-session counters.
- **Also added:** a single-instance lock that reopens the main window when the app is launched again,
  so capture that keeps running after the main window closes (the agreed decision) stays controllable.
