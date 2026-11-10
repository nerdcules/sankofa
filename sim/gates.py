#!/usr/bin/env python3
"""Accelerated multi-agent run for the 0.4 gates: several agents edit one repo at once,
every edit going through the real pre-edit gate (`sankofad gate claude-code`, real payloads)
against a private sankofad. Session S runs background subagents and its own main agent;
session T runs two more main agents. Files overlap, with a hot shared header.

An agent's edit is a read-modify-write with think time between the read and the write. A
clobber is a write onto a file someone else changed after this agent read it. Each task ends
with the real SubagentStop hook (which finishes the subagent's leases); some subagents crash
and leave their leases to expire. Every scenario runs twice: with the gate, and with
SANKOFA_GATE=0 as the control.

Usage: sim/gates.py [--seeds 3] [--tasks 40] [--tmp DIR] [--out FILE]
"""
import argparse, collections, json, os, random, signal, statistics, subprocess, sys, tempfile, threading, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLI = os.path.join(ROOT, "bin", "sankofa")
SANKOFAD = os.environ.get("SANKOFA_DAEMON_BIN", os.path.join(ROOT, "bin", "out", "sankofad"))
HANDOVER_IDLE = os.environ.get("SIM_HANDOVER_IDLE", "1s")
FILES = [f"src/mod{i}.c" for i in range(8)] + ["include/shared.h", "include/config.h", "README.md"]


def env_for(sock, gate_on):
    e = {k: v for k, v in os.environ.items() if not k.startswith(("CLAUDE_", "SANKOFA_"))}
    e.update(SANKOFA_SOCKET=sock, SANKOFA_DAEMON_BIN=SANKOFAD)
    if not gate_on:
        e["SANKOFA_GATE"] = "0"
    return e


