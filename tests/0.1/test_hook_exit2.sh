#!/bin/sh
# The Stop hook blocks only when this session changed something it has not written up.
. "$(dirname "$0")/lib.sh"
$S init >/dev/null
SANKOFA_SESSION=old $S todo "finish the installer" >/dev/null
TX=$T/tx.jsonl; : > "$TX"

# a reply that only answers a question, with an item open: never blocked
run_hook stop "$(stop_json_t ask "$TX")"; assert_eq "$RC" 0 "no edits: allowed"
tx_tool "$TX" Bash; tx_tool "$TX" Read
run_hook stop "$(stop_json_t ask "$TX")"; assert_eq "$RC" 0 "read-only tools: allowed"

# an Edit with no ledger write from this session: blocked, helpful message, nothing appended
n=$(lines .sankofa/ledger.jsonl)
TW=$T/work.jsonl; : > "$TW"; tx_tool "$TW" Edit
run_hook stop "$(stop_json_t work "$TW")"
assert_eq "$RC" 2 "edit without note: exit 2"
assert_contains "$ERR" "1 open item" "message counts open items"
assert_contains "$ERR" "1 file edit" "message says why"
assert_contains "$ERR" "finish the installer" "message lists the item"
assert_eq "$(lines .sankofa/ledger.jsonl)" "$n" "blocked stop appends nothing"

# stop_hook_active (already continued once): never block twice
run_hook stop "$(stop_json_t work "$TW" true)"; assert_eq "$RC" 0 "stop_hook_active bypass"

# a note after the edit unblocks; a later Edit blocks again; Write counts too
SANKOFA_SESSION=work $S note "installer half done"
run_hook stop "$(stop_json_t work "$TW")"; assert_eq "$RC" 0 "note after edit: allowed"
tx_tool "$TW" Write
run_hook stop "$(stop_json_t work "$TW")"; assert_eq "$RC" 2 "write after note: blocked"
SANKOFA_SESSION=work $S note "installer done"
run_hook stop "$(stop_json_t work "$TW")"; assert_eq "$RC" 0 "second note: allowed"

# edits to files outside this repo do not count; inside it they do
TF=$T/far.jsonl; : > "$TF"; tx_tool "$TF" Edit /somewhere/else/x.c
run_hook stop "$(stop_json_t far "$TF")"; assert_eq "$RC" 0 "edit outside the repo: allowed"
tx_tool "$TF" Write "$(pwd -P)/inside.c"
run_hook stop "$(stop_json_t far "$TF")"; assert_eq "$RC" 2 "edit inside the repo: blocked"
SANKOFA_SESSION=far $S note "inside.c"

# another session's note does not cover this session's edits
TO=$T/other.jsonl; : > "$TO"; tx_tool "$TO" MultiEdit
SANKOFA_SESSION=someone-else $S note "unrelated"
run_hook stop "$(stop_json_t other "$TO")"; assert_eq "$RC" 2 "someone else's note: still blocked"

# env override and config off
SANKOFA_REQUIRE_NOTE=0 run_hook stop "$(stop_json_t other "$TO")"; assert_eq "$RC" 0 "env override allows"
printf 'require_note=0\n' > .sankofa/config
run_hook stop "$(stop_json_t other "$TO")"; assert_eq "$RC" 0 "config off allows"
printf 'require_note=1\n' > .sankofa/config

# nothing open: allowed even with edits
$S done e1 >/dev/null
run_hook stop "$(stop_json_t other "$TO")"; assert_eq "$RC" 0 "nothing open: allowed"
echo "ok hook exit2"
