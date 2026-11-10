#!/bin/sh
. "$(dirname "$0")/lib.sh"
mkrepo alpha; mkrepo beta; mkdir plain
assert_eq "$($S watch list)" "no watched repos" "empty list"
(cd alpha && $S watch add >/dev/null)
$S watch add beta/ >/dev/null
assert_contains "$($S watch add alpha)" "already watching" "no duplicate"
assert_eq "$($S watch list | grep -c .)" 2 "two watched"
mkdir -p alpha/src && $S watch add alpha/src >/dev/null
assert_eq "$($S watch list | grep -c .)" 2 "subdir resolves to the repo root"
$S watch add plain 2>/dev/null && fail "add without a ledger should fail"
$S watch rm beta >/dev/null
assert_eq "$($S watch list)" "$(cd -P alpha && pwd)" "rm leaves one"
$S watch rm beta 2>/dev/null && fail "rm twice should fail"
# a watched repo that disappears is listed as missing and can still be removed
$S watch add beta >/dev/null; b=$(cd -P beta && pwd); rm -rf beta
assert_contains "$($S watch list)" "ledger missing" "missing ledger flagged"
$S watch rm "$b" >/dev/null
assert_eq "$($S watch list | grep -c .)" 1 "removed by path"
echo ok watch cli
