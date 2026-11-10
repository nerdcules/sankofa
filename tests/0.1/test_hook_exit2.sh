#!/bin/sh
. "$(dirname "$0")/lib.sh"
$S init >/dev/null

# previous session left an open item and stopped
SANKOFA_SESSION=old $S todo "finish the installer" >/dev/null
SANKOFA_SESSION=old $S note "handing off" 
stop_json old | $S hook stop
n=$(lines .sankofa/ledger.jsonl)

# new session stops with no note: blocked with a helpful message, nothing appended
err=$(stop_json new 2>&1 >/dev/null | $S hook stop 2>&1 >/dev/null) && fail "expected exit 2"
rc=$?
assert_eq "$rc" 2 "exit code 2"
assert_contains "$err" "1 open item" "message counts open items"
assert_contains "$err" "finish the installer" "message lists the item"
assert_eq "$(lines .sankofa/ledger.jsonl)" "$n" "blocked stop appends nothing"

# stop_hook_active true (already continued once): never block again
stop_json new true | $S hook stop; assert_eq "$?" 0 "stop_hook_active bypass"
assert_eq "$(lines .sankofa/ledger.jsonl)" $((n+1)) "bypassed stop is recorded"

# fresh session again, config off: allowed
out=$(stop_json new2 | SANKOFA_REQUIRE_NOTE=0 $S hook stop; echo "rc=$?")
assert_contains "$out" "rc=0" "env override allows"
printf 'require_note=0\n' > .sankofa/config
out=$(stop_json new3 | $S hook stop; echo "rc=$?")
assert_contains "$out" "rc=0" "config off allows"
printf 'require_note=1\n' > .sankofa/config

# after a note in this session: allowed
out=$(stop_json new4 | $S hook stop; echo "rc=$?")
assert_contains "$out" "rc=2" "still blocked before note"
SANKOFA_SESSION=new4 $S note "installer finished"
out=$(stop_json new4 | $S hook stop; echo "rc=$?")
assert_contains "$out" "rc=0" "note unblocks"

# resolving the only open item also unblocks a later session
$S done e1 >/dev/null
stop_json new5 | $S hook stop
out=$(stop_json new6 | $S hook stop; echo "rc=$?")
assert_contains "$out" "rc=0" "nothing open: allowed"
echo "ok hook exit2"
