# How AI sessions work on this repo

These are the working agreements and the lessons learned building InterviewCopilot with Claude Code.
They live in the repo so every session sees them: local, cloud, or a fresh clone. The rules that apply
to every change are in [`CLAUDE.md`](../CLAUDE.md). This file covers the *how*.

## Method: ATLAS + GOTCHA, scaled to the task

Say which tier you're using in one line before starting.

| Tier | When | What to do |
|------|------|------------|
| **Full** | New feature, new component, multi-file change, anything touching data schemas, auth, infra or CI/CD | Fill in ATLAS (Architect incl. out of scope, Trace, Link, Assemble, Stress-test) → map it to a GOTCHA brief (Goals, Orchestration, Tools, Context, Heuristics, Args) → save it as `docs/atlas-gotcha/<feature>.md` → **show it to the owner and wait for an explicit OK before building** → build in Orchestration order → finish with a "What we adjusted" review in the brief. |
| **Light** | A small fix or single-file change | Answer the five ATLAS questions in a few lines (boundaries, flow, connections, order, how to verify), then proceed. |
| **None** | Questions, reading, research, explanations | Just answer. |

- One GOTCHA brief per component, not one for a whole system. If unsure of the tier, go one up.
- Ask about anything you'd otherwise have to guess. **Never invent design decisions**, such as names,
  routes, ports, schemas, UI wording, storage choices or tech choices. Put them to the owner as questions,
  with a recommendation.
- Background: Victor Zaragoza's ATLAS + GOTCHA series, <https://blog.victorz.cloud/blog?series=atlas-gotcha>.
- Past briefs, which make good examples: `docs/atlas-gotcha/` (`wave-1.md` … `wave-3.md`, `release-beta.md`).

## Waves: several issues in parallel

Work on open issues is done in waves. The cheaper model builds, and the stronger model briefs and reviews.

1. **Brief.** The main (Opus) session writes `docs/atlas-gotcha/wave-N.md`, with one section per issue,
   the expected file overlaps and a merge order. It asks the owner every open question, then waits
   for the OK.
2. **Step 0, on `main`.** The main session lands the shared contracts first: interfaces, DTOs, UI
   types, and **all** schema changes in **one** EF migration. That way no two builders generate
   migrations in parallel.
3. **Build.** One Sonnet agent per issue, each in its own git worktree on a branch `issue/<n>-<slug>`.
   Tests come first. Agents never commit, push or make design decisions; they list open questions in
   their final report instead.
4. **Review.** The main session reads each diff against the brief, re-runs **every** gate itself,
   normalises line endings, commits, pushes and opens one PR per issue.
5. **Merge, in the stated order.** Merge `origin/main` into the branch with a normal merge
   (`chore: merge main into <branch>`, which the conventional-commit hook needs; never force-push).
   Re-test, wait for CI, then squash-merge. Repeat for the next PR.
6. **Close out.** Add the "What we adjusted" review to the brief, then delete the merged worktrees
   and branches (ask first).

Things to watch:

- A squash commit keeps the branch's commit bodies. A `Closes #N` in a commit body closes the issue
  even if the PR text says "Part of #N". Use "Part of" in commits when the issue must stay open.
- When an agent hits a usage limit, resume it (it keeps its state) rather than starting a new one.
- Run vitest coverage as `pnpm exec vitest run --coverage`. `pnpm test -- --coverage` passes the flag
  as a file filter and measures nothing.

## Cloud sessions (Claude Code on the web)

- Setup script: `bash scripts/cloud-setup.sh`. It installs the .NET SDK from `global.json`, the pnpm
  dependencies (without the Electron binary), the .NET local tools, `jq` for the Claude hooks, and the
  git hooks. If the cloud environment's network access is restricted, it needs to reach `dot.net`
  and `builds.dotnet.microsoft.com` (the .NET SDK), the npm registry, NuGet and PyPI.
- Pushing: in the cloud (`CLAUDE_CODE_REMOTE=true`), `.claude/hooks/block-git-push.sh` allows exactly
  one plain `git push [-u] origin <feature-branch>`, so the session can open a PR. Pushing `main`,
  tags, force-pushes, deletes and chained commands stay blocked. **The owner merges PRs and tags
  releases.**
- What can't be checked in the cloud: anything that needs the Electron app, a microphone or system
  audio, Ollama, or Windows/macOS. Say so in the PR, and leave that verification to the owner.
- Dependabot commands (`@dependabot rebase`) posted through the session's GitHub tools arrive with
  the mention garbled, and Dependabot ignores them. Refresh a stale Dependabot PR with "Update
  branch" instead, or ask the owner to post the command.

## Windows gotchas (the owner's dev machine)

- **Run `dotnet test` in Release.** Smart App Control blocks freshly built, unsigned Debug test DLLs
  ("An Application Control policy has blocked this file"). It can also block a particular Release
  build. A harmless source edit (reword a doc comment) produces a new binary. On 2026-10-26 it began
  blocking the QuestPDF native DLL and the test executables outright, and the owner turned it off.
- **Never write code through PowerShell double-quoted strings or here-strings.** They expand `${…}`
  and destroy TypeScript template literals. Use the file edit tools.
- **Never run `dotnet format` in fix mode on code that doesn't compile.** It replaced test bodies with
  `NotImplementedException`. Use `--verify-no-changes` until it builds.
- **Line endings:** tools sometimes write CRLF or a BOM. Normalise to LF without a BOM before committing.
- **Hidden UAC prompts:** the owner's shell (Seelen UI) hides admin prompts, for example from winget,
  so an install looks stuck at "Starting package install…". Ask the owner to Alt+Tab to the
  permission window.
- **The push hook blocks Bash pushes locally.** After the owner explicitly OKs a push, it has been done
  from PowerShell. Never push without that OK.

## Project state (keep this current)

- Beta: `v0.1.0-beta.1` and `v0.1.0-beta.2` are pre-releases on GitHub. Windows is tested. The macOS
  builds are microphone-only (#15) and untested on a real Mac.
- #24 (latency benchmark) stays open until a run on 16 GB reference hardware
  (`docs/benchmarks/README.md`). The first run, on a 32 GB laptop with CPU-only Ollama, gave a P95
  time-to-first-token of 884 ms, a PASS.
- #36 (first beta) stays open until the owner's fresh-user check on a clean Windows account passes.
- The v1 functional requirements (FR-01 to FR-18) are built and tested on Windows. Every module epic is
  closed except Audio (#13: macOS capture #15, macOS call detection #59) and Copilot (#21: #24).
- FluentAssertions stays on 7.x; 8.x has a commercial licence. Dependabot ignores 8 and later (#65).
- The Dependabot backlog was cleared on 2026-10-26: all GitHub Actions moved to their Node 24 majors.
  `release.yml` only runs on a tag, so the next beta tag is the first real run of the new artifact
  actions and electron-builder 26.18. Check that run.
- `dependency-review-action` v5 still honours `deny-licenses`, but it's deprecated. When v6 arrives,
  move the GPL/AGPL block to `allow-licenses`.
