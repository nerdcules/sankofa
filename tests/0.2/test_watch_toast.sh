#!/bin/sh
# One scan at a time (--once): exactly the right toasts, no backlog, no repeats.
. "$(dirname "$0")/lib.sh"
mkrepo alpha; mkrepo other
(cd alpha && $S note "history before watching" && $S watch add >/dev/null)
$W --once; assert_eq "$(toasts)" 0 "first scan: no backlog toasts"

(cd alpha && $S awaiting "sudo pacman -U pkg" >/dev/null)
$W --once
assert_eq "$(toasts)" 1 "one toast for one awaiting"
assert_eq "$(cut -f1 "$TOASTS")" critical "awaiting is urgent"
assert_eq "$(cut -f2 "$TOASTS")" "alpha: awaiting you" "awaiting title"
assert_eq "$(cut -f3 "$TOASTS")" "sudo pacman -U pkg" "awaiting body"
$W --once; assert_eq "$(toasts)" 1 "no repeat on rescan"

(cd alpha && $S note "built it")
$W --once; assert_eq "$(toasts)" 1 "notes do not toast by default"
(cd alpha && $S todo "later" >/dev/null && $S done e4 >/dev/null)
$W --once; assert_eq "$(toasts)" 1 "todo and done do not toast"

(cd other && $S awaiting "not watched" >/dev/null)
$W --once; assert_eq "$(toasts)" 1 "unregistered repo: no toast"

SANKOFA_TOAST_TYPES="awaiting note"; export SANKOFA_TOAST_TYPES
(cd alpha && $S note "loud now")
$W --once
assert_eq "$(toasts)" 2 "notes toast when asked for"
assert_eq "$(tail -n1 "$TOASTS" | cut -f2)" "alpha: note" "note title"
assert_eq "$(tail -n1 "$TOASTS" | cut -f3)" "[main] loud now" "note body names the writer"

# a burst becomes one summary toast that still names the latest awaiting item
(cd alpha && for i in 1 2 3 4 5 6; do $S note "n$i"; done && $S awaiting "log in to the registry" >/dev/null)
$W --once
unset SANKOFA_TOAST_TYPES
assert_eq "$(toasts)" 3 "burst: one summary toast"
last=$(tail -n1 "$TOASTS")
assert_eq "$(printf '%s' "$last" | cut -f2)" "alpha: 7 new entries" "summary title"
assert_contains "$last" "6 note" "summary counts"
assert_contains "$last" "latest awaiting: log in to the registry" "summary keeps the awaiting text"
assert_eq "$(printf '%s' "$last" | cut -f1)" critical "summary with awaiting is urgent"
echo ok watch toast
