#!/bin/sh
# Targets: startup under 20 ms and ask under 5 ms (issue #3); the per-edit gate under 5 ms (G4).
# Those are budgets for a developer's machine. A shared CI runner is two to three times slower
# (a GitHub runner measured the gate at 5.7 ms where this machine does 4), so under CI=true
# every budget is doubled: the test then catches a regression, not the runner.
. "$(dirname "$0")/../0.1/lib.sh"
[ -x "$SANKOFAD" ] || fail "build sankofad first (make build)"
b=$("$SANKOFAD" bench)
echo "bench: $b"
k=1; [ "${CI:-}" = true ] && k=2
ok=$(printf '%s' "$b" | jq --argjson k "$k" '.startup_ms.p50 < 20*$k and .ask_socket_ms.p99 < 5*$k and .ask_process_ms.p50 < 5*$k and .gate_process_ms.p50 < 5*$k')
assert_eq "$ok" true "startup p50 < 20 ms, ask p99 < 5 ms on a socket, ask and edit gate p50 < 5 ms as a process (x$k under CI)"
echo ok perf
