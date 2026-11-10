#!/usr/bin/env python3
"""A minimal MCP client: JSON-RPC 2.0 over stdio, newline-delimited, as the MCP stdio
transport specifies. Used by the 0.5 tests and sim/mcp.py."""
import json, subprocess, sys

PROTOCOL = "2025-06-18"


class McpClient:
    def __init__(self, argv, cwd, env=None, name="sankofa-test"):
        self.p = subprocess.Popen(argv, cwd=cwd, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.PIPE, text=True, bufsize=1)
        self.next_id = 0
        self.init = self.request("initialize", {"protocolVersion": PROTOCOL, "capabilities": {},
                                                "clientInfo": {"name": name, "version": "0"}})
        self.notify("notifications/initialized")

    def send(self, msg):
        self.p.stdin.write(json.dumps(msg) + "\n"); self.p.stdin.flush()

    def notify(self, method, params=None):
        self.send({"jsonrpc": "2.0", "method": method, **({"params": params} if params else {})})

    def request(self, method, params=None):
        self.next_id += 1
        rid = self.next_id
        self.send({"jsonrpc": "2.0", "id": rid, "method": method, **({"params": params} if params is not None else {})})
        while True:
            line = self.p.stdout.readline()
            if not line:
                raise RuntimeError("server closed stdout; stderr: " + self.p.stderr.read())
            msg = json.loads(line)
            if msg.get("id") == rid:
                if "error" in msg:
                    raise RpcError(msg["error"])
                return msg["result"]

    def tools(self):
        return self.request("tools/list", {})["tools"]

    def call(self, name, **args):
        r = self.request("tools/call", {"name": name, "arguments": args})
        return "".join(c.get("text", "") for c in r.get("content", [])), bool(r.get("isError"))

    def close(self):
        self.p.stdin.close()
        try:
            self.p.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.p.kill()


class RpcError(Exception):
    def __init__(self, err):
        super().__init__(err.get("message")); self.err = err
