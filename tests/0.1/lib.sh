# shared test helpers; sourced by each test
set -eu
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
S=$ROOT/bin/sankofa
T=$(mktemp -d)
TD=$(mktemp -d)   # daemon socket, db and log: outside $T, which some tests make a git repo
# Background processes a test starts go in PID (one) or PIDS; they are stopped on exit.
PID=""; PIDS=""; DPID=""
on_exit() {
  for p in $PID $PIDS $DPID; do kill "$p" 2>/dev/null || :; done
  for p in $PID $PIDS $DPID; do wait "$p" 2>/dev/null || :; done
  rm -rf "$T" "$TD"
}
trap on_exit EXIT
export SANKOFA_SOCKET="$TD/no-daemon.sock"  # never reach a real sankofad from a test
export SANKOFA_DB="$TD/sankofad.db"
SANKOFAD=${SANKOFA_DAEMON_BIN:-$ROOT/bin/out/sankofad}
start_daemon() { # start a private sankofad on $T/d.sock and wait until it answers
  export SANKOFA_SOCKET="$TD/d.sock"
  "$SANKOFAD" serve --socket "$SANKOFA_SOCKET" --db "$SANKOFA_DB" 2>>"$TD/sankofad.log" & DPID=$!
  i=0; until printf '{"verb":"ping"}' | "$SANKOFAD" send --socket "$SANKOFA_SOCKET" >/dev/null 2>&1; do
    i=$((i + 1)); [ $i -lt 200 ] || { cat "$TD/sankofad.log" >&2; echo "FAIL: sankofad did not start" >&2; exit 1; }
    sleep 0.01
  done
}
stop_daemon() { [ -n "$DPID" ] && { kill "$DPID"; wait "$DPID" 2>/dev/null || :; DPID=""; }; }
# SANKOFA_TEST_DAEMON=1 runs any test with every write going through sankofad
[ "${SANKOFA_TEST_DAEMON:-0}" = 1 ] && start_daemon
cd "$T"
unset CLAUDE_CODE_SESSION_ID CLAUDE_SESSION_ID SANKOFA_SESSION SANKOFA_AGENT SANKOFA_REQUIRE_NOTE || true
now_ns() { python3 -c 'import time; print(time.time_ns())'; }   # date +%N is GNU only
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
assert_eq() { [ "$1" = "$2" ] || fail "$3: expected [$2] got [$1]"; }
assert_contains() { printf '%s' "$1" | grep -qF -- "$2" || fail "$3: [$1] lacks [$2]"; }
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
tx_tool() { # transcript tool_name [file_path]: append an assistant entry that calls the tool, timestamped now
  sleep 0.01
  jq -nc --arg ts "$(ms_now)" --arg n "$2" --arg f "${3:-}" \
    '{type:"assistant",timestamp:$ts,message:{content:[{type:"text",text:"x"},
      {type:"tool_use",name:$n,input:(if $f != "" then {file_path:$f} else {} end)}]}}' >> "$1"
  sleep 0.01
}
