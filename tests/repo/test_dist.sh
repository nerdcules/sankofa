#!/bin/sh
# make dist: the tarball installs from where it is unpacked (install.sh inside it, and get.sh
# fetching it), the version shows, and on Linux the .deb has the package layout under /usr.
# Runs on Linux and macOS (the package name follows: linux, macos).
. "$(dirname "$0")/../0.1/lib.sh"
[ -x "$ROOT/bin/out/sankofad" ] || { echo "skip dist (make build first)"; exit 0; }
ARCH=$(uname -m | sed 's/x86_64/x64/; s/aarch64/arm64/')
case $(uname -s) in Darwin) PKG=macos ;; *) PKG=linux ;; esac
D=$T/dist
make -C "$ROOT" dist VERSION=9.9.9 DISTDIR="$D" >/dev/null 2>"$T/make.err" || { cat "$T/make.err" >&2; fail "make dist"; }
N=sankofa-9.9.9-$PKG-$ARCH

TGZ=$D/sankofa-$PKG-$ARCH.tar.gz
[ -f "$TGZ" ] || fail "no tarball"
LIST=$(tar -tzf "$TGZ")
for f in install.sh get.sh bin/sankofa bin/sankofa-watch bin/out/sankofa bin/out/sankofad bin/out/sankofa-mcp bin/out/sankofa-hub \
         bin/out/sankofa-tui bin/out/sankofa-gui hooks/claude-code/stop.sh hooks/git-pre-commit \
         systemd/sankofad.service AGENTS.md LICENSE README.md; do
  assert_contains "$LIST" "$N/$f" "tarball holds $f"
done
case $LIST in *"/daemon/"*|*"/tests/"*|*.pdb*) fail "tarball holds sources or symbols" ;; esac

# install from the unpacked tree, in a private HOME (XDG unset, or install.sh would reach the real ones)
export HOME=$T/home CLAUDE_SETTINGS=$T/home/.claude/settings.json SANKOFA_NO_SYSTEMD=1 SANKOFA_NO_BUILD=1 SANKOFA_NO_MCP=1 SANKOFA_NO_LAUNCHD=1
unset XDG_BIN_HOME XDG_CONFIG_HOME XDG_DATA_HOME XDG_STATE_HOME || true
mkdir -p "$T/opt" "$HOME"
tar -xzf "$TGZ" -C "$T/opt"
sh "$T/opt/$N/install.sh" >/dev/null
assert_eq "$(readlink -f "$HOME/.local/bin/sankofad")" "$T/opt/$N/bin/out/sankofad" "sankofad links into the unpacked tree"
case $("$HOME/.local/bin/sankofa" version) in "sankofa "*) ;; *) fail "the installed sankofa does not answer version" ;; esac
assert_eq "$(jq -r '.hooks.Stop[0].hooks[0].command' "$CLAUDE_SETTINGS")" "sankofa hook stop" "hooks wired"
[ "$PKG" != linux ] || assert_contains "$(cat "$HOME/.config/systemd/user/sankofad.service")" "ExecStart=$HOME/.local/bin/sankofad serve" "unit written"
sh "$T/opt/$N/install.sh" --uninstall >/dev/null
[ ! -e "$HOME/.local/bin/sankofad" ] || fail "uninstall left the daemon link"

# get.sh: fetches the tarball (file://), keeps it under SANKOFA_HOME/<name>, "current" points at it, installs
if command -v curl >/dev/null; then
  SANKOFA_URL="file://$TGZ" SANKOFA_HOME=$T/share sh "$ROOT/get.sh" >/dev/null
  assert_eq "$(readlink "$T/share/current")" "$N" "current points at the version"
  assert_eq "$(readlink -f "$HOME/.local/bin/sankofa")" "$T/share/$N/bin/sankofa" "sankofa links into SANKOFA_HOME"
  SANKOFA_URL="file://$TGZ" SANKOFA_HOME=$T/share sh "$ROOT/get.sh" >/dev/null   # again: an upgrade over itself
  case $("$HOME/.local/bin/sankofa" version) in "sankofa "*) ;; *) fail "not installed after a second get" ;; esac
  sh "$T/share/current/install.sh" --uninstall >/dev/null
fi

# --packaged: nothing linked, hooks wired, `sankofa setup` finds install.sh beside the binary
PATH="$T/opt/$N/bin:$PATH" sh "$T/opt/$N/install.sh" --packaged >/dev/null
[ ! -e "$HOME/.local/bin/sankofa" ] || fail "--packaged linked the CLI"
assert_eq "$(jq -r '.hooks.SessionEnd[0].hooks[0].command' "$CLAUDE_SETTINGS")" "sankofa hook session-end" "--packaged wired the hooks"
sh "$T/opt/$N/install.sh" --uninstall >/dev/null
"$T/opt/$N/bin/sankofa" setup >/dev/null   # runs the install.sh beside it
[ -L "$HOME/.local/bin/sankofa" ] || fail "sankofa setup did not run install.sh"
sh "$T/opt/$N/install.sh" --uninstall >/dev/null

[ "$PKG" = linux ] || { echo "ok dist ($PKG, no .deb)"; exit 0; }

# the .deb: an ar archive dpkg reads, with the package layout and the stamped version
case $ARCH in x64) DA=amd64 ;; *) DA=$ARCH ;; esac
DEB=$D/sankofa_9.9.9_$DA.deb
[ -f "$DEB" ] || fail "no .deb"
assert_eq "$(ar t "$DEB" | tr '\n' ' ')" "debian-binary control.tar.gz data.tar.gz " "deb members in order"
mkdir -p "$T/deb" && (cd "$T/deb" && ar x "$DEB")
CTL=$(tar -xzOf "$T/deb/control.tar.gz" ./control)
assert_contains "$CTL" "Version: 9.9.9" "deb version"
assert_contains "$CTL" "Architecture: $DA" "deb architecture"
DATA=$(tar -tzf "$T/deb/data.tar.gz")
for f in ./usr/bin/sankofa ./usr/bin/sankofad ./usr/bin/sankofa-gui ./usr/lib/sankofa/sankofa-gui ./usr/lib/sankofa/libSkiaSharp.so \
         ./usr/lib/systemd/user/sankofad.service ./usr/share/sankofa/install.sh ./usr/share/sankofa/hooks/git-pre-commit \
         ./usr/share/sankofa/hooks/claude-code/stop.sh ./usr/share/applications/sankofa-gui.desktop ./usr/share/doc/sankofa/copyright; do
  assert_contains "$DATA" "$f" "deb holds $f"
done
if command -v dpkg-deb >/dev/null; then dpkg-deb --info "$DEB" >/dev/null || fail "dpkg-deb rejects the package"; fi
echo "ok dist"
