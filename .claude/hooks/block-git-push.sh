#!/bin/bash
# Hook: Block git push — user must push manually or give explicit permission
# Source: Governance → "NEVER git push without explicit user approval"

INPUT=$(cat)
COMMAND=$(echo "$INPUT" | jq -r '.tool_input.command // empty')

# --- Cross-platform support (added for Windows; no-op on macOS/Linux) -------
# lib/platform.sh detects the OS. On Windows (Git Bash) it strips the "\r" that
# jq.exe adds and converts C:\ backslash paths to forward slashes, so the checks
# below work unchanged. On macOS/Linux these calls return their input as-is.
# If lib/platform.sh is missing, the hook falls back to the original behaviour.
HOOK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -f "$HOOK_DIR/lib/platform.sh" ]; then
    . "$HOOK_DIR/lib/platform.sh"
    COMMAND=$(normalize_text "$COMMAND")
fi
# ---------------------------------------------------------------------------

if echo "$COMMAND" | grep -qE '\bgit\s+push\b'; then
    # Claude Code cloud sessions (CLAUDE_CODE_REMOTE=true) can only hand work back by pushing a feature
    # branch and opening a PR, so there, and only there, allow exactly one plain
    # `git push [-u|--set-upstream] origin <branch>`. The branch can't be main/master/HEAD or a tag;
    # force, delete, --tags and --all aren't allowed; and the push can't be chained with other commands.
    # Merging to main and tagging releases stay with the owner. Local sessions are unchanged.
    if [ "${CLAUDE_CODE_REMOTE:-}" = "true" ]; then
        BRANCH=$(echo "$COMMAND" | sed -nE 's#^[[:space:]]*git[[:space:]]+push([[:space:]]+(-u|--set-upstream))?[[:space:]]+origin[[:space:]]+([A-Za-z0-9._/-]+)[[:space:]]*$#\3#p')
        if [ -n "$BRANCH" ] && ! echo "$BRANCH" | grep -qiE '^(main|master|head)$|^refs/|^v[0-9]|^-'; then
            exit 0
        fi
        echo "Blocked: in the cloud only 'git push [-u] origin <feature-branch>' is allowed (not main, tags, force or chained commands). Open a PR; the owner merges." >&2
        exit 2
    fi
    echo "Blocked: git push is not allowed. The user must push manually or explicitly approve the push." >&2
    exit 2
fi

exit 0
