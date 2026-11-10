#!/bin/sh
. "$(dirname "$0")/lib.sh"
$S init >/dev/null

# no open items: stop is recorded, exit 0
stop_json s1 false "Finished the build." | $S hook stop; rc=$?
assert_eq "$rc" 0 "stop with nothing open exits 0"
assert_eq "$(lines .sankofa/ledger.jsonl)" 1 "one stop entry"
e=$(jq -c . .sankofa/ledger.jsonl)
assert_eq "$(printf '%s' "$e" | jq -r .type)" stop "type stop"
assert_eq "$(printf '%s' "$e" | jq -r .session)" s1 "session id from payload"
assert_eq "$(printf '%s' "$e" | jq -r .agent)" main "agent main"
assert_contains "$(printf '%s' "$e" | jq -r .text)" "Finished the build." "summary from last message"

# subagent stop records agent name and never blocks, even with open items
$S todo "something open" >/dev/null
jq -nc '{session_id:"s1",agent_name:"researcher",last_assistant_message:"Report: x"}' | $S hook subagent-stop; rc=$?
assert_eq "$rc" 0 "subagent stop exits 0 with open items"
assert_eq "$(tail -n1 .sankofa/ledger.jsonl | jq -r .agent)" "subagent:researcher" "subagent agent name"

# outside a sankofa repo: silent no-op
mkdir plain && (cd plain && stop_json s9 | $S hook stop); rc=$?
assert_eq "$rc" 0 "no ledger: exit 0"
[ ! -e plain/.sankofa ] || fail "hook must not create a ledger"
echo "ok hook stop"
