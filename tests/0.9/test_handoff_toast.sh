#!/bin/sh
# 0.9: a handoff toasts on the machines it is for, never on the one that wrote it
. "$(dirname "$0")/../0.2/lib.sh"
export SANKOFA_HUB_CONF=$T/hub.conf; printf 'device=desk\n' > "$SANKOFA_HUB_CONF"
mkrepo r; (cd r && "$S" watch add >/dev/null); $W --once
L=$T/r/.sankofa/ledger.jsonl
h() { jq -nc --arg s "$1" --arg d "$2" --arg t "$3" '{ts:"2026-11-11T00:00:00.000Z",id:"e9",type:"handoff",session:$s,agent:"main",text:$t,branch:"origin/feat",sha:"abc"} + (if $d != "" then {device:$d} else {} end)' >> "$L"; }
h s1 "" "my own, from the CLI";  $W --once; assert_eq "$(toasts)" 0 "the sender's own handoff (no hub) is quiet"
h s1 desk "my own, through the hub"; $W --once; assert_eq "$(toasts)" 0 "so is this device's own through a hub"
h sankofad "" "from Alice: parser"; $W --once; assert_eq "$(toasts)" 1 "one the bridge found toasts"
assert_eq "$(cut -f1 "$TOASTS")" critical "urgently"
assert_eq "$(cut -f2 "$TOASTS")" "r: handed off to you" "title"
assert_eq "$(cut -f3 "$TOASTS")" "from Alice: parser|sankofa pickup feat" "the letter and what to run"
h s2 lap "from lap"; $W --once; assert_eq "$(toasts)" 2 "another device's through the hub toasts"
echo "ok handoff toast"
