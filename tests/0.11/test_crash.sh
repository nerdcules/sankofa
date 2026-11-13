#!/bin/sh
# 0.11: a handoff or pickup killed halfway is finished by running it again. sim/crash.py kills
# them at every step; this keeps the two worst moments: just after the letter is pushed, and just
# after the pickup's claim lands on the remote.
. "$(dirname "$0")/../0.4/lib.sh"
start_daemon
as() { n=$1; shift; GIT_AUTHOR_NAME=$n GIT_AUTHOR_EMAIL=$n@x GIT_COMMITTER_NAME=$n GIT_COMMITTER_EMAIL=$n@x SANKOFA_SESSION=s-$n "$@"; }
# a git that kills the whole command (its process group) right after a call matching $CRASH_MATCH
mkdir "$T/shim"; REAL=$(command -v git)
cat > "$T/shim/git" <<SH
#!/bin/sh
"$REAL" "\$@"; rc=\$?
case " \$* " in *"\$CRASH_MATCH"*) kill -9 "-\$(ps -o pgid= -p \$\$ | tr -d ' ')" ;; esac
exit \$rc
SH
chmod +x "$T/shim/git"
crash() { # crash <match> <name> sankofa-args...: run sankofa as <name>, killed after the matching git call
  m=$1; n=$2; shift 2
  set +e; as "$n" env PATH="$T/shim:$PATH" CRASH_MATCH="$m" setsid -w "$S" "$@" >/dev/null 2>&1; rc=$?; set -e
  assert_eq "$rc" 137 "the command was killed (after: $m)"
}

git init -q --bare -b main remote.git
for n in alice bob carol; do as "$n" git clone -q remote.git "$n" 2>/dev/null; done
cd "$T/alice" && printf '.sankofa/\n' > .gitignore && git add -A && as Alice git commit -qm init && git push -q origin main 2>/dev/null
for n in alice bob carol; do (cd "$T/$n" && "$S" init >/dev/null && "$S" bridge on 1h >/dev/null); done

# Alice is cut off right after her letter reaches the remote, before the ledger hears of it
as Alice git switch -qc feat && echo a > a.txt && git add a.txt && as Alice git commit -qm a
as Alice "$S" todo "write the docs" >/dev/null
crash "push -q --force-with-lease=refs/sankofa/handoff/feat" Alice handoff "feat works; next: docs"
assert_contains "$("$S" open)" "the handoff of feat was interrupted" "sankofa open says so"
"$S" bridge fetch >/dev/null
assert_eq "$(jq -c 'select(.type=="handoff")' .sankofa/ledger.jsonl | grep -c . || :)" 0 "her bridge does not offer her own letter"
out=$(as Alice "$S" handoff "feat works; next: docs" 2>&1)
assert_contains "$out" "finishing the handoff of feat" "running it again finishes it"
assert_eq "$(jq -c 'select(.type=="handoff")' .sankofa/ledger.jsonl | grep -c .)" 1 "one handoff"
assert_eq "$(git ls-remote "$T/remote.git" 'refs/sankofa/handoff/*' | grep -vc '\^{}')" 1 "one letter"
out=$(as Alice "$S" handoff "feat works; next: docs")
assert_contains "$out" "already handed off feat" "and once more changes nothing"

# Bob is cut off right after his claim lands, before he is on the branch
cd "$T/bob"
crash "--atomic" Bob pickup
assert_eq "$(git ls-remote "$T/remote.git" 'refs/sankofa/handoff/*' | grep -c . || :)" 0 "the claim landed"
assert_contains "$("$S" open)" "the pickup of feat was interrupted" "Bob's open says so"
assert_contains "$(cd "$T/carol" && as Carol "$S" pickup)" "nothing handed off" "Carol cannot take it from him"
out=$(as Bob "$S" pickup 2>&1)
assert_contains "$out" "finishing the pickup of feat" "running it again finishes it"
assert_contains "$out" "feat works; next: docs" "with the letter"
assert_eq "$(git branch --show-current)" feat "Bob is on feat"
assert_eq "$(git rev-parse HEAD)" "$(git -C "$T/alice" rev-parse HEAD)" "at Alice's commit"
assert_eq "$("$S" open | grep -c 'write the docs')" 1 "the letter's todo is his, once"
[ ! -e .sankofa/pickup.json ] || fail "the journal is gone"

# Alice's bridge closes her handoff and names who took it
cd "$T/alice" && "$S" bridge fetch >/dev/null
assert_contains "$(jq -r 'select(.type=="done") | .text' .sankofa/ledger.jsonl)" "picked up by Bob" "Alice sees Bob took it"
echo "ok crash"
