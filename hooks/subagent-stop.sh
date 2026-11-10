#!/bin/sh
# Claude Code SubagentStop hook. Records the subagent's final message; never blocks.
exec "$(dirname "$0")/../bin/sankofa" hook subagent-stop
