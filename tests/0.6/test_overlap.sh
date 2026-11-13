#!/bin/sh
# 0.6: two worktrees of one repo touch the same path -> exactly one overlap event
. "$(dirname "$0")/../0.4/lib.sh"
export SANKOFA_RESCAN=1   # the periodic rescan, sped up; inotify should beat it anyway
start_daemon
g() { git -c user.name=t -c user.email=t@t "$@"; }
mkdir repo && cd repo && g init -q -b main && mkdir src && echo 'int a;' > src/a.c && echo 'int b;' > src/b.c \
  && printf '.sankofa/\n' > .gitignore && g add -A && g commit -qm init
"$S" init >/dev/null
g worktree add -q -b feat "$T/wt" 2>/dev/null
overlaps() { jq -c 'select(.type=="overlap")' .sankofa/ledger.jsonl | grep -c . || :; }
wait_overlaps() { # n: up to 5 s for the ledger to hold n overlap events
  i=0; while [ "$(overlaps)" -lt "$1" ] && [ $i -lt 100 ]; do sleep 0.05; i=$((i + 1)); done
}

out=$("$S" trees on); assert_contains "$out" "$T/wt" "trees on lists the worktree"
assert_contains "$out" "feat" "trees on names the worktree's branch"

# different paths in the two trees: no overlap
echo 'int a2;' >> src/a.c
echo 'int b2;' >> "$T/wt/src/b.c"
sleep 1.5; assert_eq "$(overlaps)" 0 "different paths do not overlap"

# the same path, through inotify (no scan asked for)
t0=$(now_ns)
echo 'int a3;' >> "$T/wt/src/a.c"
wait_overlaps 1
t1=$(now_ns)
assert_eq "$(overlaps)" 1 "one overlap for src/a.c"
ev=$(jq -c 'select(.type=="overlap")' .sankofa/ledger.jsonl)
assert_eq "$(printf '%s' "$ev" | jq -r .glob)" "src/a.c" "the overlap names the path"
assert_contains "$(printf '%s' "$ev" | jq -r .text)" "wt [feat]" "the overlap names both trees"
assert_contains "$(printf '%s' "$ev" | jq -r .text)" "repo [main]" "the overlap names both trees"
echo "overlap recorded $(( (t1 - t0) / 1000000 )) ms after the second write"

# more writes to the same path, rescans, a daemon restart: still exactly one
for i in 1 2 3; do echo "int x$i;" >> src/a.c; echo "int y$i;" >> "$T/wt/src/a.c"; sleep 0.1; done
"$S" trees scan >/dev/null; sleep 1.5
stop_daemon; start_daemon; "$S" trees scan >/dev/null
assert_eq "$(overlaps)" 1 "still exactly one overlap"

# committing in the worktree still counts (it changed the path since leaving main)
(cd "$T/wt" && g commit -qam wip)
"$S" trees scan >/dev/null; assert_eq "$(overlaps)" 1 "a commit in the worktree does not re-report"

# the overlap clears when main's change is undone; touching it again reports again
g checkout -q -- src/a.c; "$S" trees scan >/dev/null
echo 'int z;' >> src/a.c; wait_overlaps 2
assert_eq "$(overlaps)" 2 "a fresh overlap after it cleared is reported"

# a new subdirectory made after the watch started is watched too
mkdir -p src/new "$T/wt/src/new"; sleep 0.3
echo 1 > src/new/c.c; echo 2 > "$T/wt/src/new/c.c"; wait_overlaps 3
assert_eq "$(overlaps)" 3 "files in a new directory overlap too"

# ignored files never overlap
printf 'build/\n' >> .gitignore; printf 'build/\n' >> "$T/wt/.gitignore"
"$S" trees scan >/dev/null; n=$(overlaps)   # .gitignore itself now overlaps
mkdir -p build "$T/wt/build"; echo 1 > build/o; echo 2 > "$T/wt/build/o"; sleep 1.5
assert_eq "$(overlaps)" "$n" "ignored files do not overlap"

out=$("$S" trees); assert_contains "$out" "worktree" "trees lists kinds"
"$S" trees off >/dev/null
echo 'int q;' >> src/b.c; echo 'int q;' >> "$T/wt/src/b.c"; sleep 1.5
assert_eq "$(overlaps)" "$n" "trees off stops the watch"
echo "ok overlap"
