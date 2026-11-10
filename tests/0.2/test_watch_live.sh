#!/bin/sh
# The real watcher with inotify: a toast arrives within seconds; a restart catches up.
. "$(dirname "$0")/lib.sh"
command -v inotifywait >/dev/null || { echo "skip watch live: no inotifywait"; exit 0; }
mkrepo alpha; (cd alpha && $S watch add >/dev/null)
export SANKOFA_WATCH_TIMEOUT=3 SANKOFA_TOAST_TYPES="awaiting note"
wait_for() { # n seconds
  i=0; while [ "$(toasts)" -lt "$1" ] && [ $i -lt $(($2 * 10)) ]; do sleep 0.1; i=$((i + 1)); done
}
$W & PID=$!
i=0; while ! grep -q alpha "$XDG_STATE_HOME/sankofa/offsets" 2>/dev/null && [ $i -lt 50 ]; do sleep 0.1; i=$((i+1)); done
sleep 0.3
(cd alpha && $S awaiting "approve the merge" >/dev/null)
wait_for 1 2
assert_eq "$(toasts)" 1 "live toast within 2s"
assert_eq "$(cut -f2 "$TOASTS")" "alpha: awaiting you" "live title"

# a repo added while running is picked up
mkrepo beta; (cd beta && $S watch add >/dev/null)
i=0; while ! grep -q beta "$XDG_STATE_HOME/sankofa/offsets" 2>/dev/null && [ $i -lt 50 ]; do sleep 0.1; i=$((i+1)); done
sleep 0.3
(cd beta && $S note "beta works")
wait_for 2 2
assert_eq "$(toasts)" 2 "newly added repo toasts"

# service down (reboot): entries written meanwhile are toasted once on return
kill $PID; wait $PID 2>/dev/null || :
(cd alpha && $S awaiting "sudo while you were away" >/dev/null)
$W & PID=$!
wait_for 3 3
assert_eq "$(toasts)" 3 "catch-up after restart"
assert_contains "$(tail -n1 "$TOASTS")" "sudo while you were away" "catch-up text"
sleep 1; assert_eq "$(toasts)" 3 "no duplicates after catch-up"
echo ok watch live
