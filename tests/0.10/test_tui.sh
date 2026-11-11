#!/bin/sh
# 0.10: the terminal dashboard. summary with no terminal; then the real binary in a real
# terminal (tmux): panels, live updates, resolving an item through the CLI, help, about, quit
. "$(dirname "$0")/../0.1/lib.sh"
TUI=$ROOT/bin/out/sankofa-tui
[ -x "$TUI" ] || fail "build sankofa-tui first (make build)"
FX=$ROOT/dashboard/tests/fixtures
export XDG_CONFIG_HOME=$T/config

out=$("$TUI" summary "$FX/api.jsonl" "$FX/web.jsonl")
assert_contains "$out" "Waiting on you (2)" "summary: waiting"
assert_contains "$out" "Open (2)" "summary: open"
assert_contains "$out" "Leases (4)" "summary: leases"
assert_eq "$("$TUI" summary --json "$FX/api.jsonl" "$FX/web.jsonl" | jq '[.waiting, .todos, .leases, .alerts] | map(length) | join(",")' -r)" "2,2,4,2" "summary --json"
assert_eq "$("$TUI" summary --json "$FX/api.jsonl" | jq -r '.waiting[0].command')" "sankofa done e6" "a row carries its command"
mkdir "$T/empty"; set +e; err=$(cd "$T/empty" && "$TUI" 2>&1); rc=$?; set -e
assert_eq "$rc" 1 "no repo here and none watched"
assert_contains "$err" "sankofa watch add" "says how to get one"
mkdir -p "$T/config/sankofa" && printf '%s\n' "$FX/../fixtures" > "$T/config/sankofa/watched"

command -v tmux >/dev/null || { echo "ok tui (summary only: no tmux)"; exit 0; }
mkdir repo && cd repo && "$S" init >/dev/null
"$S" awaiting "approve the schema change" >/dev/null
"$S" todo "write the migration" >/dev/null
L=$T/repo/.sankofa/ledger.jsonl
TM="tmux -L sktui-$$"
screen() { $TM capture-pane -p; }
shows() { i=0; until screen | grep -qF -- "$1"; do i=$((i + 1)); [ $i -lt 60 ] || { screen >&2; fail "$2"; }; sleep 0.05; done; }
trap '$TM kill-server 2>/dev/null; on_exit' EXIT
$TM new-session -d -x 120 -y 32 "env SANKOFA_CLI=$S SANKOFA_SOCKET=$SANKOFA_SOCKET XDG_CONFIG_HOME=$XDG_CONFIG_HOME $TUI; echo EXIT=\$?; sleep 30"
shows "go back and fetch it" "the sankofa first"
shows "Waiting on you (1)" "the waiting panel"
shows "Open (1)" "the open panel"
shows "approve the schema change" "the awaiting item"
shows "F1  Help" "the status line"
shows "File  Repo  Item  View  Help" "the menu bar"
shows "live  1 repo  2 events" "the live line"
colour() { $TM capture-pane -p -e | grep -F -- "$1" | head -n1; }
lit() { colour "$1" | grep -qE '\[([0-9];)*7m'; }   # reverse video, alone or with bold
! lit "write the migration" || fail "a row there at start is not lit up"

"$S" todo "and the rollback" >/dev/null
shows "Open (2)" "a new todo appears without asking"
lit "and the rollback" || fail "a new row is lit up"
i=0; while lit "and the rollback"; do i=$((i + 1)); [ $i -lt 200 ] || fail "and fades"; sleep 0.05; done

$TM send-keys d; shows "How was it resolved?" "d asks how"
$TM send-keys -l "approved on the call"; $TM send-keys Enter
shows "Waiting on you (0)" "resolved through the CLI"
assert_eq "$(jq -r 'select(.type=="done") | .text' "$L")" "approved on the call" "the done is in the ledger, with the text"

$TM send-keys '?'; shows "zoom the panel" "? shows the keys"
$TM send-keys Escape; sleep 0.3   # a lone Esc, not the start of Alt+H
$TM send-keys M-h; sleep 0.2; $TM send-keys a; shows "go back and fetch it" "about, with the sankofa"
$TM send-keys Escape; sleep 0.3
$TM send-keys Tab; $TM send-keys F5; shows "write the migration" "zoom keeps the panel"
$TM send-keys q; shows "EXIT=0" "q quits cleanly"
echo "ok tui"
