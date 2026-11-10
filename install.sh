#!/bin/sh
# Installs sankofa into ~/.local/bin, wires the Stop / SubagentStop hooks into
# ~/.claude/settings.json, and installs the sankofad and sankofa-watch systemd user
# services. Builds sankofad first if it is missing and the .NET SDK is present; without
# it the CLI works on the local ledger alone. Idempotent. --uninstall reverses it all.
# Env: XDG_BIN_HOME, CLAUDE_SETTINGS, XDG_CONFIG_HOME; SANKOFA_NO_SYSTEMD=1 writes the
# unit but does not call systemctl (tests, containers).
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
BIN=${XDG_BIN_HOME:-$HOME/.local/bin}
SETTINGS=${CLAUDE_SETTINGS:-$HOME/.claude/settings.json}
UNIT_DIR=${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user
UNITS="sankofad.service sankofa-watch.service"
STOP_CMD="sankofa hook stop"
SUB_CMD="sankofa hook subagent-stop"

command -v jq >/dev/null || { echo "install: jq is required" >&2; exit 1; }
mkdir -p "$BIN" "$(dirname "$SETTINGS")"
[ -f "$SETTINGS" ] || echo '{}' > "$SETTINGS"

backup() { cp "$SETTINGS" "$SETTINGS.bak-$(date -u +%Y%m%dT%H%M%SZ)"; }
sysd() { [ "${SANKOFA_NO_SYSTEMD:-0}" = 1 ] && return 0; command -v systemctl >/dev/null || return 0
         systemctl --user "$@" || echo "install: systemctl --user $* failed (no user session?)" >&2; }

if [ "${1:-}" = --uninstall ]; then
  for u in $UNITS; do
    [ -f "$UNIT_DIR/$u" ] && { sysd disable --now "$u"; rm -f "$UNIT_DIR/$u"; }
  done
  sysd daemon-reload
  rm -f "$BIN/sankofa" "$BIN/sankofa-watch" "$BIN/sankofad"
  backup
  tmp=$(mktemp)
  jq --arg a "$STOP_CMD" --arg b "$SUB_CMD" '
    def strip(ev; c): if .hooks[ev] then .hooks[ev] |= map(.hooks |= map(select(.command != c)) | select(.hooks|length>0)) else . end
      | if .hooks[ev] == [] then del(.hooks[ev]) else . end;
    strip("Stop"; $a) | strip("SubagentStop"; $b)
    | if .hooks == {} then del(.hooks) else . end' "$SETTINGS" > "$tmp" && mv "$tmp" "$SETTINGS"
  echo "install: removed the sankofa commands from $BIN, the hooks from $SETTINGS, and both services"
  exit 0
fi

ln -sf "$HERE/bin/sankofa" "$BIN/sankofa"
ln -sf "$HERE/bin/sankofa-watch" "$BIN/sankofa-watch"
echo "install: $BIN/sankofa and $BIN/sankofa-watch -> $HERE/bin"

if jq -e --arg a "$STOP_CMD" --arg b "$SUB_CMD" '
     ([.hooks.Stop[]?.hooks[]?.command] | index($a)) != null and
     ([.hooks.SubagentStop[]?.hooks[]?.command] | index($b)) != null' "$SETTINGS" >/dev/null; then
  echo "install: hooks already present in $SETTINGS"
else
  backup
  tmp=$(mktemp)
  jq --arg a "$STOP_CMD" --arg b "$SUB_CMD" '
    def add(ev; c): if ([.hooks[ev][]?.hooks[]?.command] | index(c)) != null then . else
        .hooks[ev] = ((.hooks[ev] // []) + [{hooks:[{type:"command",command:c}]}]) end;
    add("Stop"; $a) | add("SubagentStop"; $b)' "$SETTINGS" > "$tmp" && mv "$tmp" "$SETTINGS"
  echo "install: added Stop and SubagentStop hooks to $SETTINGS (backup kept beside it)"
fi

DAEMON=$HERE/bin/out/sankofad
if [ ! -x "$DAEMON" ] && [ "${SANKOFA_NO_BUILD:-0}" != 1 ] && command -v dotnet >/dev/null; then
  echo "install: building sankofad (dotnet publish, Native AOT)"
  make -C "$HERE" build >/dev/null
fi

mkdir -p "$UNIT_DIR"
install_unit() { sed "s|@BIN@|$BIN|g" "$HERE/systemd/$1" > "$UNIT_DIR/$1.tmp" && mv "$UNIT_DIR/$1.tmp" "$UNIT_DIR/$1"; }
if [ -x "$DAEMON" ]; then
  ln -sf "$DAEMON" "$BIN/sankofad"
  install_unit sankofad.service
  echo "install: $BIN/sankofad -> $DAEMON"
else
  echo "install: sankofad not built (no .NET SDK?); the CLI will use the local ledger only" >&2
fi
install_unit sankofa-watch.service
sysd daemon-reload
for u in $UNITS; do
  [ -f "$UNIT_DIR/$u" ] || continue
  sysd enable "$u"; sysd restart "$u"
done
echo "install: user services installed in $UNIT_DIR"
echo "install: run 'sankofa init' and 'sankofa watch add' in each repo that should keep a ledger"
