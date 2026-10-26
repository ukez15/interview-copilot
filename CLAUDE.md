# CLAUDE.md — InterviewCopilot

## Project Overview

**InterviewCopilot** — a real-time AI conversation assistant that listens to live interviews, sales
calls or any high-stakes conversation and suggests answers instantly, plus a mock-interview practice
mode with scoring and feedback. Free, open source (MIT), zero cost by default.

- **Desktop app:** Electron (Windows 10 22H2+/11, macOS 13+) — React + Vite + TypeScript + shadcn/ui + Tailwind (pnpm only)
- **Backend:** .NET 10 Web API (Clean Architecture), runs **locally** on `http://127.0.0.1:5480`
- **Browser extension:** TypeScript, Manifest V3, Chrome + Edge + Firefox (companion only)
- **Database:** SQLite via EF Core (PostgreSQL when hosted — Mode C)
- **Speech-to-text:** local Whisper (Whisper.net) by default; Deepgram implemented but **disabled**
- **LLM:** Ollama (local) by default; BYOK cloud providers optional
- **Auth:** local accounts (hashed passwords); Google/GitHub sign-in in Mode C
- **Local dev:** .NET Aspire
- **Repo:** `ukez15/interview-copilot` (GitHub, GitHub Actions, GitHub Issues + Projects)

Full brief: `docs/atlas-gotcha/interview-copilot.md`. Project constitution: `GOTCHA-PROJECT.md`.

---

## How to work here (read first)

This file is self-contained: it doesn't rely on anything outside the repo, so local and cloud sessions
follow the same rules.

- **ATLAS + GOTCHA, scaled to the task.** Say which tier you're using in one line:
  - **Full** (new feature, multi-file change, schemas, auth, infra, CI/CD): write an ATLAS + GOTCHA brief
    in `docs/atlas-gotcha/<feature>.md`, **show it to the owner and wait for an explicit OK before
    building**, then build and add a "What we adjusted" review.
  - **Light** (a small fix or single-file change): answer the five ATLAS questions in a few lines, then proceed.
  - **None** (questions, research): just answer.
- **Never invent design decisions**, such as names, routes, ports, schemas, UI wording or tech choices.
  Ask the owner, with a recommendation.
- Working agreements, the wave workflow, cloud-session notes, Windows gotchas and current project
  state: **[`docs/ai-workflow.md`](docs/ai-workflow.md)**.
- **Cloud sessions:** run `bash scripts/cloud-setup.sh` first. They may push a feature branch and open
  a PR. The owner merges and tags.

---

## Operating modes

| Mode | Status | What it is |
|------|--------|------------|
| A — Desktop standalone | v1 | Desktop app detects a meeting, **asks** to start, shows a semi-transparent always-on-top overlay (opacity slider, default 85%). |
| B — Desktop + browser companion | v1 | Same as A, but answers are also shown as an overlay inside the meeting tab via the extension. The desktop app does all capture and AI work. |
| C — Browser-only, hosted | **Future — designed for, not built** | Extension + web frontend + hosted backend + cloud models. Everything below must keep this possible. |

**Design-for-Mode-C rules:** UI code lives in `src/ui` and must not import Electron directly — go
through `src/ui/src/platform/`. The backend must not assume it is local except in
`Infrastructure` adapters. Auth, storage, STT and LLM are always behind interfaces.

---

## Zero-cost principle (non-negotiable)

- The app must run fully free and offline with local Whisper + Ollama.
- Paid providers (Deepgram, cloud LLMs) are opt-in, BYOK, and **off by default**.
- No feature may require a paid service, hosted server or code-signing certificate to work.
- CI runs only free tooling.

---

## CRITICAL: Never Sign Commits as Claude

- **NEVER** add `Co-Authored-By: Claude` or any AI attribution to commits or PRs
- Conventional commits only: `feat:`, `fix:`, `docs:`, `test:`, `refactor:`, `chore:`, `ci:`, `perf:`, `build:`

---

## CRITICAL: Issue Governance

**Every piece of work MUST be tracked in a GitHub Issue before any code is written.**

1. **Title** — clear, actionable
2. **Labels** — at minimum `layer:*`, `type:*`, `priority:*`, plus `module:*`
3. **Milestone** — delivery phase
4. **Acceptance criteria** — from the SDD spec in `specs/`

Branch naming: `issue/<number>-<short-description>`. PRs reference the issue: `Closes #<number>`.
Flow per issue: **User Story (SDD) → ATLAS checklist → GOTCHA prompt → task breakdown → code (tests first) → review.**

---

## Architecture

```
src/
├── backend/                         ← .NET 10 solution (InterviewCopilot.slnx)
│   ├── src/
│   │   ├── InterviewCopilot.Domain          → entities, value objects, enums (zero dependencies)
│   │   ├── InterviewCopilot.Application     → use cases, provider interfaces, prompt building
│   │   ├── InterviewCopilot.Infrastructure  → EF Core/SQLite, Whisper, Deepgram, Ollama, BYOK LLMs, PDF
│   │   ├── InterviewCopilot.Api             → HTTP + streaming endpoints (entry point)
│   │   ├── InterviewCopilot.ServiceDefaults → OpenTelemetry, health checks
│   │   └── InterviewCopilot.AppHost         → .NET Aspire (dev only)
│   └── tests/ UnitTests, IntegrationTests, ArchTests
├── ui/                              ← React app: desktop renderer today, Mode C web frontend later
├── desktop/                         ← Electron main + preload: overlay, audio capture, meeting detection, keychain
└── extension/                       ← MV3 companion extension (Chrome/Edge/Firefox)
```

