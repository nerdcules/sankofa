#!/bin/sh
# Claude Code Stop hook. Pipe the hook JSON in; exits 2 to keep the session going when a note is owed.
exec "$(dirname "$0")/../bin/sankofa" hook stop
