#!/bin/sh
# 0.7: a bad or revoked token is refused and logged
. "$(dirname "$0")/lib.sh"
setup_two
code=$(curl -s -o /dev/null -w '%{http_code}' -H 'Authorization: Bearer sk_nope' -d '{"verb":"ping"}' "$HUB/v1/rpc")
assert_eq "$code" 401 "an unknown token is refused"
code=$(curl -s -o /dev/null -w '%{http_code}' -d '{"verb":"ping"}' "$HUB/v1/rpc")
assert_eq "$code" 401 "no token is refused"
wait_for 2 'grep -q "rejected .* POST /v1/rpc: unknown token" "$TD/hub.log"' || fail "the refusal is logged"
grep -q "rejected .* no token" "$TD/hub.log" || fail "a missing token is logged"
# revoke lap: its writes are refused, not queued
"$SANKOFAD" hub token revoke lap 2>/dev/null
cd "$T/lap/repo"
set +e; err=$(dev b "$S" note "after revoke" 2>&1); rc=$?; set -e
assert_eq "$rc" 1 "a revoked device's write fails"
assert_contains "$err" "refused this device's token" "and says why"
assert_eq "$(count "$B" '.text=="after revoke"')" 0 "nothing is queued for a refused token"
cd "$T/desk/repo"; dev a "$S" note "desk still fine" && assert_eq "$(count "$A" '.text=="desk still fine"')" 1 "other devices are unaffected"
# a bad repo name is refused
code=$(curl -s -H "Authorization: Bearer $ta" -d '{"verb":"open","repo":"../etc"}' "$HUB/v1/rpc" | jq -r .error)
assert_contains "$code" "bad repo name" "repo names cannot escape the hub's data dir"
echo "ok hub token"
