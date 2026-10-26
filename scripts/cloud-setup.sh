#!/usr/bin/env bash
# Sets up a fresh Linux clone for Claude Code cloud sessions (or any Linux dev box): the .NET SDK
# from global.json, pnpm dependencies, the .NET local tools, jq for the Claude hooks, and the git
# hooks (pre-commit, commit-msg, pre-push), which a fresh clone doesn't have.
# Safe to re-run. Use it as the cloud environment's setup script: bash scripts/cloud-setup.sh
set -euo pipefail

cd "$(dirname "$0")/.."

run_as_root() {
  if [ "$(id -u)" -eq 0 ]; then "$@"; elif command -v sudo >/dev/null 2>&1; then sudo "$@"; else
    echo "Need root for: $*" >&2
    return 1
  fi
}

# --- jq: every Claude hook in .claude/hooks parses its input with it --------------------------
if ! command -v jq >/dev/null 2>&1; then
  run_as_root apt-get update -qq
  run_as_root apt-get install -y -qq jq
fi

# --- .NET SDK, the version global.json asks for ----------------------------------------------
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$HOME/.local/bin:$PATH"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --jsonfile global.json --install-dir "$DOTNET_ROOT"
fi

# Later shells (Claude's Bash tool) need the same PATH.
PROFILE_LINE='export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$HOME/.local/bin:$PATH"; export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1'
grep -qxF "$PROFILE_LINE" "$HOME/.bashrc" 2>/dev/null || echo "$PROFILE_LINE" >> "$HOME/.bashrc"

# --- pnpm (pinned by package.json "packageManager") and dependencies -------------------------
if ! command -v pnpm >/dev/null 2>&1; then
  corepack enable 2>/dev/null || run_as_root corepack enable
fi
# Cloud sessions can't run the Electron app, so skip its ~100 MB binary.
ELECTRON_SKIP_BINARY_DOWNLOAD=1 pnpm install --frozen-lockfile

# --- .NET local tools (reportgenerator, dotnet-ef, …) ----------------------------------------
(cd src/backend && dotnet tool restore)

# --- git hooks: the same gates as local development and CI -----------------------------------
if ! command -v pre-commit >/dev/null 2>&1; then
  if command -v pipx >/dev/null 2>&1; then pipx install pre-commit; else python3 -m pip install --user --quiet pre-commit; fi
fi
pre-commit install --hook-type pre-commit --hook-type commit-msg --hook-type pre-push

echo "Cloud setup done: $(dotnet --version) / pnpm $(pnpm --version) / $(pre-commit --version)"
