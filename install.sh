#!/bin/sh
# Installs sankofa into ~/.local/bin (the CLI, the watcher, and the daemon, MCP server, hub,
# terminal dashboard and window when built, plus a launcher entry for the window), wires the
# Stop / SubagentStop / SessionEnd hooks and the edit gate into ~/.claude/settings.json, and
# installs the sankofad and sankofa-watch systemd user services. Builds the binaries first if
# sankofad is missing and the .NET SDK is present; without it the CLI works on the local
# ledger alone. Registers `sankofad mcp` with Claude Code (claude mcp add) and Codex
# (~/.codex/config.toml) when they are installed.
# Idempotent. --uninstall reverses it all. --packaged (what `sankofa setup` runs after a .deb
# or Arch install): the commands are already in /usr/bin and the units in /usr/lib/systemd/user,
# so only the hooks, the services and the client registrations are set up.
# Env: SANKOFA_NO_MCP=1 skips the client registrations; CODEX_HOME locates Codex's config.
# Env: XDG_BIN_HOME, CLAUDE_SETTINGS, XDG_CONFIG_HOME; SANKOFA_NO_SYSTEMD=1 writes the
# unit but does not call systemctl (tests, containers).
# --hub also installs and starts sankofa-hub.service on this box (the always-on machine
# every device shares a ledger through); SANKOFA_HUB_LISTEN sets its address, like
# http://100.64.0.7:8787/ (a Tailscale address). Default http://127.0.0.1:8787/.
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
BIN=${XDG_BIN_HOME:-$HOME/.local/bin}
SETTINGS=${CLAUDE_SETTINGS:-$HOME/.claude/settings.json}
UNIT_DIR=${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user
APPS=${XDG_DATA_HOME:-$HOME/.local/share}/applications
UNITS="sankofad.service sankofa-watch.service"
CODEX_CONFIG=${CODEX_HOME:-$HOME/.codex}/config.toml
CODEX_MARK="# sankofa MCP server (install.sh)"

mcp_register() {
  [ "${SANKOFA_NO_MCP:-0}" = 1 ] && return 0
  if command -v claude >/dev/null; then
    if claude mcp get sankofa >/dev/null 2>&1; then echo "install: Claude Code already has the sankofa MCP server"
    else claude mcp add --scope user sankofa -- sankofad mcp >/dev/null && echo "install: registered sankofad mcp with Claude Code (user scope)"; fi
  fi
  if [ -f "$CODEX_CONFIG" ]; then
    if grep -qF "$CODEX_MARK" "$CODEX_CONFIG" || grep -q '^\[mcp_servers\.sankofa\]' "$CODEX_CONFIG"; then
      echo "install: Codex already has the sankofa MCP server"
    else
      cp "$CODEX_CONFIG" "$CODEX_CONFIG.bak-$(date -u +%Y%m%dT%H%M%SZ)"
      printf '\n%s\n[mcp_servers.sankofa]\ncommand = "sankofad"\nargs = ["mcp"]\n' "$CODEX_MARK" >> "$CODEX_CONFIG"
      echo "install: registered sankofad mcp with Codex in $CODEX_CONFIG (backup kept beside it)"
    fi
  fi
}

mcp_unregister() {
  [ "${SANKOFA_NO_MCP:-0}" = 1 ] && return 0
  command -v claude >/dev/null && claude mcp remove --scope user sankofa >/dev/null 2>&1 || :
  if [ -f "$CODEX_CONFIG" ] && grep -qF "$CODEX_MARK" "$CODEX_CONFIG"; then
    cp "$CODEX_CONFIG" "$CODEX_CONFIG.bak-$(date -u +%Y%m%dT%H%M%SZ)"
    tmp=$(mktemp)
    awk -v m="$CODEX_MARK" '
      $0 == m { skip = 1; next }
      skip && /^\[/ && $0 != "[mcp_servers.sankofa]" { skip = 0 }
      skip { next }
      { print }' "$CODEX_CONFIG" | sed -e :a -e '/^\n*$/{$d;N;ba' -e '}' > "$tmp" && mv "$tmp" "$CODEX_CONFIG"
  fi
}
# event | matcher | command  (the Claude Code adapter; see hooks/claude-code/)
HOOKS='[
  {"ev":"Stop","cmd":"sankofa hook stop"},
  {"ev":"SubagentStop","cmd":"sankofa hook subagent-stop"},
  {"ev":"SessionEnd","cmd":"sankofa hook session-end"},
  {"ev":"PreToolUse","matcher":"Edit|Write|MultiEdit|NotebookEdit","cmd":"sankofad gate claude-code","timeout":5}
]'

PACKAGED=0; [ "${1:-}" = --packaged ] && { PACKAGED=1; shift; }
command -v jq >/dev/null || { echo "install: jq is required" >&2; exit 1; }
mkdir -p "$BIN" "$(dirname "$SETTINGS")"
[ -f "$SETTINGS" ] || echo '{}' > "$SETTINGS"

# settings can hold keys: the copy is private even where the original is not
backup() { b=$SETTINGS.bak-$(date -u +%Y%m%dT%H%M%SZ); cp "$SETTINGS" "$b" && chmod 600 "$b"; }
sysd() { [ "${SANKOFA_NO_SYSTEMD:-0}" = 1 ] && return 0; command -v systemctl >/dev/null || return 0
         systemctl --user "$@" || echo "install: systemctl --user $* failed (no user session?)" >&2; }

if [ "${1:-}" = --uninstall ]; then
  for u in $UNITS; do
    [ -f "$UNIT_DIR/$u" ] && { sysd disable --now "$u"; rm -f "$UNIT_DIR/$u"; }
  done
  mcp_unregister
  sysd daemon-reload
  [ -f "$UNIT_DIR/sankofa-hub.service" ] && { sysd disable --now sankofa-hub.service; rm -f "$UNIT_DIR/sankofa-hub.service"; }
  rm -f "$BIN/sankofa" "$BIN/sankofa-watch" "$BIN/sankofad" "$BIN/sankofa-mcp" "$BIN/sankofa-hub" "$BIN/sankofa-tui" "$BIN/sankofa-gui"
  rm -f "$APPS/sankofa-gui.desktop"
  backup
  tmp=$(mktemp)
  jq --argjson hs "$HOOKS" '
    def strip(ev; c): if .hooks[ev] then .hooks[ev] |= map(.hooks |= map(select(.command != c)) | select(.hooks|length>0)) else . end
      | if .hooks[ev] == [] then del(.hooks[ev]) else . end;
    reduce $hs[] as $h (.; strip($h.ev; $h.cmd))
    | if .hooks == {} then del(.hooks) else . end' "$SETTINGS" > "$tmp" && mv "$tmp" "$SETTINGS"
  echo "install: removed the sankofa commands from $BIN, the hooks from $SETTINGS, and both services"
  exit 0
fi

if [ $PACKAGED = 1 ]; then
  command -v sankofa >/dev/null || { echo "install: --packaged, but sankofa is not on PATH" >&2; exit 1; }
  echo "install: packaged: $(command -v sankofa) and $(command -v sankofad 2>/dev/null || echo 'no sankofad')"
else
  ln -sf "$HERE/bin/sankofa" "$BIN/sankofa"
  ln -sf "$HERE/bin/sankofa-watch" "$BIN/sankofa-watch"
  echo "install: $BIN/sankofa and $BIN/sankofa-watch -> $HERE/bin"
fi

if jq -e --argjson hs "$HOOKS" '. as $s | all($hs[]; . as $h | ([$s.hooks[$h.ev][]?.hooks[]?.command] | index($h.cmd)) != null)' \
     "$SETTINGS" >/dev/null; then
  echo "install: hooks already present in $SETTINGS"
else
  backup
  tmp=$(mktemp)
  jq --argjson hs "$HOOKS" '
    def add($h): if ([.hooks[$h.ev][]?.hooks[]?.command] | index($h.cmd)) != null then . else
        .hooks[$h.ev] = ((.hooks[$h.ev] // []) + [
          ({hooks:[{type:"command",command:$h.cmd} + (if $h.timeout then {timeout:$h.timeout} else {} end)]}
           + (if $h.matcher then {matcher:$h.matcher} else {} end))]) end;
    reduce $hs[] as $h (.; add($h))' "$SETTINGS" > "$tmp" && mv "$tmp" "$SETTINGS"
  echo "install: added the Stop, SubagentStop, SessionEnd and PreToolUse (edit gate) hooks to $SETTINGS (backup kept beside it)"
fi

if [ $PACKAGED = 1 ]; then
  sysd daemon-reload
  for u in $UNITS; do sysd enable "$u"; sysd restart "$u"; done
  echo "install: user services enabled (units from the package)"
  mcp_register
  echo "install: in each repo that should keep a ledger: sankofa init; sankofa watch add; sankofa gate install"
  exit 0
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
  [ -x "$HERE/bin/out/sankofa-mcp" ] && ln -sf "$HERE/bin/out/sankofa-mcp" "$BIN/sankofa-mcp"
  [ -x "$HERE/bin/out/sankofa-hub" ] && ln -sf "$HERE/bin/out/sankofa-hub" "$BIN/sankofa-hub"
  [ -x "$HERE/bin/out/sankofa-tui" ] && ln -sf "$HERE/bin/out/sankofa-tui" "$BIN/sankofa-tui"
  if [ -x "$HERE/bin/out/sankofa-gui" ]; then
    ln -sf "$HERE/bin/out/sankofa-gui" "$BIN/sankofa-gui"
    # a launcher entry, so the app launcher (Super+Space on Omarchy) finds the window
    mkdir -p "$APPS"
    sed "s|@BIN@|$BIN|g" "$HERE/packaging/linux/sankofa-gui.desktop" > "$APPS/sankofa-gui.desktop"
  fi
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
if [ "${1:-}" = --hub ]; then
  listen=${SANKOFA_HUB_LISTEN:-http://127.0.0.1:8787/}
  sed "s|@BIN@|$BIN|g; s|@LISTEN@|$listen|g" "$HERE/systemd/sankofa-hub.service" > "$UNIT_DIR/sankofa-hub.service"
  sysd daemon-reload; sysd enable sankofa-hub.service; sysd restart sankofa-hub.service
  echo "install: sankofa-hub listening on $listen. Add a token per device: sankofad hub token add <device>"
  echo "install: on a server, keep user services running after logout: loginctl enable-linger $(id -un)"
fi
mcp_register
echo "install: in each repo that should keep a ledger: sankofa init; sankofa watch add; sankofa gate install"
