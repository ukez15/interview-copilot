#!/usr/bin/env bash
# Tests for block-git-push.sh: 0 = allowed, 2 = blocked. Run: bash .claude/hooks/tests/block-git-push.test.sh
# Locally every push is blocked. In Claude Code cloud sessions (CLAUDE_CODE_REMOTE=true) only a plain
# feature-branch push is allowed.
H="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/block-git-push.sh"
P="git pu""sh" # split so local hooks don't block running this file
fail=0

t() {
  local want=$1 remote=$2 cmd=$3
  printf '{"tool_input":{"command":"%s"}}' "$cmd" | CLAUDE_CODE_REMOTE=$remote bash "$H" >/dev/null 2>&1
  local rc=$?
  if [ "$rc" != "$want" ]; then
    printf 'FAIL want=%s got=%s remote=%s: %s\n' "$want" "$rc" "${remote:-no}" "$cmd"
    fail=1
  fi
}

# Local: always blocked.
t 2 ""   "$P -u origin claude/feature-x"
t 2 ""   "$P origin main"
# Cloud: plain feature-branch pushes are allowed.
t 0 true "$P -u origin claude/feature-x"
t 0 true "$P origin feat/jd-from-link"
t 0 true "$P --set-upstream origin fix/a.b"
# Cloud: everything else is blocked.
t 2 true "$P origin main"
t 2 true "$P origin MASTER"
t 2 true "$P -u origin HEAD"
t 2 true "$P origin v0.1.0-beta.3"
t 2 true "$P origin refs/tags/v1"
t 2 true "$P --force origin claude/x"
t 2 true "$P -f origin claude/x"
t 2 true "$P origin --delete claude/x"
t 2 true "$P origin claude/x && $P origin main"
t 2 true "$P origin claude/x; rm x"
t 2 true "$P --tags"
t 2 true "$P"
t 2 true "$P origin claude/x:main"
t 2 true "$P upstream claude/x"
# Not a push: never blocked.
t 0 true "git status"
t 0 ""   "git status"

[ "$fail" = 0 ] && echo "block-git-push: all cases pass"
exit $fail
