#!/bin/sh
# Claude Code PreToolUse hook for Edit|Write|MultiEdit|NotebookEdit: lease the file, or deny
# naming whoever holds it. install.sh points settings.json straight at `sankofad gate claude-code`;
# this script is the same thing for hand-written configs.
exec sankofad gate claude-code
