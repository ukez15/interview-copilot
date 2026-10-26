# Governance — InterviewCopilot

This project uses the **full Gartner seven-invariant model** (`context/governance.md`). Every change
must pass all seven at three checkpoints: Claude hooks while code is generated, pre-commit on your
machine, and GitHub Actions in a clean environment.

## How each invariant is enforced here

| # | Invariant | Generation (Claude hooks) | Pre-commit / pre-push | CI |
|---|-----------|---------------------------|-----------------------|----|
| 1 | **Functional** | `enforce-invariants.sh` (eslint/jq/yamllint per file) | `dotnet format`, `pnpm lint`, `pnpm typecheck` | `ci.yml`: build with `TreatWarningsAsErrors`, Roslyn + Sonar + Roslynator analyzers, eslint, tsc |
| 2 | **Tested** | `require-tests.sh` warns when production code has no sibling test | `dotnet test`, `pnpm test` (pre-push) | `ci.yml`: backend coverage gate ≥75% lines; vitest thresholds ≥75% per package |
| 3 | **Secure** | `block-secrets.sh` | gitleaks | `governance.yml`: gitleaks, CodeQL (C# + TS), dependency review (high CVEs, copyleft licences), known vulnerabilities in shipped deps (`pnpm audit --prod`, `dotnet list package --vulnerable`; high/critical fail, dev deps warn, also weekly); Dependabot weekly; manual `/security-review` before each release |
| 4 | **Scalable** | — | — | Review checklist: async I/O, no unbounded queries, streaming answers, bounded audio buffers (ATLAS stress-tests) |
| 5 | **Performant** | — | — | NFR-05 latency budget (2–5 s to first token) measured manually per release on reference hardware; pipeline overhead covered by tests |
| 6 | **Observable** | — | — | OpenTelemetry logs/metrics/traces in `ServiceDefaults`; no export endpoint configured (by choice), so nothing leaves the machine. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to enable |
| 7 | **Auditable** | `provenance-stamp.sh` (basic ledger in `.claude/provenance/`, gitignored), `block-claude-attribution.sh`, `block-git-push.sh`, `block-no-verify.sh` | conventional-commit hook | `governance.yml`: PR-title check, no-AI-attribution check, SBOM (syft) |

## Choices made in the scaffold interview

| Setting | Value |
|---------|-------|
| Model | Full |
| Reasoning-based security scan | Pilot — run `/security-review` in Claude Code manually (no paid CI calls) |
| LLM gateway | None |
| OpenTelemetry endpoint | None |
| MCP servers for agents in this repo | None (`enabledMcpjsonServers: []`) |
| WebFetch allowlist | 14 documentation domains (see `.claude/settings.json`) |
| Provenance | Basic |

## Limits worth knowing

- **Hooks live in `.claude/settings.json`**, which a contributor can edit locally. To make them
  non-bypassable, deploy them as managed settings (`context/governance.md`). For a personal open
  source project this isn't needed; CI re-runs every check anyway.
- **MCP lockdown** in project settings covers project-level servers only. Connectors you enabled
  globally in Claude are still available unless you deny them in managed settings
  (`deniedMcpServers`).
- **WebFetch**: listed domains are pre-approved; anything else asks for permission instead of being
  hard-blocked.
- **Signed artifacts**: installers are unsigned (zero-cost principle). The SBOM is still produced.

## Required repository settings (after the repo exists)

- Branch protection on `main`: require PRs, and require the **`ci`** jobs and **Governance gate** to pass.
- Enable Dependabot alerts and secret scanning (free for public repos).
