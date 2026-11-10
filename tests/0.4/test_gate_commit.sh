#!/bin/sh
# git pre-commit gate: refuses a path someone else holds, allows after release.
. "$(dirname "$0")/lib.sh"
git init -q . && git config user.email t@t && git config user.name t
$S init >/dev/null
export PATH="$(dirname "$SANKOFAD"):$PATH"
$S gate install >/dev/null
assert_contains "$($S gate status)" "installed" "gate installed"
start_daemon
mkdir -p src; echo 1 > src/a.c; echo 1 > src/b.c; git add -A; git commit -qm base

id=$(SANKOFA_SESSION=A $S claim src/a.c | cut -d' ' -f1)
echo 2 > src/a.c; git add src/a.c
set +e; err=$(unset CLAUDE_CODE_SESSION_ID; git commit -qm "touch a" 2>&1); rc=$?; set -e
[ "$rc" -ne 0 ] || fail "commit of a leased path went through"
assert_contains "$err" "src/a.c  leased to A [main] as $id" "refusal names the holder"
SANKOFA_SESSION=A git commit -qm "holder commits its own path" || fail "holder's own commit refused"

echo 2 > src/b.c; git add src/b.c; git commit -qm "unleased path" || fail "unleased path refused"
echo 3 > src/a.c; git add src/a.c
set +e; git commit -qm "still leased" 2>/dev/null; rc=$?; set -e
[ "$rc" -ne 0 ] || fail "still leased"
SANKOFA_SESSION=A $S release "$id"
git commit -qm "after release" || fail "commit refused after release"

# daemon down: commits are gated on it (refused), with a way through
id=$(SANKOFA_SESSION=A $S claim src/b.c | cut -d' ' -f1); SANKOFA_SESSION=A $S done "$id"
stop_daemon
echo 4 > src/a.c; git add src/a.c
set +e; err=$(git commit -qm "daemon down" 2>&1); rc=$?; set -e
[ "$rc" -ne 0 ] || fail "commit went through with the daemon down"
assert_contains "$err" "not answering" "says why"
SANKOFA_GATE=0 git commit -qm "override" || fail "SANKOFA_GATE=0 override refused"

# a foreign pre-commit hook is never overwritten
$S gate uninstall >/dev/null
printf '#!/bin/sh\nexit 0\n' > .git/hooks/pre-commit
$S gate install 2>/dev/null && fail "overwrote a foreign hook"
echo ok gate commit
