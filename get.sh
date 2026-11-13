#!/bin/sh
# Gets the latest sankofa release for this machine (Linux or macOS) and installs it:
#   curl -fsSL https://raw.githubusercontent.com/nerdcules/sankofa/main/get.sh | sh
# The tarball is unpacked under ~/.local/share/sankofa/<version> ("current" points at it) and
# its install.sh links the commands into ~/.local/bin, wires the Claude Code hooks and the user
# services (systemd on Linux, launchd on macOS), and registers the MCP server. Run it again to
# upgrade. Arguments go to install.sh (--hub). Env: SANKOFA_URL (another tarball; file:// works),
# SANKOFA_HOME (another folder). On Windows: download sankofa-windows-<arch>.zip from the
# releases page, unzip it, add the folder to PATH, run `sankofa setup`.
set -eu
case $(uname -s) in
  Linux) os=linux ;;
  Darwin) os=macos ;;
  *) echo "get: on Windows, download sankofa-windows-x64.zip (or -arm64) from https://github.com/nerdcules/sankofa/releases, unzip it, add the folder to PATH and run: sankofa setup" >&2; exit 1 ;;
esac
case $(uname -m) in x86_64) arch=x64 ;; aarch64|arm64) arch=arm64 ;; *) echo "get: no build for $(uname -m)" >&2; exit 1 ;; esac
url=${SANKOFA_URL:-https://github.com/nerdcules/sankofa/releases/latest/download/sankofa-$os-$arch.tar.gz}
home=${SANKOFA_HOME:-${XDG_DATA_HOME:-$HOME/.local/share}/sankofa}
command -v curl >/dev/null || { echo "get: curl is required" >&2; exit 1; }
command -v git >/dev/null || echo "get: sankofa needs git; install it with your package manager" >&2
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
echo "get: $url"
curl -fsSL "$url" -o "$tmp/sankofa.tar.gz"
tar -xzf "$tmp/sankofa.tar.gz" -C "$tmp"
dir=$(find "$tmp" -mindepth 1 -maxdepth 1 -type d -name 'sankofa-*' | head -n 1)
[ -n "$dir" ] || { echo "get: the tarball has no sankofa-<version> folder" >&2; exit 1; }
name=$(basename "$dir")
case $name in sankofa-*) ;; *) echo "get: unexpected folder $name" >&2; exit 1 ;; esac
mkdir -p "$home"
if [ -d "$home/$name" ]; then rm -r "$home/$name"; fi
mv "$dir" "$home/$name"
ln -sfn "$name" "$home/current"
echo "get: unpacked to $home/$name"
sh "$home/current/install.sh" "$@"
