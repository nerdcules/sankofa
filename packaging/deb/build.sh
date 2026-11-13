#!/bin/sh
# Builds sankofa_<version>_<arch>.deb from a dist tree (make dist) with nothing but ar and tar,
# so it builds on any Linux; dpkg installs it. Same layout as the Arch package: commands in
# /usr/bin, the window and its libraries in /usr/lib/sankofa, units in /usr/lib/systemd/user,
# hooks and install.sh (for `sankofa setup`) in /usr/share/sankofa.
# usage: build.sh <dist-tree> <version> <x64|arm64> <out-dir>
set -eu
src=$1; ver=$2; arch=$3; out=$4
case $arch in x64) deb_arch=amd64 ;; arm64) deb_arch=arm64 ;; *) echo "deb: unknown arch $arch" >&2; exit 1 ;; esac
w=$(mktemp -d)
trap 'rm -rf "$w"' EXIT
r=$w/root
install -Dm755 "$src/bin/sankofa" "$r/usr/bin/sankofa"
install -Dm755 "$src/bin/sankofa-watch" "$r/usr/bin/sankofa-watch"
for b in sankofad sankofa-mcp sankofa-hub sankofa-tui; do install -Dm755 "$src/bin/out/$b" "$r/usr/bin/$b"; done
if [ -x "$src/bin/out/sankofa-gui" ]; then
  install -Dm755 "$src/bin/out/sankofa-gui" "$r/usr/lib/sankofa/sankofa-gui"
  install -Dm755 "$src/bin/out/libSkiaSharp.so" "$src/bin/out/libHarfBuzzSharp.so" -t "$r/usr/lib/sankofa"
  ln -s ../lib/sankofa/sankofa-gui "$r/usr/bin/sankofa-gui"
  sed 's|@BIN@|/usr/bin|g' "$src/packaging/linux/sankofa-gui.desktop" \
    | install -Dm644 /dev/stdin "$r/usr/share/applications/sankofa-gui.desktop"
fi
install -Dm755 "$src/hooks/git-pre-commit" "$r/usr/share/sankofa/hooks/git-pre-commit"
for f in "$src"/hooks/claude-code/*.sh "$src"/hooks/codex/pre-tool.sh; do
  install -Dm755 "$f" "$r/usr/share/sankofa/hooks/$(basename "$(dirname "$f")")/$(basename "$f")"
done
install -Dm755 "$src/install.sh" "$r/usr/share/sankofa/install.sh"
for u in sankofad.service sankofa-watch.service; do
  sed 's|@BIN@|/usr/bin|g' "$src/systemd/$u" | install -Dm644 /dev/stdin "$r/usr/lib/systemd/user/$u"
done
sed 's|@BIN@|/usr/bin|g; s|@LISTEN@|http://127.0.0.1:8787/|g' "$src/systemd/sankofa-hub.service" \
  | install -Dm644 /dev/stdin "$r/usr/lib/systemd/user/sankofa-hub.service"
install -Dm644 "$src/LICENSE" "$r/usr/share/doc/sankofa/copyright"
install -Dm644 "$src/README.md" "$r/usr/share/doc/sankofa/README.md"
install -Dm644 "$src/AGENTS.md" "$r/usr/share/doc/sankofa/AGENTS.md"
install -Dm644 "$src/hooks/codex/README.md" "$r/usr/share/doc/sankofa/codex.md"

mkdir -p "$w/ctl"
cat > "$w/ctl/control" <<CTL
Package: sankofa
Version: $ver
Architecture: $deb_arch
Maintainer: nerdcules <nerdcules@users.noreply.github.com>
Installed-Size: $(du -sk "$r" | cut -f1)
Depends: jq, git, libsqlite3-0, inotify-tools, libnotify-bin
Recommends: fontconfig, libx11-6
Section: devel
Priority: optional
Homepage: https://github.com/nerdcules/sankofa
Description: ledger and gates for many AI coding sessions on one codebase
 A small ledger that lets many AI coding sessions work on one codebase
 without stepping on each other: notes, todos, leases on files, handoffs
 to a teammate. After installing, run: sankofa setup
CTL
(cd "$r" && find . -type f | sed 's|^\./||' | LC_ALL=C sort | xargs md5sum) > "$w/ctl/md5sums"
printf '2.0\n' > "$w/debian-binary"
tar -C "$w/ctl" --owner=0 --group=0 -czf "$w/control.tar.gz" ./control ./md5sums
tar -C "$r" --owner=0 --group=0 -czf "$w/data.tar.gz" .
mkdir -p "$out"
deb=$(cd "$out" && pwd)/sankofa_${ver}_${deb_arch}.deb
rm -f "$deb"
(cd "$w" && ar rc "$deb" debian-binary control.tar.gz data.tar.gz)
echo "$deb"
