#!/bin/sh
# 0.14: what the other platforms' paths look like, as far as Linux can run them. The notifier in
# the native binary (sankofa watch once) with a fake notify-send; install.sh's launchd branch
# (SANKOFA_UNAME=Darwin) writing agents instead of systemd units, and removing them again;
# sankofa setup finding install.sh beside a checkout.
. "$(dirname "$0")/../0.2/lib.sh"
B=$ROOT/bin/out/sankofa
[ -x "$B" ] || { echo "skip platform (make build first)"; exit 0; }

# --- the notifier, called as the service does (no launcher in between)
mkrepo alpha; (cd alpha && $S watch add >/dev/null)
"$B" watch once; assert_eq "$(toasts)" 0 "first scan: no backlog"
(cd alpha && $S awaiting "sign the release" >/dev/null)
"$B" watch once
assert_eq "$(toasts)" 1 "sankofa watch once toasts the awaiting item"
assert_eq "$(cut -f2 "$TOASTS")" "alpha: awaiting you" "with the same title as before"

# --- launchd agents on macOS: install.sh writes plists, loads nothing here, removes them on uninstall
export HOME=$T/home CLAUDE_SETTINGS=$T/home/.claude/settings.json SANKOFA_NO_SYSTEMD=1 SANKOFA_NO_BUILD=1 SANKOFA_NO_MCP=1
export SANKOFA_UNAME=Darwin SANKOFA_LAUNCH_AGENTS=$T/agents TMPDIR=$T/tmp
unset XDG_BIN_HOME XDG_CONFIG_HOME XDG_DATA_HOME XDG_STATE_HOME || true
mkdir -p "$HOME/.claude" "$TMPDIR"
echo '{}' > "$CLAUDE_SETTINGS"
SANKOFA_HUB_LISTEN=http://100.64.0.7:8787/ "$ROOT/install.sh" --hub >/dev/null
for a in sankofad sankofa-watch sankofa-hub; do
  P=$SANKOFA_LAUNCH_AGENTS/io.sankofa.$a.plist
  [ -f "$P" ] || fail "no launchd agent for $a"
  python3 -c "import plistlib,sys; plistlib.load(open(sys.argv[1],'rb'))" "$P" || fail "$a plist is not valid plist XML"
done
assert_contains "$(cat "$SANKOFA_LAUNCH_AGENTS/io.sankofa.sankofad.plist")" "<string>$HOME/.local/bin/sankofad</string>" "the daemon agent runs the linked sankofad"
assert_contains "$(cat "$SANKOFA_LAUNCH_AGENTS/io.sankofa.sankofad.plist")" "<string>serve</string>" "with serve"
assert_contains "$(cat "$SANKOFA_LAUNCH_AGENTS/io.sankofa.sankofa-watch.plist")" "<string>watch</string>" "the notifier agent runs sankofa watch serve"
assert_contains "$(cat "$SANKOFA_LAUNCH_AGENTS/io.sankofa.sankofa-hub.plist")" "<string>http://100.64.0.7:8787/</string>" "the hub agent carries its address"
assert_contains "$(cat "$SANKOFA_LAUNCH_AGENTS/io.sankofa.sankofad.plist")" "<key>KeepAlive</key><true/>" "agents are kept alive"
[ ! -e "$HOME/.config/systemd/user/sankofad.service" ] || fail "a systemd unit was left on macOS"
[ ! -e "$HOME/.config/systemd/user/sankofa-watch.service" ] || fail "a systemd watch unit was left on macOS"
assert_eq "$(jq -r '.hooks.Stop[0].hooks[0].command' "$CLAUDE_SETTINGS")" "sankofa hook stop" "the hooks are wired on macOS too"
"$ROOT/install.sh" --uninstall >/dev/null
for a in sankofad sankofa-watch sankofa-hub; do [ ! -e "$SANKOFA_LAUNCH_AGENTS/io.sankofa.$a.plist" ] || fail "uninstall left the $a agent"; done
[ ! -e "$HOME/.local/bin/sankofa" ] || fail "uninstall left the command"

# --- sankofa setup from a checkout runs the install.sh beside it (SANKOFA_UNAME still Darwin: agents again)
"$B" setup >/dev/null
[ -f "$SANKOFA_LAUNCH_AGENTS/io.sankofa.sankofa-watch.plist" ] || fail "sankofa setup did not run install.sh"
"$B" setup --uninstall >/dev/null
[ ! -e "$SANKOFA_LAUNCH_AGENTS/io.sankofa.sankofa-watch.plist" ] || fail "sankofa setup --uninstall left the agent"
echo "ok platform"
