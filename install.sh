#!/bin/sh
# Installs the sankofa CLI into ~/.local/bin and wires the Stop / SubagentStop hooks
# into ~/.claude/settings.json. Idempotent. --uninstall reverses both.
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
BIN=${XDG_BIN_HOME:-$HOME/.local/bin}
SETTINGS=${CLAUDE_SETTINGS:-$HOME/.claude/settings.json}
STOP_CMD="sankofa hook stop"
SUB_CMD="sankofa hook subagent-stop"

command -v jq >/dev/null || { echo "install: jq is required" >&2; exit 1; }
mkdir -p "$BIN" "$(dirname "$SETTINGS")"
[ -f "$SETTINGS" ] || echo '{}' > "$SETTINGS"

backup() { cp "$SETTINGS" "$SETTINGS.bak-$(date -u +%Y%m%dT%H%M%SZ)"; }

if [ "${1:-}" = --uninstall ]; then
  rm -f "$BIN/sankofa"
  backup
  tmp=$(mktemp)
  jq --arg a "$STOP_CMD" --arg b "$SUB_CMD" '
    def strip(ev; c): if .hooks[ev] then .hooks[ev] |= map(.hooks |= map(select(.command != c)) | select(.hooks|length>0)) else . end
      | if .hooks[ev] == [] then del(.hooks[ev]) else . end;
    strip("Stop"; $a) | strip("SubagentStop"; $b)
    | if .hooks == {} then del(.hooks) else . end' "$SETTINGS" > "$tmp" && mv "$tmp" "$SETTINGS"
  echo "install: removed $BIN/sankofa and the hooks from $SETTINGS"
  exit 0
fi

ln -sf "$HERE/bin/sankofa" "$BIN/sankofa"
echo "install: $BIN/sankofa -> $HERE/bin/sankofa"

if jq -e --arg a "$STOP_CMD" --arg b "$SUB_CMD" '
     ([.hooks.Stop[]?.hooks[]?.command] | index($a)) != null and
     ([.hooks.SubagentStop[]?.hooks[]?.command] | index($b)) != null' "$SETTINGS" >/dev/null; then
  echo "install: hooks already present in $SETTINGS"
  exit 0
fi

backup
tmp=$(mktemp)
jq --arg a "$STOP_CMD" --arg b "$SUB_CMD" '
  def add(ev; c): if ([.hooks[ev][]?.hooks[]?.command] | index(c)) != null then . else
      .hooks[ev] = ((.hooks[ev] // []) + [{hooks:[{type:"command",command:c}]}]) end;
  add("Stop"; $a) | add("SubagentStop"; $b)' "$SETTINGS" > "$tmp" && mv "$tmp" "$SETTINGS"
echo "install: added Stop and SubagentStop hooks to $SETTINGS (backup kept beside it)"
echo "install: run 'sankofa init' in each repo that should keep a ledger"
