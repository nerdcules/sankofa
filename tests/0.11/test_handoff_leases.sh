#!/bin/sh
# 0.11: through a hub the sender's leases are on every device; a handoff finishes the ones its
# session holds, so the teammate who picks it up is not blocked by them until they expire
. "$(dirname "$0")/../0.7/lib.sh"
setup_two
as() { n=$1; shift; GIT_AUTHOR_NAME=$n GIT_AUTHOR_EMAIL=$n@x GIT_COMMITTER_NAME=$n GIT_COMMITTER_EMAIL=$n@x "$@"; }
git init -q --bare -b main "$T/remote.git"
cd "$T/desk/repo" && printf '.sankofa/\n' > .gitignore && git init -q -b main && git remote add origin "$T/remote.git" &&
  git add .gitignore && as Desk git commit -qm init && git push -q -u origin main 2>/dev/null
cd "$T/lap/repo" && printf '.sankofa/\n' > .gitignore && git init -q -b main && git remote add origin "$T/remote.git" &&
  git fetch -q origin && git reset -q --hard origin/main

cd "$T/desk/repo"
as Desk git switch -qc feat
SANKOFA_SESSION=s-desk dev a "$S" claim x.c 2h >/dev/null
SANKOFA_SESSION=s-desk dev a "$S" claim "docs/*" 2h >/dev/null
SANKOFA_SESSION=s-other dev a "$S" claim other.c 2h >/dev/null
echo x > x.c && git add x.c && as Desk git commit -qm x
cd "$T/lap/repo"
wait_for 3 '[ "$(dev b "$S" ask x.c | head -n1 | cut -c1-4)" = held ]' || fail "lap sees desk's lease"
cd "$T/desk/repo"
err=$(SANKOFA_SESSION=s-desk as Desk dev a "$S" handoff "x is in" 2>&1 >/dev/null)
assert_contains "$err" "finished 2 lease(s)" "the handoff finishes the session's two leases"
cd "$T/lap/repo"
wait_for 3 '[ "$(dev b "$S" ask x.c | head -n1 | cut -c1-4)" = free ]' || fail "x.c is free on lap: $(dev b "$S" ask x.c)"
assert_contains "$(dev b "$S" ask other.c)" "held" "another session's lease is left alone"
out=$(as Lap dev b "$S" pickup feat)
assert_contains "$out" "picked up feat" "lap picks it up"
assert_contains "$(dev b "$S" ask x.c)" "free" "and can edit x.c at once"
echo "ok handoff finishes leases"
