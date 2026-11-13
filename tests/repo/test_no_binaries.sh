#!/bin/sh
# No compiled output or other binary files in git: every tracked file is text (or a symlink)
# and under 256 KB. Build outputs live in ignored directories (bin/out, */bin, */obj). The one
# exception is docs/images: the pictures the README shows on GitHub, PNG or SVG, under 256 KB each.
set -eu
cd "$(dirname "$0")/../.."
bad=$(git ls-files -s | while read -r mode _ _ path; do
  [ "$mode" = 120000 ] && continue                        # symlinks (CLAUDE.md -> AGENTS.md)
  [ -f "$path" ] || continue                               # deleted in the work tree
  if [ "$(wc -c < "$path")" -gt 262144 ]; then echo "too large: $path"; continue; fi
  case $path in docs/images/*.png|docs/images/*.svg) continue ;; esac
  case $(file -b --mime-encoding "$path") in binary) echo "binary: $path" ;; esac
done)
# anything else under docs/images is a mistake
for f in docs/images/*; do
  case $f in *.png|*.svg|'docs/images/*') ;; *) bad="$bad
not a picture: $f" ;; esac
done
[ -z "$bad" ] || { printf 'FAIL: %s\n' "$bad" >&2; exit 1; }
echo ok no binaries
