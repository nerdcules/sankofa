# helpers for the 0.4 gate tests; builds on the 0.1 helpers
. "$(dirname "$0")/../0.1/lib.sh"
[ -x "$SANKOFAD" ] || fail "build sankofad first (make build)"
edit_json() { # session file [agent_id agent_type] [tool]
  jq -nc --arg s "$1" --arg f "$2" --arg id "${3:-}" --arg ty "${4:-}" --arg tool "${5:-Edit}" --arg cwd "$PWD" '
    {session_id:$s, cwd:$cwd, hook_event_name:"PreToolUse", tool_name:$tool,
     tool_input:{file_path:$f, old_string:"a", new_string:"b"}}
    + (if $id != "" then {agent_id:$id, agent_type:$ty} else {} end)'
}
gate() { # gate <client> <json>: sets OUT (stdout), RC
  set +e; OUT=$(printf '%s' "$2" | "$SANKOFAD" gate "$1" 2>"$T/gate.err"); RC=$?; set -e
}
denied() { [ "$(printf '%s' "$OUT" | jq -r '.hookSpecificOutput.permissionDecision // empty' 2>/dev/null)" = deny ]; }
reason() { printf '%s' "$OUT" | jq -r '.hookSpecificOutput.permissionDecisionReason'; }
