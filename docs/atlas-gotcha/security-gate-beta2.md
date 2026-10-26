# Brief: known-vulnerability CI gate + v0.1.0-beta.2. ATLAS + GOTCHA, compact

Status: **approved by owner** (2026-10-26).

Owner decisions (2026-10-26):
- **D1:** a new job in `governance.yml`, required through `Governance gate`.
- **D2:** fail on high/critical in shipped deps (`--prod`, plus .NET); dev deps only warn.
- **D3:** beta.2 is security-only, tagged before #60 merges.
- **D4:** Dependabot PR #51 is reviewed separately, not in beta.2.

Context: on the push after v0.1.0-beta.1, GitHub reported 7 Dependabot alerts on `main`. The worst
was Electron 44.7.2 (compromised eMudhra intermediate certificates), which ships in the beta.1 installers.
This branch already bumps electron to 44.9.2 and fast-uri to 3.1.9. The owner dismissed node-forge (#1)
as dev-only. The owner asked for a beta.2 with the patched Electron and a CI check.

## A: Architect

- **Why the alerts slipped through:** `governance.yml` has `dependency-review-action`, but it only
  checks dependencies a PR *adds or changes*. Nothing fails CI on vulnerabilities in what's already in
  the lockfile, or on CVEs published after a dependency was merged.
- **Goal:** CI fails when a dependency that ships has a known high or critical vulnerability, on every
  PR, on `main`, and in the existing weekly scheduled run (which catches newly published CVEs).
- **Out of scope:** auto-merging Dependabot PRs, code signing, changing release channels.

## T: Trace

1. PR / push to `main` / Monday 06:00 schedule → `governance.yml`.
2. New job **"Secure — known vulnerabilities"**:
   - `pnpm install --frozen-lockfile` then `pnpm audit --prod --audit-level high`;
   - `dotnet list src/backend/InterviewCopilot.slnx package --vulnerable --include-transitive`, failing
     if the output lists a `High` or `Critical` package.
3. `governance-gate` gains the job in `needs`, so a finding blocks the merge like the other invariants.
4. Dev-only findings (`pnpm audit` without `--prod`) are printed as a `::warning::`, not a failure.

## L: Link

- The npm advisory DB (via `pnpm audit`) and NuGet's vulnerability feed (via `dotnet list`). Both are free
  and need no token. If either service is down, the job fails, and a re-run fixes it.
- Today's state: `pnpm audit --prod` finds no vulnerabilities on this branch, and no .NET project has a
  vulnerable package. So the gate passes now.

## A: Assemble (order)

1. Add the job to `.github/workflows/governance.yml` and to `governance-gate.needs`. Update
   `GOVERNANCE.md` where it lists the Secure checks.
2. One PR from `chore/security-alerts`: the deps bump (7b3510c), the gate, and this brief. CI must be green.
3. **beta.2:** after that PR merges, tag `v0.1.0-beta.2` on `main`. `release.yml` builds and publishes the
   pre-release with generated notes (version comes from the tag). The hook blocks `git push`, so **the
   owner pushes the tag**. Then I check the release assets and add a short note saying beta.1 users
   should update (no auto-update).

## S: Stress-test

- **False alarms:** a high advisory with no fix in a shipped package blocks every PR. Escape hatch:
  dismiss or ignore it explicitly (`pnpm.auditConfig.ignoreGhsas` in `package.json`, with a comment
  saying why). The owner decides each one.
- **Flaky registry:** re-run the job. No retries built in.
- **Scheduled run:** if a new CVE lands mid-week, the Monday run goes red on `main`, which is the signal.

## Decisions for the owner

- **D1 Where:** new job in `governance.yml`, required through `Governance gate`. *(recommended)*
  The alternative is a step in `ci.yml`.
- **D2 Scope:** fail on high and critical in **shipped** deps (`--prod`, plus .NET); dev deps warn only.
  *(recommended. Failing on dev deps would fail today on node-forge, which has no fix.)*
- **D3 What goes in beta.2:** (a) `main` once the security PR merges, which also includes PR #60
  (meeting detection) if you merge #60 first; or (b) security fix only, tagging before #60 merges.
  *(recommended: (b), so beta.2 is a pure security release and goes out sooner.)*
- **D4 Dependabot PR #51:** after this merges it rebases without Electron. Review it separately, not in beta.2.
  *(recommended)*

## GOTCHA

- **Goals:** shipped dependencies with known high/critical vulns fail CI; beta.2 ships Electron 44.9.2.
- **Orchestration:** gate job → GOVERNANCE.md → PR (green) → merge → owner pushes tag → verify release.
- **Tools:** `pnpm audit`, `dotnet list package --vulnerable`, GitHub Actions, `gh release view`.
- **Context:** `governance.yml`, `release.yml`, `GOVERNANCE.md`, `docs/atlas-gotcha/release-beta.md`.
- **Heuristics:** pnpm only; no push or tag push by Claude; every ignored advisory has a written reason
  and the owner's OK.
- **Args:** tag `v0.1.0-beta.2`; audit level `high`; D1–D4.
