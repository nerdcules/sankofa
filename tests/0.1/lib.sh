# shared test helpers; sourced by each test
set -eu
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
S=$ROOT/bin/sankofa
T=$(mktemp -d); trap 'rm -rf "$T"' EXIT
cd "$T"
unset CLAUDE_CODE_SESSION_ID CLAUDE_SESSION_ID SANKOFA_SESSION SANKOFA_AGENT SANKOFA_REQUIRE_NOTE || true
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
assert_eq() { [ "$1" = "$2" ] || fail "$3: expected [$2] got [$1]"; }
assert_contains() { printf '%s' "$1" | grep -q -- "$2" || fail "$3: [$1] lacks [$2]"; }
lines() { grep -c . "$1" 2>/dev/null || echo 0; }
stop_json() { # session [stop_hook_active] [message]
  jq -nc --arg s "$1" --argjson a "${2:-false}" --arg m "${3:-All done.}" \
    '{session_id:$s, stop_hook_active:$a, last_assistant_message:$m, hook_event_name:"Stop"}'
}
run_hook() { # run_hook <kind> <json>  -> sets RC (exit code) and ERR (stderr)
  set +e; ERR=$(printf '%s' "$2" | "$S" hook "$1" 2>&1 >/dev/null); RC=$?; set -e
}
stop_json_t() { # session transcript_path [stop_hook_active]
  jq -nc --arg s "$1" --arg t "$2" --argjson a "${3:-false}" \
    '{session_id:$s, transcript_path:$t, stop_hook_active:$a, last_assistant_message:"ok", hook_event_name:"Stop"}'
}
ms_now() { python3 -c 'import datetime;print(datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3]+"Z")'; }
tx_tool() { # transcript tool_name: append an assistant entry that calls the tool, timestamped now
  sleep 0.01
  jq -nc --arg ts "$(ms_now)" --arg n "$2" \
    '{type:"assistant",timestamp:$ts,message:{content:[{type:"text",text:"x"},{type:"tool_use",name:$n,input:{}}]}}' >> "$1"
  sleep 0.01
}
