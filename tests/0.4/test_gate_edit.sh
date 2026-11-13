#!/bin/sh
# Claude Code pre-edit gate: lease on first edit, deny a second holder, expiry, subagents, ends.
. "$(dirname "$0")/lib.sh"
$S init >/dev/null
start_daemon
F=$PWD/src/a.c; mkdir -p src

gate claude-code "$(edit_json A "$F")"
assert_eq "$RC" 0 "first edit: exit 0"; assert_eq "$OUT" "" "first edit: no output (normal permission flow)"
assert_contains "$($S ask src/a.c)" "held  src/a.c" "first edit leased the file"

# two holders, same path: second edit denied, naming the holder
gate claude-code "$(edit_json B "$F")"
assert_eq "$RC" 0 "deny is exit 0 with JSON"
denied || fail "second holder's edit not denied: $OUT"
assert_contains "$(reason)" "src/a.c is leased to A [main]" "reason names the holder"
gate claude-code "$(edit_json A "$F")"; assert_eq "$OUT" "" "holder keeps editing (renew)"

# a subagent of the same session inherits the parent's lease once the parent has left the file
# alone for handover_idle; the parent then waits for it
printf 'require_note=1\nhandover_idle=1s\n' > .sankofa/config
gate claude-code "$(edit_json A "$F" ag1 general-purpose)"
denied || fail "no handover while the parent may still be mid-edit"
sleep 1.1
gate claude-code "$(edit_json A "$F" ag1 general-purpose)"
assert_eq "$OUT" "" "subagent takes over the idle parent's lease"
gate claude-code "$(edit_json A "$F")"; denied || fail "parent must wait for its subagent"
assert_contains "$(reason)" "leased to A [subagent:general-purpose/ag1]" "names the subagent"
gate claude-code "$(edit_json A "$F" ag2 general-purpose)"; denied || fail "a sibling subagent is another holder"

# SubagentStop finishes that subagent's leases; the next editor gets a clean file. The hook runs
# in the session's own directory, which need not be this repo or any ledger repo at all.
mkdir -p "$TD/elsewhere"
(cd "$TD/elsewhere" && jq -nc '{session_id:"A",agent_id:"ag1",agent_type:"general-purpose",last_assistant_message:"done"}' | $S hook subagent-stop)
assert_contains "$($S ask src/a.c)" "free  src/a.c" "subagent stop finished its lease"
assert_eq "$($S ask src/a.c | grep -c dirty || true)" 0 "finished, not dirty"
gate claude-code "$(edit_json B "$F")"; assert_eq "$OUT" "" "B can edit now"

# SessionEnd finishes every lease of the session
gate claude-code "$(edit_json B "$PWD/src/b.c")"
(cd "$TD/elsewhere" && jq -nc '{session_id:"B",reason:"other",hook_event_name:"SessionEnd"}' | $S hook session-end)
assert_eq "$($S leases)" "no leases" "session end finished all of B's leases"

# expired lease: second edit allowed, and warned about the half-finished edit via the ledger
printf 'require_note=1\nlease_ttl=1s\n' > .sankofa/config
gate claude-code "$(edit_json C "$PWD/src/c.c")"
sleep 1.2
gate claude-code "$(edit_json D "$PWD/src/c.c")"
assert_eq "$OUT" "" "expired lease: second edit allowed"
assert_contains "$(tail -n 3 .sankofa/ledger.jsonl)" "expired" "the expiry is recorded"
printf 'require_note=1\n' > .sankofa/config

# not gated: other tools, files outside any ledger, gate=0
gate claude-code "$(edit_json B "$F" "" "" Read)"; assert_eq "$OUT" "" "Read is not gated"
mkdir -p "$T/../$$-outside" 2>/dev/null || :
gate claude-code "$(edit_json B /etc/hosts)"; assert_eq "$OUT" "" "file outside a ledger repo"
printf 'gate=0\n' >> .sankofa/config
gate claude-code "$(edit_json Z "$PWD/src/c.c")"; assert_eq "$OUT" "" "gate=0 turns it off"
grep -v '^gate=0$' .sankofa/config > .sankofa/config.tmp || :; mv .sankofa/config.tmp .sankofa/config
SANKOFA_GATE=0 gate claude-code "$(edit_json Z "$PWD/src/c.c")"; assert_eq "$OUT" "" "SANKOFA_GATE=0 turns it off"

# NotebookEdit uses notebook_path
nb=$(jq -nc --arg s Z --arg f "$PWD/src/c.c" --arg cwd "$PWD" '{session_id:$s,cwd:$cwd,tool_name:"NotebookEdit",tool_input:{notebook_path:$f}}')
gate claude-code "$nb"; denied || fail "NotebookEdit is gated too"
echo ok gate edit
