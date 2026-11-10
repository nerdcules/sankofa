#!/bin/sh
# Targets: startup under 20 ms and ask under 5 ms (issue #3); the per-edit gate under 5 ms (G4).
. "$(dirname "$0")/../0.1/lib.sh"
[ -x "$SANKOFAD" ] || fail "build sankofad first (make build)"
b=$("$SANKOFAD" bench)
echo "bench: $b"
ok=$(printf '%s' "$b" | jq '.startup_ms.p50 < 20 and .ask_socket_ms.p99 < 5 and .ask_process_ms.p50 < 5 and .gate_process_ms.p50 < 5')
assert_eq "$ok" true "startup p50 < 20 ms, ask p99 < 5 ms on a socket, ask and edit gate p50 < 5 ms as a process"
echo ok perf
