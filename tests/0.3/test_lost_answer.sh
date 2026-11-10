#!/bin/sh
# A write whose answer is lost (sankofad send exits 4) is written by the fallback only if
# the daemon did not already put it in the ledger: never lost, never doubled.
. "$(dirname "$0")/../0.1/lib.sh"
$S init >/dev/null
python3 -c "import socket,sys; socket.socket(socket.AF_UNIX).bind(sys.argv[1])" "$TD/fake.sock"
export SANKOFA_SOCKET="$TD/fake.sock" SANKOFA_DAEMON_BIN="$TD/fake-sankofad"

# the daemon died before writing anything
cat > "$SANKOFA_DAEMON_BIN" <<'FAKE'
#!/bin/sh
cat >/dev/null; exit 4
FAKE
chmod +x "$SANKOFA_DAEMON_BIN"
$S note "answer lost, nothing written"
assert_eq "$(lines .sankofa/ledger.jsonl)" 1 "fallback wrote it once"
assert_eq "$(jq -r .text .sankofa/ledger.jsonl)" "answer lost, nothing written" "text kept"
[ -n "$(jq -r '.nonce // empty' .sankofa/ledger.jsonl)" ] || fail "fallback line carries the nonce"

# the daemon wrote the line, then died before answering
cat > "$SANKOFA_DAEMON_BIN" <<'FAKE'
#!/bin/sh
jq -c '{ts:"2026-11-10T00:00:00.000Z", id:"e2", type, session, agent, text, nonce}' >> .sankofa/ledger.jsonl
exit 4
FAKE
id=$($S todo "answer lost after the write")
assert_eq "$id" e2 "reports the id the daemon wrote"
assert_eq "$(lines .sankofa/ledger.jsonl)" 2 "not written a second time"

# done takes the same care
cat > "$SANKOFA_DAEMON_BIN" <<'FAKE'
#!/bin/sh
jq -c '{ts:"2026-11-10T00:00:01.000Z", id:"e3", type:"done", session, agent, text, ref, nonce}' >> .sankofa/ledger.jsonl
exit 4
FAKE
$S done e2 "finished"
assert_eq "$(lines .sankofa/ledger.jsonl)" 3 "done not doubled"
assert_eq "$($S open)" "no open items" "item resolved"
echo ok lost answer
