#!/usr/bin/env python3
"""Minimal MCP streamable-HTTP client: initialize, tools/list, tools/call. Prints JSON results."""
import json, sys, urllib.request

url, token = sys.argv[1], sys.argv[2]
session = None


def rpc(method, params=None, id=1):
    global session
    body = {"jsonrpc": "2.0", "method": method, "params": params or {}}
    if id is not None: body["id"] = id
    h = {"Authorization": f"Bearer {token}", "Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
    if session: h["Mcp-Session-Id"] = session
    req = urllib.request.Request(url, data=json.dumps(body).encode(), headers=h, method="POST")
    with urllib.request.urlopen(req) as r:
        session = r.headers.get("Mcp-Session-Id") or session
        data = r.read()
        return json.loads(data) if data else None


init = rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "cloud-session", "version": "1"}})
assert init["result"]["serverInfo"]["name"] == "sankofa-hub", init
assert rpc("notifications/initialized", id=None) is None
tools = [t["name"] for t in rpc("tools/list", id=2)["result"]["tools"]]
out = {"tools": tools, "session": session}
for i, (name, args) in enumerate(json.loads(sys.argv[3])):
    r = rpc("tools/call", {"name": name, "arguments": args}, id=10 + i)["result"]
    out[f"{i}:{name}"] = {"text": r["content"][0]["text"], "isError": r["isError"]}
print(json.dumps(out))
