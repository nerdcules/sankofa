#!/bin/sh
# 0.6: a bare remote receives a push -> one landed event with branch and sha
. "$(dirname "$0")/../0.4/lib.sh"
export SANKOFA_BRIDGE_TICK=0.2
start_daemon
g() { git -c user.name=Routine -c user.email=r@r "$@"; }
g init -q --bare -b main remote.git
g clone -q remote.git repo 2>/dev/null
cd repo && printf '.sankofa/\n' > .gitignore && g add -A && g commit -qm init && g push -q origin main 2>/dev/null
"$S" init >/dev/null
g clone -q "$T/remote.git" "$T/routine" 2>/dev/null
landed() { jq -c 'select(.type=="landed")' .sankofa/ledger.jsonl | grep -c . || :; }

out=$("$S" bridge on 1h); assert_contains "$out" "bridge on" "bridge on"
assert_contains "$out" "1 remote branches" "the baseline is taken at once"
assert_eq "$(landed)" 0 "the baseline records nothing"

# the routine pushes a new branch; an explicit fetch reports it
(cd "$T/routine" && g checkout -qb routine/nightly && echo x > n.txt && g add n.txt && g commit -qm "nightly run" && g push -q origin routine/nightly 2>/dev/null)
sha=$(git -C "$T/routine" rev-parse HEAD)
out=$("$S" bridge fetch); assert_contains "$out" "landed" "fetch prints the landing"
assert_eq "$(landed)" 1 "one landed event"
ev=$(jq -c 'select(.type=="landed")' .sankofa/ledger.jsonl)
assert_eq "$(printf '%s' "$ev" | jq -r .branch)" "origin/routine/nightly" "landed names the branch"
assert_eq "$(printf '%s' "$ev" | jq -r .sha)" "$sha" "landed names the sha"
assert_contains "$(printf '%s' "$ev" | jq -r .text)" '"nightly run" by Routine' "landed says what and who"
"$S" bridge fetch >/dev/null; assert_eq "$(landed)" 1 "fetching again records nothing new"

# commits to an existing branch, while the daemon is down (the machine asleep): reported on return
stop_daemon
(cd "$T/routine" && for i in 1 2; do echo $i >> n.txt; g commit -qam "step $i"; done && g push -q 2>/dev/null)
git fetch -q   # the human's own fetch moves origin/... first; still reported
start_daemon
"$S" bridge fetch >/dev/null
assert_eq "$(landed)" 2 "a push made while the daemon was down is reported"
assert_contains "$(jq -r 'select(.type=="landed") | .text' .sankofa/ledger.jsonl | tail -n1)" "2 new commits" "counts the commits"

# the human's own push is not a landing
echo y > own.txt && g add own.txt && g commit -qm own && g push -q origin main 2>/dev/null
"$S" bridge fetch >/dev/null; assert_eq "$(landed)" 2 "your own push is not reported"
echo z > own2.txt && g add own2.txt && g commit -qm own2 && g push -q origin main 2>/dev/null
g commit -q --allow-empty -m "more on top, not pushed yet"
"$S" bridge fetch >/dev/null; assert_eq "$(landed)" 2 "your own push with commits on top is not reported"

# the loop: with a short interval, a push is found without asking
"$S" bridge on 1 >/dev/null
(cd "$T/routine" && g checkout -q main && g pull -q 2>/dev/null; g checkout -qb second && g commit -q --allow-empty -m second && g push -q origin second 2>/dev/null)
i=0; while [ "$(landed)" -lt 3 ] && [ $i -lt 100 ]; do sleep 0.05; i=$((i + 1)); done
assert_eq "$(landed)" 3 "the fetch loop finds a push by itself"

# a force push is called a rewrite
(cd "$T/routine" && g commit -q --amend --allow-empty -m second-again && g push -qf origin second 2>/dev/null)
"$S" bridge fetch >/dev/null
assert_contains "$(jq -r 'select(.type=="landed") | .text' .sankofa/ledger.jsonl | tail -n1)" "rewritten" "a force push is a rewrite"

# an unreachable remote is a recorded failure, not a hang
git remote set-url origin "$T/gone.git"
set +e; err=$("$S" bridge fetch 2>&1 >/dev/null); set -e
assert_contains "$err" "fetch failed" "a failed fetch is reported"
assert_contains "$("$S" bridge)" "FAILED" "status shows the failure"
"$S" bridge off >/dev/null; assert_contains "$("$S" bridge)" "bridge off" "bridge off"
echo "ok bridge"
