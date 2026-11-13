#!/bin/sh
# 0.10 (found by sim/handoff.py): two people picking up one handoff at once get one owner; a
# copy of a handoff someone else already took is not offered; the same commit handed off again
# by someone else is a new handoff, not a repeat
. "$(dirname "$0")/../0.4/lib.sh"
start_daemon
as() { n=$1; shift; GIT_AUTHOR_NAME=$n GIT_AUTHOR_EMAIL=$n@x GIT_COMMITTER_NAME=$n GIT_COMMITTER_EMAIL=$n@x SANKOFA_SESSION=s-$n "$@"; }
git init -q --bare -b main remote.git
for n in alice bob carol; do as "$n" git clone -q remote.git "$n" 2>/dev/null; done
cd alice && printf '.sankofa/\n' > .gitignore && git add -A && as Alice git commit -qm init && git push -q origin main 2>/dev/null
for n in alice bob carol; do (cd "$T/$n" && git pull -q 2>/dev/null; "$S" init >/dev/null); done
(cd "$T/carol" && "$S" bridge on 1h >/dev/null)

as Alice git switch -qc feat && echo a > a.txt && git add a.txt && as Alice git commit -qm a
as Alice "$S" handoff "race me" >/dev/null
sha=$(git rev-parse HEAD)
(cd "$T/carol" && "$S" bridge fetch >/dev/null)   # Carol's ledger now has a copy

# Bob and Carol at the same moment
(trap - EXIT; cd "$T/bob" && as Bob "$S" pickup feat > "$T/bob.out" 2>&1; echo $? > "$T/bob.rc") & p1=$!
(trap - EXIT; cd "$T/carol" && as Carol "$S" pickup feat > "$T/carol.out" 2>&1; echo $? > "$T/carol.rc") & p2=$!
wait "$p1" || :; wait "$p2" || :   # not a bare wait: the daemon is a child too (and the subshells must not run the exit trap)
won=$(grep -l "^picked up feat" "$T/bob.out" "$T/carol.out" | wc -l | tr -d ' ')
assert_eq "$won" 1 "exactly one of them has it"
lost=$(grep -l "someone else picked up feat first\|nothing handed off" "$T/bob.out" "$T/carol.out" | wc -l | tr -d ' ')
assert_eq "$lost" 1 "the other is told"
for n in bob carol; do
  if grep -q "^picked up" "$T/$n.out"; then winner=$n; else loser=$n; fi
done
assert_eq "$(git -C "$T/$loser" branch --show-current)" main "the loser did not switch"

# Carol's copy is not offered once the letter is gone, even before her bridge looks again
if [ "$loser" = carol ]; then
  assert_eq "$(cd "$T/carol" && as Carol "$S" pickup)" "nothing handed off" "a copy taken elsewhere is not offered"
fi

# the winner hands the same commit straight on: a new letter, recorded and offered as new
cd "$T/$winner"
as Winner "$S" handoff "not for me after all" >/dev/null
assert_eq "$(git rev-parse HEAD)" "$sha" "the same commit"
cd "$T/carol"
if [ "$winner" = carol ]; then cd "$T/alice" && git switch -q main; fi
out=$(as Someone "$S" pickup feat)
assert_contains "$out" "not for me after all" "the second letter is picked up, not mistaken for the first"
(cd "$T/carol" && "$S" bridge fetch >/dev/null)
assert_eq "$(cd "$T/carol" && "$S" open | grep -c handoff || :)" 0 "nothing left open on Carol's machine"
echo "ok handoff race"
