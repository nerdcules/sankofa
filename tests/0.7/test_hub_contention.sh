#!/bin/sh
# 0.7: two devices against one hub: one ledger, leases honoured across devices
. "$(dirname "$0")/lib.sh"
setup_two
assert_eq "$(count "$A" '.text=="desk history before the hub"')" 1 "the first device seeds the hub"
assert_eq "$(count "$B" '.text=="lap history, kept aside"')" 0 "a later device joins the hub's ledger"
assert_eq "$(count "$T/lap/repo/.sankofa/ledger.pre-hub.jsonl" '.text=="lap history, kept aside"')" 1 "its own history is kept"
wait_for 3 '[ "$(count "$B" ".text==\"desk history before the hub\"")" = 1 ]' || fail "lap sees desk's history"

cd "$T/desk/repo"
out=$(SANKOFA_SESSION=s-desk dev a "$S" claim 'src/**' 10m); assert_contains "$out" "held" "desk claims src/**"
cd "$T/lap/repo"
set +e; err=$(SANKOFA_SESSION=s-lap dev b "$S" claim src/x.c 2>&1); rc=$?; set -e
assert_eq "$rc" 2 "lap's overlapping claim is denied"
assert_contains "$err" "held by s-desk [main] on desk" "the denial names the other device"
# the edit gate on lap
mkdir -p src; OUT=$(edit_json s-lap "$T/lap/repo/src/y.c" | dev b "$SANKOFAD" gate claude-code)
assert_eq "$(printf '%s' "$OUT" | jq -r .hookSpecificOutput.permissionDecision)" deny "lap's edit gate denies"
assert_contains "$(printf '%s' "$OUT" | jq -r .hookSpecificOutput.permissionDecisionReason)" "on desk" "the gate names the device"
# commit gate on lap refuses desk's paths: same as local (check through the hub)
dev b "$S" note "lap: waiting for desk to finish src"
wait_for 3 '[ "$(count "$A" ".text==\"lap: waiting for desk to finish src\"")" = 1 ]' || fail "desk sees lap's note within 3 s"
ev=$(jq -c 'select(.text=="lap: waiting for desk to finish src")' "$A")
assert_eq "$(printf '%s' "$ev" | jq -r .device)" lap "events record the device"
assert_eq "$(printf '%s' "$ev" | jq -r .client)" cli "events record the client"
assert_eq "$(jq -r .id "$A" | tr '\n' ' ')" "$(jq -r .id "$B" | tr '\n' ' ')" "both replicas hold the same events"

# desk finishes: lap can claim
cd "$T/desk/repo"; id=$(dev a "$S" leases | awk '$3=="src/**"{print $1}')
SANKOFA_SESSION=s-desk dev a "$S" done "$id" "src done" >/dev/null
cd "$T/lap/repo"; out=$(SANKOFA_SESSION=s-lap dev b "$S" claim src/x.c); assert_contains "$out" "held" "after done, lap gets the lease"
assert_contains "$(dev b "$S" hub)" "hub online  demo" "hub status"
echo "ok hub contention"
