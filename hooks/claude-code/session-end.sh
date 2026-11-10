#!/bin/sh
# Claude Code SessionEnd hook: finish every lease this session still holds.
exec "$(dirname "$0")/../../bin/sankofa" hook session-end