### Modules

Auth, Sessions, Documents, Audio, Transcription, Copilot, Practice, Transcripts, Providers, Overlay, Settings

| Module | Backend | UI | Desktop | Extension |
|--------|:-:|:-:|:-:|:-:|
| Auth | ✔ | ✔ | | |
| Sessions | ✔ | ✔ | | |
| Documents | ✔ | ✔ | | |
| Audio | | | ✔ | |
| Transcription | ✔ | ✔ | | |
| Copilot | ✔ | ✔ | | ✔ |
| Practice | ✔ | ✔ | | |
| Transcripts | ✔ | ✔ | | |
| Providers | ✔ | ✔ | ✔ (keychain) | |
| Overlay | | ✔ | ✔ | ✔ |
| Settings | ✔ | ✔ | ✔ | |

---

## Clean Code Rules

1. Meaningful names, small functions, single responsibility.
2. Depend on abstractions — every external service sits behind an `Application` interface.
3. No magic numbers/strings — constants or configuration (`MaxDocumentsPerSession = 10`, etc.).
4. Guard clauses, explicit domain exceptions, never swallow errors.
5. `record` types for DTOs; immutable where possible.
6. No class over ~200 lines.

---

## Testing Requirements

- **Minimum 75% coverage** (CI fails below), **target 80%+** — backend and TypeScript packages
- Unit: Domain + Application (every public method); Vitest for `ui`, `desktop`, `extension`
- Integration: API endpoints against an in-memory SQLite database
- Architecture: NetArchTest enforces layer dependencies
- Test naming (.NET): `Should_ReturnUser_When_ValidIdProvided`
- Tests are written **first** (SDD Execute stage)

---

## Supply Chain Security (pnpm enforced)

- **pnpm only** — never `npm`, `npx` or `yarn`; use `pnpm exec` / `pnpm dlx`
- Pin exact versions (`save-exact=true`); always commit `pnpm-lock.yaml`
- CI uses `pnpm install --frozen-lockfile`
- Install scripts are disabled; packages that need them are allow-listed in `pnpm.onlyBuiltDependencies`
- .NET: central package management in `src/backend/Directory.Packages.props`

---

## Guardrails

- Always use **pnpm** — never npm, npx or yarn
- **NEVER git push** without explicit user authorization. In cloud sessions the push hook allows only
  `git push [-u] origin <feature-branch>`, never main or tags.
- **NEVER commit as Claude** — no AI attribution in commits
- **NEVER --no-verify** — fix the hook, don't skip it
- **NEVER write secrets** to source — BYOK keys go to the OS keychain via the desktop app only
- **NEVER send audio, transcripts or documents off the machine** unless the user enabled a cloud provider
- **NEVER auto-start capture without the user's consent** — meeting detection prompts first (auto-start is an opt-in setting)
- **NEVER enable a paid provider by default** (zero-cost principle)
- **NEVER hand-edit AI-generated code that failed an invariant** — reject and regenerate
- **Always ask before any design decision** — never assume
- **Always ask before any destructive action**: `rm -rf`, deleting files or branches, destructive
  or mutating cloud CLI commands
- **Tests:** coverage at least 75% per package (target 80%), enforced in CI

---

## Governance — Gartner 7 Invariants (full model)

See `GOVERNANCE.md` and `context/governance.md`.

| Hook | Phase | Purpose |
|------|-------|---------|
| `block-npm.sh` | PreToolUse Bash | Enforce pnpm |
| `block-git-push.sh` | PreToolUse Bash | Block unauthorized push |
| `block-terraform-apply.sh` | PreToolUse Bash | Apply only via pipeline (future Mode C infra) |
| `block-no-verify.sh` | PreToolUse Bash | Forbid hook bypass |
| `block-destructive-actions.sh` | PreToolUse Bash | Block rm -rf, destructive az |
| `block-claude-attribution.sh` | PreToolUse Bash | No AI authorship in commits |
| `block-secrets.sh` | PreToolUse Write/Edit | Detect secrets |
| `enforce-invariants.sh` | PostToolUse Write/Edit | Lint/format each file written |
| `require-tests.sh` | PostToolUse Write/Edit | Warn when prod code lacks a sibling test |
| `provenance-stamp.sh` | PostToolUse Write/Edit | Append to `.claude/provenance/*.jsonl` |

Security review: manual, before each release — run `/security-review` in Claude Code.

---

## Environments (build channels)

| Channel | Trigger | Output |
|---------|---------|--------|
| dev | run from source (`pnpm dev`) | — |
| beta | tag `vX.Y.Z-beta.N` | unsigned installers, GitHub **pre-release** |
| release | tag `vX.Y.Z` | unsigned installers, GitHub **release** |

---

## Local Development

```bash
pnpm install
pnpm dev              # Aspire: backend + UI dev server; then `pnpm --filter @interview-copilot/desktop dev`
pnpm test             # all TypeScript tests
dotnet test src/backend/InterviewCopilot.slnx
```

Prerequisites: Node 24+, pnpm 10+, .NET 10 SDK, Ollama (with a 3–4B model pulled), ~5 GB disk for models.
