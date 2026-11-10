#!/usr/bin/env python3
"""Accelerated two-client run for the MCP server: several `sankofad mcp` servers, named as
Claude Code and Codex sessions, make random tool calls at once on one repo through a private
sankofad, which is SIGKILLed and restarted mid-run. Checks: every acknowledged note, todo,
awaiting and done is in the ledger exactly once; contention across clients is seen and
answered; no server dies; tool-call latency.

Usage: sim/mcp.py [--seeds 3] [--clients 4] [--calls 200] [--tmp DIR] [--out FILE]
"""
import argparse, collections, json, os, random, signal, statistics, subprocess, sys, tempfile, threading, time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, os.path.join(ROOT, "tests", "0.5"))
from mcp_client import McpClient  # noqa: E402

D = os.environ.get("SANKOFA_DAEMON_BIN", os.path.join(ROOT, "bin", "out", "sankofad"))
CLI = os.path.join(ROOT, "bin", "sankofa")
FILES = [f"src/m{i}.c" for i in range(6)] + ["include/shared.h"]


def one(seed, n_clients, calls, tmp):
    base = tempfile.mkdtemp(prefix="sankofa-msim-", dir=tmp)
    sock = os.path.join(os.environ.get("XDG_RUNTIME_DIR") or tempfile.gettempdir(), f"sk-msim-{os.getpid()}-{seed}.sock")
    env = {k: v for k, v in os.environ.items() if not k.startswith(("CLAUDE_", "SANKOFA_"))}
    env.update(SANKOFA_SOCKET=sock, SANKOFA_DAEMON_BIN=D, SANKOFA_CLI=CLI)
    repo = os.path.join(base, "repo"); os.makedirs(repo)
    subprocess.run([CLI, "init"], cwd=repo, env=env, check=True, capture_output=True)
    db = os.path.join(base, "d.db")
    dm = {"p": None}
    def start():
        dm["p"] = subprocess.Popen([D, "serve", "--socket", sock, "--db", db], env=env, stderr=subprocess.DEVNULL)
        for _ in range(300):
            if subprocess.run([D, "send", "--socket", sock], input='{"verb":"ping"}', capture_output=True, text=True).returncode == 0:
                return
            time.sleep(0.01)
    start()

    acked = collections.Counter()
    st = collections.Counter()
    lat = []
    lock = threading.Lock()
    clients = []
    for i in range(n_clients):
        name = "claude-code" if i % 2 == 0 else "codex"
        clients.append((f"{name}-{i}", McpClient([D, "mcp"], repo, dict(env, SANKOFA_SESSION=f"{name}-{i}"), name=name)))

    def work(session, c, r):
        mine_todo, mine_lease = [], []
        for k in range(calls):
            x = r.random()
            t0 = time.perf_counter()
            if x < 0.4:
                text = f"{session} note {k}"
                out, err = c.call("note", text=text)
                if not err: 
                    with lock: acked[("note", text)] += 1
            elif x < 0.5:
                text = f"{session} todo {k}"
                out, err = c.call("todo", text=text)
                if not err:
                    mine_todo.append(out.strip())
                    with lock: acked[("todo", text)] += 1
            elif x < 0.55:
                text = f"{session} awaiting {k}"
                out, err = c.call("awaiting", text=text)
                if not err:
                    with lock: acked[("awaiting", text)] += 1
            elif x < 0.62 and mine_todo:
                tid = mine_todo.pop(0); text = f"{session} closed {tid}"
                out, err = c.call("done", id=tid, text=text)
                if not err:
                    with lock: acked[("done", text)] += 1
            elif x < 0.82:
                out, err = c.call("claim", glob=r.choice(FILES), ttl="3s")
                with lock:
                    if out.startswith("denied"): st["claims denied (held by another client)"] += 1
                    elif "held" in out or "renewed" in out:
                        st["claims granted"] += 1
                        if "renewed" not in out: mine_lease.append(out.split()[0])
                    elif "not recorded" in out: st["claims flagged (daemon down)"] += 1
            elif x < 0.9:
                out, err = c.call("ask", path=r.choice(FILES))
                with lock: st["asks"] += 1
            elif mine_lease:
                out, err = c.call("done", id=mine_lease.pop(0), text=f"{session} finished a lease")
                if not err:
                    with lock: acked[("done", f"{session} finished a lease")] += 1
            else:
                out, err = c.call("open")
            with lock:
                lat.append((time.perf_counter() - t0) * 1000)
                st["tool errors"] += err

    rng = random.Random(seed)
    threads = [threading.Thread(target=work, args=(s, c, random.Random(rng.random()))) for s, c in clients]
    for t in threads: t.start()
    for _ in range(2):          # kill the daemon twice mid-run, restart after a pause
        time.sleep(rng.uniform(1.5, 3))
        dm["p"].send_signal(signal.SIGKILL); dm["p"].wait(); st["daemon kills"] += 1
        time.sleep(rng.uniform(0.3, 1.0)); start()
    for t in threads: t.join()
    alive = sum(c.p.poll() is None for _, c in clients)
    for _, c in clients: c.close()
    subprocess.run([D, "send", "--socket", sock], input=json.dumps({"verb": "sync", "repo": os.path.realpath(repo)}),
                   capture_output=True, text=True)
    dm["p"].terminate(); dm["p"].wait()

    entries = [json.loads(l) for l in open(os.path.join(repo, ".sankofa", "ledger.jsonl"))]
    got = collections.Counter((e["type"], e["text"]) for e in entries if e["type"] in ("note", "todo", "awaiting", "done"))
    problems = []
    for k, v in acked.items():
        if got[k] != v:
            problems.append(f"{k[0]} {k[1]!r}: acknowledged {v}, in ledger {got[k]}")
    if [e["id"] for e in entries] != [f"e{i + 1}" for i in range(len(entries))]:
        problems.append("ledger ids are not e1..eN")
    agents = {e["agent"] for e in entries if e["type"] == "note"}
    if agents != {"mcp:claude-code", "mcp:codex"}:
        problems.append(f"writers seen: {sorted(agents)}")
    if alive != n_clients:
        problems.append(f"{n_clients - alive} MCP servers died")
    st["acknowledged writes"] = sum(acked.values())
    return st, lat, problems


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seeds", type=int, default=3)
    ap.add_argument("--clients", type=int, default=4)
    ap.add_argument("--calls", type=int, default=200)
    ap.add_argument("--tmp")
    ap.add_argument("--out")
    a = ap.parse_args()
    tot, lat, probs = collections.Counter(), [], []
    for seed in range(a.seeds):
        st, l, p = one(seed, a.clients, a.calls, a.tmp)
        tot.update(st); lat += l; probs += p
        print(f"seed {seed}: {dict(st)} problems: {p or 'none'}", file=sys.stderr)
    n = a.seeds
    lat.sort()
    lines = [f"# Accelerated two-client run (MCP): {n} seeds x {a.clients} clients (half Claude Code, half Codex) "
             f"x {a.calls} tool calls, sankofad SIGKILLed twice a run", "", "| measure | per run |", "|---|---|"]
    lines += [f"| {k} | {v / n:.1f} |" for k, v in sorted(tot.items())]
    lines += [f"| acknowledged writes lost or doubled | {len(probs)} problems |", "",
              f"Tool call latency (MCP round trip, CLI behind it): median {statistics.median(lat):.1f} ms, "
              f"p99 {lat[int(len(lat) * 0.99) - 1]:.1f} ms over {len(lat)} calls"]
    if probs:
        lines += ["", "Problems:"] + [f"- {p}" for p in probs]
    report = "\n".join(lines) + "\n"
    print(report)
    if a.out:
        open(a.out, "w").write(report)
    sys.exit(1 if probs else 0)


if __name__ == "__main__":
    main()
