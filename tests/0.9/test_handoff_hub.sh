#!/bin/sh
# 0.9: through a hub the handoff is in the shared ledger: the other device sees it at once,
# picks it up by id, and the sender sees it taken; the open items are already shared
. "$(dirname "$0")/../0.7/lib.sh"
setup_two
as() { n=$1; shift; GIT_AUTHOR_NAME=$n GIT_AUTHOR_EMAIL=$n@x GIT_COMMITTER_NAME=$n GIT_COMMITTER_EMAIL=$n@x "$@"; }
git init -q --bare -b main "$T/remote.git"
cd "$T/desk/repo" && printf '.sankofa/\n' > .gitignore && git init -q -b main && git remote add origin "$T/remote.git" &&
  git add .gitignore && as Desk git commit -qm init && git push -q -u origin main 2>/dev/null
cd "$T/lap/repo" && printf '.sankofa/\n' > .gitignore && git init -q -b main && git remote add origin "$T/remote.git" &&
  git fetch -q origin && git reset -q --hard origin/main

cd "$T/desk/repo"
as Desk git switch -qc feat && echo x > x.c && git add x.c && as Desk git commit -qm "x"
todo=$(dev a "$S" todo "tests for x")
out=$(SANKOFA_SESSION=s-desk as Desk dev a "$S" handoff "x is in; tests next")
id=$(printf '%s' "$out" | grep -o 'e[0-9][0-9]*' | tail -n1)
[ -n "$id" ] || fail "handoff printed an id: $out"
ev=$(jq -c --arg i "$id" 'select(.id==$i)' "$A")
assert_eq "$(printf '%s' "$ev" | jq -r .device)" desk "the hub records the sending device"

cd "$T/lap/repo"
wait_for 3 '[ "$(count "$B" ".type==\"handoff\"")" -eq 1 ]' || fail "lap sees the handoff through the hub"
assert_contains "$(dev b "$S" open)" "handoff" "it is open on lap"
out=$(as Lap dev b "$S" pickup "$id")
assert_contains "$out" "picked up feat from Desk" "lap picks it up by id"
assert_eq "$(git branch --show-current)" feat "lap is on the branch"
assert_eq "$(count "$B" ".type==\"todo\"")" 1 "the open items are shared already, not copied"
cd "$T/desk/repo"
wait_for 3 '[ "$(count "$A" ".type==\"done\" and .ref==\"'"$id"'\"")" -eq 1 ]' || fail "desk sees it taken"
assert_contains "$(jq -r --arg i "$id" 'select(.type=="done" and .ref==$i) | .text' "$A")" "picked up on lap" "by lap"
assert_contains "$(dev a "$S" open)" "$todo" "the todo is still open, for lap to finish"
echo "ok handoff through a hub"
