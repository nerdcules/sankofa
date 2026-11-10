#!/bin/sh
. "$(dirname "$0")/lib.sh"
$S init >/dev/null
[ -f .sankofa/ledger.jsonl ] || fail "init did not create ledger"

SANKOFA_SESSION=sess-A $S note "wired the thing"
id1=$($S todo "write the tests")
id2=$($S awaiting "sudo pacman -U sankofa.pkg")
assert_eq "$id1" e2 "todo id"
assert_eq "$id2" e3 "awaiting id"
assert_eq "$(lines .sankofa/ledger.jsonl)" 3 "three entries"

out=$($S open)
assert_contains "$out" "e2 " "open lists todo"
assert_contains "$out" "e3 " "open lists awaiting"
assert_eq "$(printf '%s\n' "$out" | grep -c .)" 2 "two open items"

$S done e2 "tests written"
out=$($S open)
assert_eq "$(printf '%s\n' "$out" | grep -c .)" 1 "one open after done"
assert_contains "$out" "e3 " "awaiting still open"

$S done e2 2>/dev/null && fail "double done should fail"
$S done e9 2>/dev/null && fail "unknown id should fail"

last=$($S last 1)
assert_contains "$last" "done" "last shows done entry"
assert_contains "$last" "(e2)" "last shows ref"
assert_eq "$(jq -r 'select(.id=="e1").session' .sankofa/ledger.jsonl)" sess-A "session recorded"

# nested dir still finds the ledger
mkdir -p src/deep && (cd src/deep && $S note "from deep") 
assert_eq "$(lines .sankofa/ledger.jsonl)" 5 "note from nested dir appended"
echo "ok cli"
