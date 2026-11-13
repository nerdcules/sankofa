#!/bin/sh
# Daemon down: edit allowed within the deadline, one flag event written, seen on reconnect.
. "$(dirname "$0")/lib.sh"
$S init >/dev/null
start_daemon; stop_daemon   # leaves no socket: unreachable
t0=$(now_ns)
gate claude-code "$(edit_json A "$PWD/src/a.c")"
ms=$(( ($(now_ns) - t0) / 1000000 ))
assert_eq "$RC$OUT" 0 "edit allowed"
[ "$ms" -lt 100 ] || fail "unreachable daemon took ${ms} ms"
gate claude-code "$(edit_json A "$PWD/src/a.c")"
assert_eq "$(jq -r 'select(.type=="flag") | .text' .sankofa/ledger.jsonl)" \
  "edit of src/a.c by A [main] not leased: sankofad unreachable" "exactly one flag for repeated edits"

# a socket that accepts but never answers: allowed within the 50 ms deadline, flagged
python3 -c "import socket,sys,time; s=socket.socket(socket.AF_UNIX); s.bind(sys.argv[1]); s.listen(5); time.sleep(30)" "$TD/hang.sock" & PIDS="$PIDS $!"
i=0; until [ -S "$TD/hang.sock" ] || [ $i -ge 100 ]; do sleep 0.02; i=$((i+1)); done
t0=$(now_ns)
SANKOFA_SOCKET="$TD/hang.sock" gate claude-code "$(edit_json A "$PWD/src/b.c")"
ms=$(( ($(now_ns) - t0) / 1000000 ))
assert_eq "$RC$OUT" 0 "hung daemon: edit allowed"
[ "$ms" -lt 150 ] || fail "hung daemon took ${ms} ms"
assert_contains "$(tail -n1 .sankofa/ledger.jsonl)" "src/b.c by A [main] not leased" "hung daemon flagged"

start_daemon
assert_eq "$($S events | jq -r 'select(.type=="flag") | .text' | grep -c 'not leased')" 2 "flags reach the daemon on reconnect"
echo ok gate down
