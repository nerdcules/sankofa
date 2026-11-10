#!/bin/sh
# Edits made through Bash leave no Edit/Write in the transcript; the git working tree catches them.
. "$(dirname "$0")/lib.sh"
git init -q . && git config user.email t@t && git config user.name t
echo tx.jsonl >> .git/info/exclude  # transcripts live outside real repos
echo one > a.txt && git add a.txt && git commit -qm init
$S init >/dev/null
$S todo "ship it" >/dev/null
TX=$T/tx.jsonl; : > "$TX"

run_hook stop "$(stop_json_t bash "$TX")"; assert_eq "$RC" 0 "first stop, clean tree: allowed"
echo two >> a.txt
run_hook stop "$(stop_json_t bash "$TX")"; assert_eq "$RC" 2 "tracked file changed by bash: blocked"
assert_contains "$ERR" "working tree changed" "message says why"
SANKOFA_SESSION=bash $S note "appended to a.txt"
run_hook stop "$(stop_json_t bash "$TX")"; assert_eq "$RC" 0 "note covers it"

echo new > b.txt
run_hook stop "$(stop_json_t bash "$TX")"; assert_eq "$RC" 2 "new untracked file: blocked"
SANKOFA_SESSION=bash $S note "added b.txt"
echo more >> b.txt
run_hook stop "$(stop_json_t bash "$TX")"; assert_eq "$RC" 2 "untracked file edited again: blocked"
SANKOFA_SESSION=bash $S note "b.txt grew"

git add -A && git commit -qm work
run_hook stop "$(stop_json_t bash "$TX")"; assert_eq "$RC" 2 "a commit is a change: blocked"
SANKOFA_SESSION=bash $S note "committed"

# a fresh session whose first turn edits through bash is caught on its first stop
echo three >> a.txt
run_hook stop "$(stop_json_t fresh "$TX")"; assert_eq "$RC" 2 "fresh session, bash edit: blocked"
SANKOFA_SESSION=fresh $S note "a.txt line three"

# ledger writes themselves never count as changes, tracked or not
SANKOFA_SESSION=bash $S note "again"
run_hook stop "$(stop_json_t bash "$TX")"; assert_eq "$RC" 0 "ledger writes are not edits"
echo ok hook gitedits
