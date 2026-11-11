#!/bin/sh
# 0.9: out of credits, hand the branch to a teammate through nothing but the git remote; they
# pick it up, and the sender's bridge sees it taken
. "$(dirname "$0")/../0.4/lib.sh"
start_daemon
as() { # as <name> <command...>: git and sankofa as that person
  n=$1; shift
  GIT_AUTHOR_NAME=$n GIT_AUTHOR_EMAIL=$n@x GIT_COMMITTER_NAME=$n GIT_COMMITTER_EMAIL=$n@x SANKOFA_SESSION=s-$n "$@"
}
git init -q --bare -b main remote.git
as Alice git clone -q remote.git alice 2>/dev/null
cd alice && printf '.sankofa/\n' > .gitignore && echo 'int main;' > parse.c && git add -A && as Alice git commit -qm init && git push -q origin main 2>/dev/null
"$S" init >/dev/null
as Bob git clone -q "$T/remote.git" "$T/bob" 2>/dev/null
(cd "$T/bob" && "$S" init >/dev/null && "$S" bridge on 1h >/dev/null)

# Alice works on a branch, runs out of credits
as Alice git switch -qc parser
echo 'int parse;' >> parse.c && as Alice git commit -qam "parse numbers"
as Alice "$S" todo "error messages for bad input" >/dev/null
echo 'half' >> parse.c
set +e; err=$(as Alice "$S" handoff "numbers parse" 2>&1); rc=$?; set -e
assert_eq "$rc" 1 "a dirty tree is refused"
assert_contains "$err" "commit first" "and says why"
assert_contains "$err" "parse.c" "naming the file"
as Alice git commit -qam "parse words"
out=$(as Alice "$S" handoff "numbers and words parse; next: error messages")
assert_contains "$out" "handed off parser" "handoff reports the branch"
assert_contains "$out" "sankofa pickup parser" "and what to run on the other side"
assert_eq "$(git ls-remote "$T/remote.git" refs/heads/parser | cut -f1)" "$(git rev-parse HEAD)" "the branch is pushed"
assert_eq "$(git ls-remote "$T/remote.git" refs/sankofa/handoff/parser | grep -c .)" 1 "the letter is on the remote"
ev=$(jq -c 'select(.type=="handoff")' .sankofa/ledger.jsonl)
assert_eq "$(printf '%s' "$ev" | jq -r .branch)" origin/parser "the handoff names the remote branch"
assert_eq "$(printf '%s' "$ev" | jq -r .sha)" "$(git rev-parse HEAD)" "and the commit"
assert_contains "$("$S" open)" "handoff" "the sender's handoff is open until it is taken"

# Bob's bridge sees it: a handoff event, not a landed one
cd "$T/bob"
"$S" bridge fetch >/dev/null
assert_eq "$(jq -c 'select(.type=="handoff")' .sankofa/ledger.jsonl | grep -c .)" 1 "the bridge records the handoff"
assert_eq "$(jq -c 'select(.type=="landed")' .sankofa/ledger.jsonl | grep -c . || :)" 0 "no separate landed event for it"
assert_contains "$(jq -r 'select(.type=="handoff") | .text' .sankofa/ledger.jsonl)" "from Alice: numbers and words parse" "who and what"
"$S" bridge fetch >/dev/null
assert_eq "$(jq -c 'select(.type=="handoff")' .sankofa/ledger.jsonl | grep -c .)" 1 "fetching again records nothing new"

# Bob picks it up
echo scratch > notes.txt   # untracked files do not block a pickup
out=$(as Bob "$S" pickup)
assert_contains "$out" "picked up parser from Alice" "pickup says what and from whom"
assert_contains "$out" "next: error messages" "and prints the letter"
assert_eq "$(git branch --show-current)" parser "Bob is on the branch"
assert_eq "$(git rev-parse HEAD)" "$(git -C "$T/alice" rev-parse HEAD)" "at Alice's commit"
assert_eq "$(git rev-parse --abbrev-ref '@{u}')" origin/parser "tracking the remote branch"
assert_eq "$(jq -r 'select(.type=="handoff") | .id' .sankofa/ledger.jsonl | while read -r i; do jq -c --arg i "$i" 'select(.type=="done" and .ref==$i)' .sankofa/ledger.jsonl; done | grep -c .)" 1 "the handoff is finished"
assert_contains "$("$S" open)" "error messages for bad input (from Alice, was e1)" "Alice's open items are Bob's todos now"
assert_eq "$(git ls-remote "$T/remote.git" 'refs/sankofa/handoff/*' | grep -c . || :)" 0 "the letter is gone from the remote"
assert_eq "$(git ls-remote "$T/remote.git" refs/sankofa/taken/parser | grep -c . || :)" 1 "a marker says it was taken"
git fetch -q "$T/remote.git" refs/sankofa/taken/parser
assert_contains "$(git cat-file tag FETCH_HEAD)" "tagger Bob" "and by whom"
assert_eq "$(as Bob "$S" pickup)" "nothing handed off" "nothing left to pick up"

# Alice's bridge sees it taken
cd "$T/alice"
"$S" bridge on 1h >/dev/null
assert_contains "$(jq -r 'select(.type=="done") | .text' .sankofa/ledger.jsonl)" "picked up by Bob: the handoff of origin/parser is gone" "the sender's handoff is finished, naming who took it"
assert_eq "$("$S" open | grep -c handoff || :)" 0 "and no longer open"

# and back, to someone with no daemon and no bridge
cd "$T/bob"
echo 'int err;' >> parse.c && as Bob git commit -qam "errors" && rm notes.txt
as Bob "$S" handoff >/dev/null 2>&1 && fail "no text and no agent reply: refused" || :
as Bob "$S" handoff "errors done; review it" >/dev/null
as Carol git clone -q "$T/remote.git" "$T/carol" 2>/dev/null
cd "$T/carol" && SANKOFA_NO_DAEMON=1 "$S" init >/dev/null
set +e; SANKOFA_NO_DAEMON=1 as Carol "$S" pickup nope >/dev/null; rc=$?; set -e
assert_eq "$rc" 1 "an unknown handoff is an error"
out=$(SANKOFA_NO_DAEMON=1 as Carol "$S" pickup parser)
assert_contains "$out" "picked up parser from Bob" "picked up without a daemon"
assert_eq "$(git log -1 --format=%s)" errors "at Bob's commit"
assert_contains "$(jq -r 'select(.type=="note") | .text' .sankofa/ledger.jsonl)" "errors done; review it" "the letter is kept as a note"
echo "ok handoff"
