#!/bin/sh
. "$(dirname "$0")/lib.sh"
export HOME=$T/home CLAUDE_SETTINGS=$T/home/.claude/settings.json
mkdir -p "$HOME/.claude"
printf '{"permissions":{"allow":["Bash(ls)"]}}\n' > "$CLAUDE_SETTINGS"

"$ROOT/install.sh" >/dev/null
[ -L "$HOME/.local/bin/sankofa" ] || fail "symlink missing"
assert_eq "$(jq -r '.hooks.Stop[0].hooks[0].command' "$CLAUDE_SETTINGS")" "sankofa hook stop" "Stop hook added"
assert_eq "$(jq -r '.hooks.SubagentStop[0].hooks[0].command' "$CLAUDE_SETTINGS")" "sankofa hook subagent-stop" "SubagentStop hook added"
assert_eq "$(jq -r '.permissions.allow[0]' "$CLAUDE_SETTINGS")" "Bash(ls)" "existing settings kept"
ls "$HOME/.claude"/settings.json.bak-* >/dev/null || fail "no backup"

"$ROOT/install.sh" >/dev/null
assert_eq "$(jq '.hooks.Stop|length' "$CLAUDE_SETTINGS")" 1 "idempotent"

"$ROOT/install.sh" --uninstall >/dev/null
[ ! -e "$HOME/.local/bin/sankofa" ] || fail "symlink not removed"
assert_eq "$(jq -r '.hooks // "none"' "$CLAUDE_SETTINGS")" none "hooks removed"
assert_eq "$(jq -r '.permissions.allow[0]' "$CLAUDE_SETTINGS")" "Bash(ls)" "settings intact after uninstall"
echo "ok install"
