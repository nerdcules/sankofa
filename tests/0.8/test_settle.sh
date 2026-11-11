#!/bin/sh
# 0.8.1: an ended lease keeps its half-finished mark until git shows its paths clean in every
# worktree; then sankofad finishes it with a done that says why
. "$(dirname "$0")/../0.4/lib.sh"
export SANKOFA_SETTLE=3600   # no periodic pass while the cases are set up; settle is asked for
start_daemon
g() { git -c user.name=t -c user.email=t@t "$@"; }
mkdir repo && cd repo && g init -q -b main && mkdir src
for f in a b c d e; do echo "int $f;" > src/$f.c; done
printf '.sankofa/\nbuild/\n' > .gitignore && g add -A && g commit -qm init
"$S" init >/dev/null
g worktree add -q -b feat "$T/wt" 2>/dev/null
export SANKOFA_SESSION=s1
claim() { "$S" claim "$1" 1 | grep -o 'e[0-9][0-9]*' | head -1; }
la=$(claim src/a.c); lb=$(claim src/b.c); lc=$(claim src/c.c); ld=$(claim src/d.c); lbuild=$(claim build/out.o)
le=$(claim 'src/e*')
echo 'int a2;' >> src/a.c && g commit -qam "a"            # edited and committed
echo 'int b2;' >> src/b.c                                  # edited, not committed
echo 'int c2;' >> "$T/wt/src/c.c"                          # edited in the other worktree
"$S" release "$ld" >/dev/null                              # released, nothing edited
mkdir -p build && echo x > build/out.o                     # ignored by git
echo 'int e2;' > src/e2.c                                  # a new file under the glob, untracked
sleep 1.2
state() { "$S" leases | awk -v id="$1" '$1 == id { print $2 }'; }
assert_eq "$(state "$la")" "expired,dirty" "a.c expired dirty before settling"

out=$("$S" leases settle)
assert_contains "$out" "$la" "committed a.c is settled"
assert_contains "$out" "$ld" "released clean d.c is settled"
assert_contains "$out" "$lbuild" "a path git ignores counts as clean"
assert_eq "$(state "$la")" "" "a.c no longer listed"
assert_eq "$(state "$lb")" "expired,dirty" "uncommitted b.c keeps its mark"
assert_eq "$(state "$lc")" "expired,dirty" "c.c changed in another worktree keeps its mark"
assert_eq "$(state "$le")" "expired,dirty" "an untracked file under the glob keeps it"
ev=$(jq -c --arg r "$la" 'select(.type=="done" and .ref==$r)' .sankofa/ledger.jsonl)
assert_eq "$(printf '%s' "$ev" | jq -r .session)" "sankofad" "the daemon writes the done"
assert_contains "$(printf '%s' "$ev" | jq -r .text)" "no uncommitted changes in git" "the done says why"
assert_eq "$("$S" leases settle)" "nothing to settle" "a second pass writes nothing"

# committing in each tree settles the rest, on the periodic pass this time
g commit -qam "b"
(cd "$T/wt" && g commit -qam "c")
g add src/e2.c && g commit -qm "e"
stop_daemon; SANKOFA_SETTLE=0.3 start_daemon
i=0; while [ -n "$(state "$lb")$(state "$lc")$(state "$le")" ] && [ $i -lt 100 ]; do sleep 0.05; i=$((i + 1)); done
assert_eq "$(state "$lb")$(state "$lc")$(state "$le")" "" "the periodic pass settles b.c, c.c and src/e*"
assert_eq "$(jq -c 'select(.type=="done" and .session=="sankofad")' .sankofa/ledger.jsonl | grep -c .)" 6 "one done per lease"

# not a git repository: no opinion, the marks stay
mkdir "$T/plain" && cd "$T/plain" && "$S" init >/dev/null
lp=$(claim notes.txt); sleep 1.2
assert_eq "$("$S" leases settle)" "nothing to settle" "outside git nothing is settled"
assert_eq "$(state "$lp")" "expired,dirty" "and the mark stays"
echo "ok settle"
