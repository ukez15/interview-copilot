# Next steps — InterviewCopilot

## 1. Git init and first commit

```bash
cd interview-copilot
git init -b main
pipx install pre-commit
pre-commit install --hook-type pre-commit --hook-type commit-msg --hook-type pre-push
git add .
git commit -m "chore: scaffold InterviewCopilot"
```

## 2. Create the GitHub repo, labels and milestones

```bash
bash scripts/setup-github.sh
```

It creates `ukez15/interview-copilot` (public), asks before it pushes `main`, then creates the
labels and five milestones (v0.1 Foundation → v1.0 Release). Afterwards, in the repo settings, turn
on branch protection for `main` and make the `ci` jobs and **Governance gate** required checks
(see `GOVERNANCE.md`).

## 3. Run it locally

Prerequisites: Node 24+, pnpm 10+, .NET 10 SDK, [Ollama](https://ollama.com).

```bash
pnpm install
ollama pull llama3.2:3b
pnpm dev:backend                                   # terminal 1: http://127.0.0.1:5480
pnpm --filter @interview-copilot/ui dev            # terminal 2: UI dev server on :5173
pnpm dev:desktop                                   # terminal 3: Electron app
```

Or `pnpm dev` for the Aspire dashboard (backend + UI together).

If Electron complains its binary is missing, pnpm skipped its install script. Run this once:

```bash
node src/desktop/node_modules/electron/install.js
```

What works today: create an account, set up sessions (mode, length, STAR/SMART, pinned type,
session prompt), attach up to 10 `.txt`/`.md` documents, **type or paste** a question and get a
streamed answer from Ollama with Regenerate/Shorter/Longer, open the overlay, adjust opacity, and
export a PDF. Live audio capture and transcription are the first issues (milestone v0.2 Core).

Tests:

```bash
dotnet test --solution src/backend/InterviewCopilot.slnx
pnpm test:coverage
```

## 4. Create the initial issues

```bash
bash scripts/create-issues.sh
```

11 epics (one per module) and 19 issues, each with acceptance criteria, its ATLAS checklist and
the SDD → ATLAS → GOTCHA task list.

## 5. First iteration (v0.2 Core)

Work order follows the ATLAS Assemble phases (`docs/atlas-gotcha/interview-copilot.md`):

1. **Audio:** capture on Windows (your main machine), then macOS.
2. **Transcription:** audio WebSocket + voice-activity buffering, then the Whisper model download UI.
3. **Copilot:** auto-answer detected questions, then the events WebSocket so the overlay and
   extension update live.
4. **Documents:** PDF/DOCX extraction. Most resumes are PDFs, so this matters early. It needs a
   library choice from you first.

For each issue: write the story in `specs/stories/`, review the ATLAS checklist, write the GOTCHA
prompt, then tests first.

## 6. Before the first beta (`v0.1.0-beta.1`)

- Run `/security-review` in Claude Code (manual reasoning-based scan, per the governance choice).
- Measure the NFR-05 latency on your machine.
- Tag and push the tag yourself: `git tag v0.1.0-beta.1 && git push origin v0.1.0-beta.1`. The
  release workflow builds unsigned Windows/macOS installers and the extension packages, and
  publishes a GitHub pre-release.

## Open decisions for you

- Default Ollama model is `llama3.2:3b` (picked within your "3–4B" choice). Keep or change.
- Library for PDF/DOCX text extraction.
- Document retrieval: keyword matching now; embeddings later?
- Languages other than English (Whisper is set to English).
- Firefox add-on ID `interview-copilot@ukez15.github.io` (only matters if you publish to AMO).
