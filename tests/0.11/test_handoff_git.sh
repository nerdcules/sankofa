#!/bin/sh
# 0.11: handoffs when git is not tidy: a branch rewritten or deleted after the handoff, a remote
# that is not called origin beside a second one, a branch name with slashes
. "$(dirname "$0")/../0.4/lib.sh"
start_daemon
as() { n=$1; shift; GIT_AUTHOR_NAME=$n GIT_AUTHOR_EMAIL=$n@x GIT_COMMITTER_NAME=$n GIT_COMMITTER_EMAIL=$n@x SANKOFA_SESSION=s-$n "$@"; }
git init -q --bare -b main up.git; git init -q --bare -b main fork.git
git init -q -b main alice && cd alice && git remote add upstream "$T/up.git" && git remote add fork "$T/fork.git"
printf '.sankofa/\n' > .gitignore && git add -A && as Alice git commit -qm init && git push -q upstream main 2>/dev/null && git push -q fork main 2>/dev/null
"$S" init >/dev/null
git init -q -b main "$T/bob" && cd "$T/bob" && git remote add upstream "$T/up.git" && git remote add fork "$T/fork.git" &&
  git fetch -q upstream && git reset -q --hard upstream/main && "$S" init >/dev/null
cd "$T/alice"

# a branch with slashes, through a remote called upstream (the first one: no origin here)
as Alice git switch -qc team/alice/parser && echo 1 > p.c && git add p.c && as Alice git commit -qm p1
set +e; err=$(as Alice "$S" handoff "parser one" 2>&1); rc=$?; set -e
assert_eq "$rc" 1 "with several remotes, no origin and no upstream set, sankofa does not guess"
assert_contains "$err" "git push -u <remote> team/alice/parser" "it says what to do"
git push -q -u fork team/alice/parser 2>/dev/null
out=$(as Alice "$S" handoff "parser one")
assert_contains "$out" "pushed to fork" "then the branch's own remote is used"
cd "$T/bob" && out=$(as Bob "$S" pickup team/alice/parser)
assert_contains "$out" "picked up team/alice/parser" "picked up by its slashed name"
assert_eq "$(git rev-parse --abbrev-ref '@{u}')" fork/team/alice/parser "tracking the remote it came from"

# Alice hands off, then rewrites the branch on the remote: Bob gets what she handed off, and is told
cd "$T/alice" && as Alice git switch -qc rw && echo 1 > r.c && git add r.c && as Alice git commit -qm r1
git push -q -u upstream rw 2>/dev/null
handed=$(git rev-parse HEAD)
as Alice "$S" handoff "rw one" >/dev/null
as Alice git commit -q --amend -m "r1 reworded" && git push -q -f upstream rw 2>/dev/null
cd "$T/bob" && out=$(as Bob "$S" pickup rw 2>&1)
assert_contains "$out" "picked up rw" "the pickup still works"
assert_eq "$(git rev-parse HEAD)" "$handed" "at the handed-off commit"
assert_contains "$out" "upstream/rw was rewritten after the handoff" "and Bob is told the branch moved away"

# Alice hands off, then deletes the branch on the remote: the letter still has the commit
cd "$T/alice" && as Alice git switch -qc gone && echo 1 > g.c && git add g.c && as Alice git commit -qm g1
git push -q -u upstream gone 2>/dev/null
handed=$(git rev-parse HEAD)
as Alice "$S" handoff "gone one" >/dev/null
git push -q upstream :gone 2>/dev/null
cd "$T/bob" && out=$(as Bob "$S" pickup gone 2>&1)
assert_contains "$out" "picked up gone" "a branch deleted from the remote can still be picked up"
assert_eq "$(git rev-parse HEAD)" "$handed" "at the handed-off commit"
assert_contains "$out" "upstream/gone is no longer on upstream" "and Bob is told"
# a remote that keeps the letter but not the marker: removing the letter alone is still the claim
cd "$T" && git init -q --bare -b main strict.git
printf '#!/bin/sh\nwhile read o n r; do case $r in refs/sankofa/taken/*) echo "not here" >&2; exit 1 ;; esac; done\n' > strict.git/hooks/pre-receive
chmod +x strict.git/hooks/pre-receive
cd "$T/alice" && git remote add strict "$T/strict.git" && as Alice git switch -qc st && echo 1 > s.c && git add s.c && as Alice git commit -qm s1
git push -q -u strict st 2>/dev/null && as Alice "$S" handoff "strict one" >/dev/null
cd "$T/bob" && git remote add strict "$T/strict.git"
out=$(as Bob "$S" pickup st 2>&1)
assert_contains "$out" "picked up st" "a remote that refuses the marker still lets the handoff be taken"
assert_eq "$(git ls-remote "$T/strict.git" 'refs/sankofa/*' | grep -c . || :)" 0 "the letter is gone, and no marker"
echo "ok handoff git states"
