#!/bin/sh
. "$(dirname "$0")/lib.sh"
export HOME=$T/home CLAUDE_SETTINGS=$T/home/.claude/settings.json SANKOFA_NO_SYSTEMD=1 SANKOFA_NO_BUILD=1
unset XDG_BIN_HOME XDG_CONFIG_HOME || true
mkdir -p "$HOME/.claude"
printf '{"permissions":{"allow":["Bash(ls)"]}}\n' > "$CLAUDE_SETTINGS"

"$ROOT/install.sh" >/dev/null
[ -L "$HOME/.local/bin/sankofa" ] || fail "symlink missing"
assert_eq "$(jq -r '.hooks.Stop[0].hooks[0].command' "$CLAUDE_SETTINGS")" "sankofa hook stop" "Stop hook added"
assert_eq "$(jq -r '.hooks.SubagentStop[0].hooks[0].command' "$CLAUDE_SETTINGS")" "sankofa hook subagent-stop" "SubagentStop hook added"
assert_eq "$(jq -r '.hooks.SessionEnd[0].hooks[0].command' "$CLAUDE_SETTINGS")" "sankofa hook session-end" "SessionEnd hook added"
assert_eq "$(jq -c '.hooks.PreToolUse[0] | [.matcher, .hooks[0].command, .hooks[0].timeout]' "$CLAUDE_SETTINGS")" \
  '["Edit|Write|MultiEdit|NotebookEdit","sankofad gate claude-code",5]' "edit gate added with matcher and timeout"
assert_eq "$(jq -r '.permissions.allow[0]' "$CLAUDE_SETTINGS")" "Bash(ls)" "existing settings kept"
ls "$HOME/.claude"/settings.json.bak-* >/dev/null || fail "no backup"

"$ROOT/install.sh" >/dev/null
assert_eq "$(jq '.hooks.Stop|length' "$CLAUDE_SETTINGS")" 1 "idempotent"

U=$HOME/.config/systemd/user/sankofa-watch.service
UD=$HOME/.config/systemd/user/sankofad.service
if [ -x "$ROOT/bin/out/sankofad" ]; then
  [ -L "$HOME/.local/bin/sankofad" ] || fail "daemon symlink missing"
  assert_contains "$(cat "$UD")" "ExecStart=$HOME/.local/bin/sankofad serve" "daemon unit points at the daemon"
fi
[ -L "$HOME/.local/bin/sankofa-watch" ] || fail "watcher symlink missing"
assert_contains "$(cat "$U")" "ExecStart=$HOME/.local/bin/sankofa-watch" "unit points at the watcher"

"$ROOT/install.sh" --uninstall >/dev/null
[ ! -e "$U" ] || fail "unit not removed"
[ ! -e "$UD" ] || fail "daemon unit not removed"
[ ! -e "$HOME/.local/bin/sankofad" ] || fail "daemon symlink not removed"
[ ! -e "$HOME/.local/bin/sankofa-watch" ] || fail "watcher symlink not removed"
[ ! -e "$HOME/.local/bin/sankofa" ] || fail "symlink not removed"
assert_eq "$(jq -r '.hooks // "none"' "$CLAUDE_SETTINGS")" none "hooks removed"
assert_eq "$(jq -r '.permissions.allow[0]' "$CLAUDE_SETTINGS")" "Bash(ls)" "settings intact after uninstall"
echo "ok install"
