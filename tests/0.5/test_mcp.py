#!/usr/bin/env python3
"""0.5: drive `sankofad mcp` with a minimal MCP client. Same assertions as the 0.3 integration
test, through the tool interface; schemas validate; errors are tool errors, not crashes."""
import json, os, subprocess, sys, tempfile, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcp_client import McpClient, RpcError

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
D = os.environ.get("SANKOFA_DAEMON_BIN", os.path.join(ROOT, "bin", "out", "sankofad"))
CLI = os.path.join(ROOT, "bin", "sankofa")

def fail(msg):
    print("FAIL:", msg, file=sys.stderr); sys.exit(1)

def check(cond, msg):
    if not cond: fail(msg)

t = tempfile.mkdtemp(); td = tempfile.mkdtemp()
env = {k: v for k, v in os.environ.items() if not k.startswith(("CLAUDE_", "SANKOFA_"))}
sock = os.path.join(td, "d.sock")
env.update(SANKOFA_SOCKET=sock, SANKOFA_DAEMON_BIN=D, SANKOFA_CLI=CLI)
repo = os.path.join(t, "repo"); os.makedirs(os.path.join(repo, "src"))
subprocess.run([CLI, "init"], cwd=repo, env=env, check=True, capture_output=True)
daemon = subprocess.Popen([D, "serve", "--socket", sock, "--db", os.path.join(td, "d.db")], env=env, stderr=subprocess.DEVNULL)
for _ in range(200):
    if os.path.exists(sock): break
    time.sleep(0.01)
try:
    a = McpClient([D, "mcp"], repo, dict(env, SANKOFA_SESSION="claude-1"), name="claude-code")
    b = McpClient([D, "mcp"], repo, dict(env, SANKOFA_SESSION="codex-1"), name="codex")
    check(a.init["serverInfo"]["name"] == "sankofa", "server name")
    check("tools" in a.init["capabilities"], "tools capability")
    check(a.init.get("instructions", "").startswith("sankofa is"), "server instructions")

    # schemas: every tool has a well-formed object schema whose required keys are declared
    tools = {x["name"]: x for x in a.tools()}
    want = {"note", "todo", "awaiting", "done", "open", "last", "claim", "release", "ask", "leases"}
    check(set(tools) == want, f"tool names: {sorted(tools)}")
    try:
        import jsonschema
        meta = jsonschema.Draft202012Validator
    except ImportError:
        meta = None
    for name, x in tools.items():
        s = x["inputSchema"]
        check(s.get("type") == "object" and isinstance(s.get("properties"), dict), f"{name}: object schema")
        check(set(s.get("required", [])) <= set(s["properties"]), f"{name}: required keys are declared")
        check(x.get("description"), f"{name}: has a description")
        if meta:
            meta.check_schema(s)

    # same assertions as tests/0.3/test_daemon.sh, through MCP
    out, err = a.call("note", text="first note"); check(not err and out == "ok", f"note: {out!r}")
    out, err = a.call("todo", text="wire the gate"); check(not err and out == "e2", f"todo id: {out!r}")
    out, err = a.call("open"); check("wire the gate" in out, "open lists the todo")
    out, err = a.call("claim", glob="src/**", ttl="10m"); check(not err and "e3  held  src/**" in out, f"claim: {out!r}")
    out, err = b.call("claim", glob="src/net/socket.c")
    check(not err, "a denied claim is an answer, not a tool error")
    check("denied: src/net/socket.c overlaps" in out and "held by claude-1 [mcp:claude-code]" in out, f"contend: {out!r}")
    out, err = b.call("ask", path="src/net/socket.c"); check(out.startswith("held  src/net/socket.c  e3"), f"ask: {out!r}")
    out, err = a.call("claim", glob="src/**"); check("renewed" in out, "same holder renews")
    out, err = a.call("done", id="e3", text="src finished"); check(not err, f"done lease: {out!r}")
    out, err = b.call("leases"); check(out == "no leases", f"leases after done: {out!r}")
    out, err = b.call("awaiting", text="log in to the registry"); check(not err and out.startswith("e"), "awaiting id")
    out, err = a.call("last", n=2); check("awaiting" in out and "log in to the registry" in out, "last shows the awaiting")
    lines = [json.loads(l) for l in open(os.path.join(repo, ".sankofa", "ledger.jsonl"))]
    check({l["agent"] for l in lines if l["type"] in ("note", "todo", "claim", "awaiting")} == {"mcp:claude-code", "mcp:codex"},
          "both clients wrote to one ledger, each under its own name")

    # errors are tool errors, and the server keeps serving
    out, err = a.call("done", id="e99"); check(err and "no open item with id e99" in out, f"unknown id: {out!r}")
    out, err = a.call("note"); check(err and "'text' is required" in out, f"missing argument: {out!r}")
    out, err = a.call("no-such-tool"); check(err and "unknown tool" in out, f"unknown tool: {out!r}")
    out, err = a.call("note", text="x", repo="/nonexistent/place"); check(err, "a repo without a ledger is a tool error")
    try:
        a.request("no/such/method", {}); fail("unknown method should be a JSON-RPC error")
    except RpcError:
        pass
    out, err = a.call("open"); check(not err and "log in to the registry" in out, "server still answers after errors")

    # daemon down: MCP writes fall back to the ledger file like the CLI
    daemon.terminate(); daemon.wait()
    out, err = b.call("note", text="written while sankofad was down"); check(not err, f"fallback note: {out!r}")
    check("written while sankofad was down" in open(os.path.join(repo, ".sankofa", "ledger.jsonl")).read(), "fallback reached the file")
    a.close(); b.close()
    print("ok mcp")
finally:
    if daemon.poll() is None:
        daemon.terminate(); daemon.wait()
    subprocess.run(["rm", "-rf", t, td])
