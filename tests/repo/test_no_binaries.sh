#!/bin/sh
# No compiled output or other binary files in git: every tracked file is text (or a symlink)
# and under 256 KB. Build outputs live in ignored directories (bin/out, */bin, */obj).
set -eu
cd "$(dirname "$0")/../.."
bad=$(git ls-files -s | while read -r mode _ _ path; do
  [ "$mode" = 120000 ] && continue                        # symlinks (CLAUDE.md -> AGENTS.md)
  [ -f "$path" ] || continue                               # deleted in the work tree
  if [ "$(wc -c < "$path")" -gt 262144 ]; then echo "too large: $path"; continue; fi
  case $(file -b --mime-encoding "$path") in binary) echo "binary: $path" ;; esac
done)
[ -z "$bad" ] || { printf 'FAIL: %s\n' "$bad" >&2; exit 1; }
echo ok no binaries
