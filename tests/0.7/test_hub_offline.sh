#!/bin/sh
# 0.7: hub down -> allow-and-flag on the device; the queue is written on return
. "$(dirname "$0")/lib.sh"
setup_two
wait_for 3 '[ "$(count "$B" ".text==\"desk history before the hub\"")" = 1 ]' || fail "lap synced"
stop_hub
cd "$T/lap/repo"
dev b "$S" note "written on the train"
tid=$(dev b "$S" todo "finish the parser")
case $tid in q*) ;; *) fail "an offline todo gets a queued id, got $tid" ;; esac
out=$(SANKOFA_SESSION=s-lap dev b "$S" claim src/parse.c); assert_contains "$out" "queued" "an offline claim is allowed and queued"
mkdir -p src; OUT=$(edit_json s-lap "$T/lap/repo/src/lex.c" | dev b "$SANKOFAD" gate claude-code)
assert_eq "$OUT" "" "the edit gate allows offline"
dev b "$S" done "$tid" "parser finished offline"
assert_contains "$(dev b "$S" open)" "no open items" "the offline done resolves the offline todo locally"
# lap's sankofad down too: the CLI's own fallback queues
stop_dev b; dev b "$S" note "written with no daemon at all"; start_dev b
set +e; dev b "$S" leases >/dev/null 2>&1; rc=$?; set -e
assert_eq "$rc" 1 "reads that need the hub fail while it is down"
cd "$T/lap/repo"; git init -q; git add -A 2>/dev/null; echo x > src/parse.c; git add src/parse.c
set +e; dev b "$SANKOFAD" gate git-pre-commit 2>/dev/null; rc=$?; set -e
assert_eq "$rc" 1 "commits stay gated while the hub is down"
# meanwhile desk (also offline from the hub) - then the hub returns
start_hub
wait_for 8 '! grep -q "\"id\":\"q" "$B"' || { cat "$TD/b.log" >&2; fail "lap's queue is reconciled within 8 s"; }
for t in "written on the train" "finish the parser" "parser finished offline" "written with no daemon at all"; do
  wait_for 3 '[ "$(count "$A" ".text==\"$t\"")" = 1 ]' || fail "desk sees '$t' exactly once"
  assert_eq "$(count "$B" ".text==\"$t\"")" 1 "lap has '$t' exactly once"
done
hid=$(jq -r 'select(.text=="finish the parser") | .id' "$A")
assert_eq "$(jq -r 'select(.text=="parser finished offline") | .ref' "$A")" "$hid" "the done's ref follows the todo's new id"
cd "$T/desk/repo"; assert_contains "$(dev a "$S" open)" "no open items" "desk sees the todo resolved"
# a q id still works for done after it was renumbered
cd "$T/lap/repo"; stop_hub; t2=$(dev b "$S" todo "second offline todo"); start_hub
wait_for 8 '! grep -q "\"id\":\"q" "$B"' || fail "second reconcile"
dev b "$S" done "$t2" "done by its old id" || fail "done with a renumbered q id"
cd "$T/desk/repo"; wait_for 3 '[ "$(count "$A" ".text==\"done by its old id\"")" = 1 ]' || fail "the late done reaches desk"
assert_contains "$(dev a "$S" open)" "no open items" "and resolves the item"
assert_eq "$(count "$A" '.type=="claim" and .glob=="src/parse.c"')" 1 "the queued claim became a lease on the hub"
assert_eq "$(jq -r .id "$A" | tr '\n' ' ')" "$(jq -r .id "$B" | tr '\n' ' ')" "replicas converge"

# conflict: desk holds a path, lap edits it offline -> contention flagged on return
SANKOFA_SESSION=s-desk dev a "$S" claim docs/plan.md >/dev/null
wait_for 3 '[ "$(count "$B" ".glob==\"docs/plan.md\"")" = 1 ]' || fail "lap sees desk's lease"
stop_hub
cd "$T/lap/repo"; SANKOFA_SESSION=s-lap dev b "$S" claim docs/plan.md >/dev/null
start_hub
wait_for 8 '[ "$(count "$A" ".type==\"flag\" and (.text|contains(\"edited docs/plan.md without a lease\"))")" = 1 ]' \
  || fail "the offline edit of a held path is flagged on the hub"
assert_contains "$(jq -r 'select(.type=="flag" and (.text|contains("docs/plan.md"))) | .text' "$A")" "while lap could not reach the hub" "the flag names the device"
echo "ok hub offline"
