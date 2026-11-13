#!/bin/sh
# 0.8.1: in a repo shared through a hub, only the device that held a lease settles it (only its
# worktrees can show the edit), and nothing is settled while the hub is out of reach
export SANKOFA_SETTLE=3600
. "$(dirname "$0")/../0.7/lib.sh"
setup_two
g() { git -c user.name=t -c user.email=t@t "$@"; }
for d in desk lap; do (cd "$T/$d/repo" && printf '.sankofa/\n' > .gitignore && g init -q -b main && g add -A && g commit -qm init); done
state() { dev "$1" "$S" leases | awk -v id="$2" '$1 == id { print $2 }'; }
cd "$T/desk/repo"; ld=$(SANKOFA_SESSION=s-desk dev a "$S" claim src/x.c 1 | grep -o 'e[0-9][0-9]*' | head -1)
cd "$T/lap/repo";  ll=$(SANKOFA_SESSION=s-lap dev b "$S" claim src/y.c 1 | grep -o 'e[0-9][0-9]*' | head -1)
echo 'int y;' > "$T/lap/repo/x-scratch.c"   # lap's tree is not desk's business
sleep 1.3
cd "$T/desk/repo"
assert_eq "$(state a "$ll")" "expired,dirty" "desk sees lap's ended lease"

# the hub is down: desk settles nothing and queues nothing
stop_hub
assert_eq "$(dev a "$S" leases settle 2>&1 || :)" "nothing to settle" "no settling while the hub is unreachable"
assert_eq "$(jq -r .id "$A" | grep -c '^q' || :)" 0 "and nothing queued"
start_hub
wait_for 5 '[ "$(dev a "$S" hub status | grep -c online)" -ge 1 ]' || fail "desk back online"

# the replica catches up with the hub a moment after it is back online
i=0; out=$(dev a "$S" leases settle)
while ! printf '%s' "$out" | grep -qF -- "$ld" && [ $i -lt 20 ]; do sleep 0.25; i=$((i + 1)); out=$(dev a "$S" leases settle); done
assert_contains "$out" "$ld" "desk settles its own lease"
case $out in *"$ll"*) fail "desk settled lap's lease" ;; esac
wait_for 3 '[ -z "$(state b "$ld")" ]' || fail "lap sees desk's lease settled"
assert_eq "$(state b "$ll")" "expired,dirty" "lap's lease is still marked"
cd "$T/lap/repo"
i=0; out=$(dev b "$S" leases settle)
while ! printf '%s' "$out" | grep -qF -- "$ll" && [ $i -lt 20 ]; do sleep 0.25; i=$((i + 1)); out=$(dev b "$S" leases settle); done
assert_contains "$out" "$ll" "lap settles its own"
ev=$(jq -c --arg r "$ll" 'select(.type=="done" and .ref==$r)' "$B")
assert_eq "$(printf '%s' "$ev" | jq -r .device)" lap "the done is recorded from lap"
echo "ok settle across a hub"
