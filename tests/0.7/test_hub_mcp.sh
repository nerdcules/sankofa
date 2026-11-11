#!/bin/sh
# 0.7: a client that cannot run sankofad (a cloud session) reaches the hub over MCP streamable HTTP
. "$(dirname "$0")/lib.sh"
setup_two
tc=$("$SANKOFAD" hub token add cloud 2>/dev/null)
cd "$T/desk/repo"; SANKOFA_SESSION=s-desk dev a "$S" claim 'src/**' >/dev/null
out=$(python3 "$ROOT/tests/0.7/mcp_http.py" "$HUB/mcp" "$tc" '[
  ["note", {"repo": "demo", "text": "cloud run: dependency bumps pushed to routine/deps"}],
  ["claim", {"repo": "demo", "glob": "src/net.c"}],
  ["claim", {"repo": "demo", "glob": "docs/x.md"}],
  ["open", {"repo": "demo"}],
  ["ask", {"repo": "demo", "path": "src/a.c"}],
  ["note", {"repo": "../x", "text": "escape"}]
]')
assert_contains "$(printf '%s' "$out" | jq -r '.tools | join(",")')" "note,todo,awaiting,done,open,last,claim,release,ask,leases" "tools listed"
case $(printf '%s' "$out" | jq -r '."0:note".text') in e*) ;; *) fail "note returns an id: $out" ;; esac
assert_contains "$(printf '%s' "$out" | jq -r '."1:claim".text')" "held:" "the cloud's claim on desk's paths is refused"
assert_contains "$(printf '%s' "$out" | jq -r '."1:claim".text')" "s-desk" "naming the holder"
assert_contains "$(printf '%s' "$out" | jq -r '."2:claim".text')" "granted" "a free path is granted"
assert_contains "$(printf '%s' "$out" | jq -r '."4:ask".text')" "held" "ask through MCP"
assert_eq "$(printf '%s' "$out" | jq -r '."5:note".isError')" true "a bad repo is a tool error"
wait_for 3 '[ "$(count "$A" ".text|startswith(\"cloud run\")")" = 1 ]' || fail "desk's replica gets the cloud's note"
ev=$(jq -c 'select(.text|startswith("cloud run"))' "$A")
assert_eq "$(printf '%s' "$ev" | jq -r .device)" cloud "recorded with the cloud's device"
assert_eq "$(printf '%s' "$ev" | jq -r .client)" mcp:cloud-session "and its MCP client"
assert_eq "$(curl -s -o /dev/null -w '%{http_code}' -X POST -d '{}' "$HUB/mcp")" 401 "MCP needs a token too"
echo "ok hub mcp"