def run(seed, tasks, gate_on, tmp, inherit_main_edits):
    base = tempfile.mkdtemp(prefix="sankofa-gsim-", dir=tmp)
    repo = os.path.join(base, "repo")
    os.makedirs(os.path.join(repo, "src")); os.makedirs(os.path.join(repo, "include"))
    for f in FILES:
        open(os.path.join(repo, f), "w").write("0\n")
    sock = os.path.join(os.environ.get("XDG_RUNTIME_DIR") or tempfile.gettempdir(), f"sk-gsim-{os.getpid()}-{seed}-{int(gate_on)}.sock")
    env = env_for(sock, gate_on)
    subprocess.run([CLI, "init"], cwd=repo, env=env, check=True, capture_output=True)
    with open(os.path.join(repo, ".sankofa", "config"), "a") as f:
        f.write("lease_ttl=2s\nhandover_idle=%s\n" % HANDOVER_IDLE)
    daemon = subprocess.Popen([SANKOFAD, "serve", "--socket", sock, "--db", os.path.join(base, "d.db")],
                              stderr=subprocess.DEVNULL, env=env)
    for _ in range(300):
        if subprocess.run([SANKOFAD, "send", "--socket", sock], input='{"verb":"ping"}', capture_output=True,
                          text=True).returncode == 0:
            break
        time.sleep(0.01)

    st = collections.Counter()
    gate_ms = []
    waits = []
    lock = threading.Lock()
    rng_master = random.Random(seed)

    def gate(session, path, agent_id=None):
        payload = {"session_id": session, "cwd": repo, "hook_event_name": "PreToolUse", "tool_name": "Edit",
                   "tool_input": {"file_path": os.path.join(repo, path), "old_string": "x", "new_string": "y"}}
        if agent_id:
            payload["agent_id"], payload["agent_type"] = agent_id, "general-purpose"
        t0 = time.perf_counter()
        r = subprocess.run([SANKOFAD, "gate", "claude-code"], input=json.dumps(payload), capture_output=True,
                           text=True, env=env)
        with lock:
            gate_ms.append((time.perf_counter() - t0) * 1000)
        return '"deny"' not in r.stdout

    def edit(path, who, r):
        full = os.path.join(repo, path)
        before = open(full).read()
        time.sleep(r.uniform(0.005, 0.03))           # the agent thinks between read and write
        now = open(full).read()
        if now != before:
            with lock:
                st["clobbers"] += 1
        open(full, "w").write(f"{int(before.split()[0]) + 1}\n{who}\n")
        with lock:
            st["edits"] += 1

    def agent(session, agent_id, r, crash):
        files = r.sample(FILES[:8], r.randint(1, 2)) + (["include/shared.h"] if r.random() < 0.35 else [])
        for _ in range(r.randint(3, 8)):
            path = r.choice(files)
            t0 = time.perf_counter()
            for attempt in range(40):
                if gate(session, path, agent_id):
                    break
                with lock:
                    st["denied"] += 1
                time.sleep(r.uniform(0.02, 0.08))
            else:
                with lock:
                    st["gave up"] += 1
                continue
            with lock:
                waits.append((time.perf_counter() - t0) * 1000)
            edit(path, agent_id or session, r)
        if agent_id and not crash:
            subprocess.run([CLI, "hook", "subagent-stop"], cwd=repo, env=env, capture_output=True,
                           input=json.dumps({"session_id": session, "agent_id": agent_id, "agent_type": "general-purpose",
                                             "last_assistant_message": "done"}), text=True)
        elif agent_id:
            with lock:
                st["crashed subagents"] += 1

    # S: background subagents in waves, plus S's own main agent editing meanwhile; T: two mains
    jobs = []
    for i in range(tasks):
        r = random.Random(rng_master.random())
        if i % 5 == 4:
            jobs.append(("T", None, r, False))
        elif i % 7 == 6 and inherit_main_edits:
            jobs.append(("S", None, r, False))
        else:
            jobs.append(("S", f"ag{i}", r, r.random() < 0.1))
    t_start = time.time()
    threads = []
    for j in jobs:
        th = threading.Thread(target=agent, args=j)
        th.start(); threads.append(th)
        time.sleep(rng_master.uniform(0.02, 0.15))
        threads = [t for t in threads if t.is_alive() or t.join() or False]
        while sum(t.is_alive() for t in threads) >= 5:
            time.sleep(0.01)
    for th in threads:
        th.join()
    elapsed = time.time() - t_start
    leases = subprocess.run([SANKOFAD, "send", "--socket", sock], capture_output=True, text=True,
                            input=json.dumps({"verb": "events", "repo": os.path.realpath(repo)})).stdout
    types = collections.Counter(e["type"] for e in json.loads(leases).get("entries", []))
    daemon.send_signal(signal.SIGTERM); daemon.wait()
    st["expired leases"] = types["expire"]
    st["handovers"] = types["release"]   # the only releases here: main handing a file to its subagent
    return st, gate_ms, waits, elapsed, types


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seeds", type=int, default=3)
    ap.add_argument("--tasks", type=int, default=40)
    ap.add_argument("--tmp")
    ap.add_argument("--out")
    a = ap.parse_args()
    rows = []
    for label, inherit in (("subagents of S + 2 mains of T", False), ("... + S's own main editing too", True)):
        for gate_on in (True, False):
            tot, gms, ws, secs = collections.Counter(), [], [], 0
            for seed in range(a.seeds):
                st, g, w, e, _ = run(seed, a.tasks, gate_on, a.tmp, inherit)
                tot.update(st); gms += g; ws += w; secs += e
            rows.append((label, gate_on, tot, gms, ws, secs))
            print(f"{label} gate={'on' if gate_on else 'off'}: {dict(tot)}", file=sys.stderr)
    n = a.seeds
    lines = [f"# Accelerated multi-agent run (gates): {n} seeds x {a.tasks} tasks, up to 5 agents at once", "",
             "| scenario | gate | edits | clobbers | denied attempts | gave up | expired leases | main-to-subagent handovers | median wait for a file |",
             "|---|---|---|---|---|---|---|---|---|"]
    for label, on, t, g, w, s in rows:
        lines.append(f"| {label} | {'on' if on else 'off'} | {t['edits'] / n:.0f} | {t['clobbers'] / n:.1f} | "
                     f"{t['denied'] / n:.0f} | {t['gave up'] / n:.1f} | {t['expired leases'] / n:.1f} | {t['handovers'] / n:.1f} | "
                     f"{statistics.median(w):.0f} ms |")
    g = sorted(x for r in rows if r[1] for x in r[3])
    lines += ["", f"Gate latency per edit (process, gate on): median {statistics.median(g):.1f} ms, "
              f"p99 {g[int(len(g) * 0.99) - 1]:.1f} ms over {len(g)} edits"]
    report = "\n".join(lines) + "\n"
    print(report)
    if a.out:
        open(a.out, "w").write(report)


if __name__ == "__main__":
    main()
