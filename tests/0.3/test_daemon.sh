#!/bin/sh
# Integration: sankofad on a temp socket, driven by the CLI.
. "$(dirname "$0")/../0.1/lib.sh"
[ -x "$SANKOFAD" ] || fail "build sankofad first (make build)"
$S init >/dev/null
assert_contains "$($S daemon 2>&1 || :)" "not answering" "no daemon yet"
start_daemon
assert_contains "$($S daemon)" "sankofad $(sed -n "s/^VERSION=//p" "$ROOT/bin/sankofa")" "daemon answers"

# ledger verbs go through the daemon and land in the mirror
SANKOFA_SESSION=s1 $S note "first note"
id=$(SANKOFA_SESSION=s1 $S todo "wire the gate")
assert_eq "$id" e2 "ids continue the ledger"
assert_eq "$(lines .sankofa/ledger.jsonl)" 2 "mirrored to ledger.jsonl"
assert_contains "$($S open)" "wire the gate" "open reads the mirror"

# leases: grant, contend (exit 2), ask, subagent is a separate holder
out=$(SANKOFA_SESSION=s1 $S claim 'src/**' 10m)
assert_contains "$out" "e3  held  src/**" "claim granted"
mkdir -p src/net
set +e; err=$(cd src/net && SANKOFA_SESSION=s2 $S claim socket.c 2>&1); rc=$?; set -e
assert_eq "$rc" 2 "overlapping claim exits 2"
assert_contains "$err" "denied: src/net/socket.c overlaps" "path is repo-relative from a subdir"
assert_contains "$err" "e3  src/**  held by s1 [main]" "names the holder"
set +e; SANKOFA_SESSION=s1 SANKOFA_AGENT=subagent:net $S claim src/net/x.c >/dev/null 2>&1; rc=$?; set -e
assert_eq "$rc" 2 "a subagent of the same session is another holder"
assert_contains "$($S ask src/net/socket.c)" "held  src/net/socket.c  e3 src/** by s1 [main]" "ask names the holder"
assert_contains "$($S ask docs/x.md)" "free  docs/x.md" "unleased path is free"
assert_contains "$(SANKOFA_SESSION=s1 $S claim 'src/**')" "renewed" "same holder renews"
assert_contains "$($S leases)" "held,dirty" "leases shows dirty while held"

# done clears dirty; release keeps it and ask says so
SANKOFA_SESSION=s1 $S done e3 "src finished"
assert_eq "$($S leases)" "no leases" "done lease is gone"
lid=$(SANKOFA_SESSION=s2 $S claim docs/x.md | cut -d' ' -f1)
SANKOFA_SESSION=s2 $S release "$lid"
assert_contains "$($S ask docs/x.md)" "dirty  $lid docs/x.md by s2 [main] ended released without done" "release keeps dirty"
w=$(SANKOFA_SESSION=s3 $S claim docs/x.md 2>&1 >/dev/null)
assert_contains "$w" "warning: $lid on docs/x.md" "claim warns about the half-finished edit"

# events: the raw stream, and a live follower that sees new writes
n=$($S events | grep -c .)
assert_eq "$n" "$(lines .sankofa/ledger.jsonl)" "one event per ledger line"
assert_eq "$($S events | jq -r .type | sort -u | tr '\n' ' ')" "claim contend done note release renew todo " "event types"
$S events --follow > "$T/follow.out" & PIDS="$PIDS $!"
sleep 0.3
SANKOFA_SESSION=s1 $S awaiting "log in to the registry" >/dev/null
i=0; until grep -q registry "$T/follow.out" 2>/dev/null || [ $i -ge 50 ]; do sleep 0.05; i=$((i+1)); done
assert_eq "$(jq -r .text "$T/follow.out")" "log in to the registry" "follower got only the live event"
assert_eq "$($S events --since 3 | head -n1 | jq -r .id)" e4 "since replays after a sequence number"

# daemon down: writes fall back to the file, claims are allowed and flagged, nothing is lost
stop_daemon
before=$(lines .sankofa/ledger.jsonl)
SANKOFA_SESSION=s4 $S note "written while sankofad was down"
fid=$(SANKOFA_SESSION=s4 $S todo "fallback todo")
err=$(SANKOFA_SESSION=s4 $S claim src/a.c 2>&1); assert_contains "$err" "not recorded" "claim allowed and flagged"
assert_contains "$($S ask src/a.c)" "unknown" "ask without daemon"
assert_eq "$(lines .sankofa/ledger.jsonl)" $((before + 3)) "fallback appended three lines"
start_daemon
$S note "daemon is back" >/dev/null
assert_eq "$($S events | jq -r 'select(.session=="s4") | .text' | tr '\n' '|')" \
  "written while sankofad was down|fallback todo|claim on src/a.c not recorded: sankofad unreachable|" "fallback lines imported in order"
assert_eq "$($S events | grep -c .)" "$(lines .sankofa/ledger.jsonl)" "daemon and file agree after import"
assert_eq "$(jq -r .id .sankofa/ledger.jsonl | sort | uniq -d)" "" "no duplicate ids"
$S done "$fid" "done through the daemon"

# a lease finished while the daemon is down is applied when it is back
lid=$(SANKOFA_SESSION=s5 $S claim src/late.c | cut -d' ' -f1)
stop_daemon
SANKOFA_SESSION=s5 $S done "$lid" "finished offline"
start_daemon
assert_eq "$($S ask src/late.c)" "free  src/late.c" "offline done finished the lease, not dirty"
assert_contains "$($S open)" "log in to the registry" "open still lists the awaiting item"

# someone truncates the file: the daemon puts it back and says so
total=$(lines .sankofa/ledger.jsonl)
head -n 3 .sankofa/ledger.jsonl > "$T/cut" && cat "$T/cut" > .sankofa/ledger.jsonl
$S note "after the truncation"
assert_eq "$(lines .sankofa/ledger.jsonl)" $((total + 2)) "restored, flagged, then appended"
assert_contains "$(tail -n 2 .sankofa/ledger.jsonl | head -n 1)" "restored the rest from sankofad" "restore is flagged"

# a client that strips XDG_RUNTIME_DIR (Codex does for MCP servers) still finds /run/user/<uid>
if [ -d "/run/user/$(id -u)" ]; then
  out=$(env -u SANKOFA_SOCKET -u XDG_RUNTIME_DIR "$S" daemon 2>&1 || :)
  assert_contains "$out" "/run/user/$(id -u)/sankofa/sankofad.sock" "socket path without XDG_RUNTIME_DIR"
fi

# a second daemon on the same socket refuses to start
set +e; "$SANKOFAD" serve --socket "$SANKOFA_SOCKET" --db "$TD/other.db" 2>"$T/second.err"; rc=$?; set -e
assert_eq "$rc" 1 "second daemon exits"
assert_contains "$(cat "$T/second.err")" "another sankofad is listening" "and says why"
echo ok daemon
