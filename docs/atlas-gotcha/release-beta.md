# Brief: #36, first beta (v0.1.0-beta.1). ATLAS + GOTCHA, compact

Status: **approved by owner** (2026-10-26), including the build-only `workflow_dispatch`.

Owner decisions (2026-10-26):
- The beta ships for **Windows and macOS**. macOS is labelled *microphone only, no system audio yet*
  (#15) and is untested on a real Mac.
- The desktop app **starts the bundled backend on launch and stops it on quit**, and reuses a healthy
  backend already on 5480.
- The **owner does the fresh-user check** on a clean Windows account, following the README from the
  published pre-release.

## A: Architect

- **Goal:** someone with a clean Windows (or macOS) machine downloads the pre-release, follows the
  README, and gets answers using only free software.
- **Already in place:**
  - `release.yml` builds per OS, plus the extension zips, and publishes a pre-release for `-beta` tags.
  - electron-builder bundles the UI and a self-contained backend.
  - `BackendProcess` starts the backend, restarts it up to 3 times, and stops it on quit.
- **Gaps found:**
  1. If something already answers `/health` on 5480 (a second copy, a dev backend), the app still
     spawns its own. That copy can't bind the port, crash-loops 3 times, and shows a misleading
     "port in use" error.
  2. The README misses **Smart App Control**. New Windows 11 PCs often have it on, and it blocks
     unsigned apps outright, with no "Run anyway".
  3. The README says the Whisper model downloads on first run. Since #19 it only downloads on a click.
  4. There's no macOS "microphone only" note.
  5. The release workflow has **never run**, so the first tag would also be its first test.
- **Out of scope:** code signing, auto-update, store listings, a hosted (Mode C) build, and macOS
  system-audio capture (#15).

## T: Trace (first run, Windows)

1. Download `InterviewCopilot-0.1.0-beta.1-windows-x64.exe` from Releases.
2. Get past SmartScreen ("More info → Run anyway"). If Smart App Control blocks the app, follow the
   README section on it.
3. Install, then launch. The app checks `/health`: if it's already healthy, it reuses that backend;
   otherwise it spawns the bundled one and waits up to 30 s.
4. Create an account, then follow the Whisper banner: click Download (about 466 MB).
5. Install Ollama and run `ollama pull llama3.2:3b`.
6. Start a session, ask a question, and get an answer.

## L: Link

- `release.yml` gains `workflow_dispatch` (proposed, needs your OK). It builds the installers and
  extension as **workflow artifacts only, with no release**, so the pipeline can be proven before the tag.
- Tagging pushes `v0.1.0-beta.1`, and that tag creates a public pre-release. I'll ask for your
  explicit OK at that point.

## A: Assemble (order)

1. `backend-process.ts` / `main.ts`: before spawning, if `/health` already answers, reuse it
   without spawning, and don't stop it on quit, since it isn't ours. Tests first.
2. README:
   - a Smart App Control section;
   - the Whisper click-to-download step;
   - a macOS "microphone only (beta)" note;
   - first-run steps in the order of the Trace;
   - a "Known limitations (beta)" list.
3. `release.yml`: add `workflow_dispatch` (artifacts only).
4. Local proof: build the Windows installer on this PC (`dotnet publish win-x64` + `pnpm dist`). Check
   that the packaged app starts its backend and stops it on quit. I won't install the app on your
   account.
5. PR, CI, merge, then run the dispatch workflow and check that all four artifacts build.
6. **With your OK:** push tag `v0.1.0-beta.1`, then check the published pre-release.
7. **You:** do the fresh-user check on a clean account and report back. I fix what you find in a
   `beta.2` if needed, then close #36.

## S: Stress-test

- **Port 5480 is held by a non-InterviewCopilot program:** `/health` fails, our backend crash-loops,
  and the existing fatal message shows. That's acceptable for the beta.
- **App quit while it reused a dev backend:** the dev backend keeps running, which is correct.
- **macOS build:** cross-built on Apple Silicon runners and never launched. The release notes say so.
- **Ollama missing:** answers show "Can't reach Ollama" (wave 1). The README tells users to install it first.

## GOTCHA

- **Goals:** the trace above works on a clean Windows account.
- **Orchestration:** Opus does it directly, since it's small and in sequence. No agents.
- **Tools:** pnpm, dotnet, gh, electron-builder.
- **Context:** this brief, `atlas/desktop.md`, `docs/benchmarks/README.md` (for the latency number in the README).
- **Heuristics:** no pushes or tags without your OK; zero cost; unsigned by design; never claim
  macOS works.
- **Args:** tag `v0.1.0-beta.1`; health timeout 30 s; port 5480.
