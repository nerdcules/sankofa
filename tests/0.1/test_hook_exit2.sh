#!/bin/sh
. "$(dirname "$0")/lib.sh"
$S init >/dev/null

# previous session left an open item and stopped
SANKOFA_SESSION=old $S todo "finish the installer" >/dev/null
SANKOFA_SESSION=old $S note "handing off"
run_hook stop "$(stop_json old)"; assert_eq "$RC" 0 "old session stops cleanly"
n=$(lines .sankofa/ledger.jsonl)

# new session stops with no note: blocked with a helpful message, nothing appended
run_hook stop "$(stop_json new)"
assert_eq "$RC" 2 "exit code 2"
assert_contains "$ERR" "1 open item" "message counts open items"
assert_contains "$ERR" "finish the installer" "message lists the item"
assert_eq "$(lines .sankofa/ledger.jsonl)" "$n" "blocked stop appends nothing"

# stop_hook_active true (already continued once): never block again
run_hook stop "$(stop_json new true)"; assert_eq "$RC" 0 "stop_hook_active bypass"
assert_eq "$(lines .sankofa/ledger.jsonl)" $((n+1)) "bypassed stop is recorded"

# env override and config off: allowed
SANKOFA_REQUIRE_NOTE=0 run_hook stop "$(stop_json new2)"; assert_eq "$RC" 0 "env override allows"
printf 'require_note=0\n' > .sankofa/config
run_hook stop "$(stop_json new3)"; assert_eq "$RC" 0 "config off allows"
printf 'require_note=1\n' > .sankofa/config

# after a note in this session: allowed
run_hook stop "$(stop_json new4)"; assert_eq "$RC" 2 "still blocked before note"
SANKOFA_SESSION=new4 $S note "installer finished"
run_hook stop "$(stop_json new4)"; assert_eq "$RC" 0 "note unblocks"

# resolving the only open item also unblocks a later session
$S done e1 >/dev/null
run_hook stop "$(stop_json new5)"; assert_eq "$RC" 0 "done counts as activity"
run_hook stop "$(stop_json new6)"; assert_eq "$RC" 0 "nothing open: allowed"
echo "ok hook exit2"
