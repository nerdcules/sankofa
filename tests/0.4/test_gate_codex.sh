#!/bin/sh
# Codex adapter: apply_patch headers name the files; same gate behind it.
. "$(dirname "$0")/lib.sh"
$S init >/dev/null
start_daemon
patch='*** Begin Patch
*** Update File: src/a.c
@@
-old
+new
*** Add File: src/new.c
+hello
*** Update File: src/old_name.c
*** Move to: src/new_name.c
*** Delete File: docs/gone.md
*** End Patch'
pj() { jq -nc --arg s "$1" --arg p "$patch" --arg cwd "$PWD" '{session_id:$s,cwd:$cwd,hook_event_name:"PreToolUse",tool_name:"apply_patch",tool_input:{command:$p}}'; }
gate codex "$(pj X)"; assert_eq "$OUT" "" "codex patch allowed"
assert_eq "$($S leases | grep -c 'X \[main\]')" 5 "every file in the patch leased"
gate codex "$(pj Y)"; denied || fail "second codex session denied"
assert_contains "$(reason)" "src/a.c is leased to X [main]" "codex deny names the holder"
gate claude-code "$(edit_json Z "$PWD/docs/gone.md")"; denied || fail "Claude Code sees the Codex lease"
gate codex "$(jq -nc '{session_id:"Y",tool_name:"Bash",tool_input:{command:"ls"}}')"; assert_eq "$OUT" "" "other codex tools pass"
echo ok gate codex
