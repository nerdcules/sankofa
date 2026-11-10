#!/bin/sh
# Targets from issue #3: startup under 20 ms; ask round trip under 5 ms.
. "$(dirname "$0")/../0.1/lib.sh"
[ -x "$SANKOFAD" ] || fail "build sankofad first (make build)"
b=$("$SANKOFAD" bench)
echo "bench: $b"
ok=$(printf '%s' "$b" | jq '.startup_ms.p50 < 20 and .ask_socket_ms.p99 < 5 and .ask_process_ms.p50 < 5')
assert_eq "$ok" true "startup p50 < 20 ms, ask p99 < 5 ms on a socket, ask p50 < 5 ms as a process"
echo ok perf
