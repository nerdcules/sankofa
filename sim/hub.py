#!/usr/bin/env python3
"""Accelerated week for the hub: one real sankofa-hub, two devices (desk and lap, each a real
sankofad with the real CLI and edit gate), and a cloud session speaking MCP over HTTP. Each
device reaches the hub through a proxy the simulation controls, so the network can fail the
way real ones do:

  cut        connections refused or reset (wifi off, VPN down)
  blackhole  connections accepted, nothing comes back (a dead route: no reset, just silence)

Each simulated day: sessions on both devices write notes, todos, awaiting items and dones,
claim paths through the edit gate, and end (finishing their leases); the laptop spends part of
the day offline (cut or blackhole); now and then the desktop drops too, the hub restarts, or
the laptop's sankofad is killed and the CLI falls back to the file.

Checks, per run:
  exact      every acknowledged write is on the hub exactly once (by nonce), whether it went
             straight there or was queued offline; no queued (q...) ids remain on any device
  converge   after the last day, desk, lap and the hub hold the same events in the same order
  offline    every offline claim on a path another device held is flagged on the hub
  latency    edit gate through the hub (online), and while offline (allow-and-flag)

Usage: sim/hub.py [--seeds 3] [--days 7] [--out FILE]
"""
import argparse, json, os, random, shutil, signal, socket, statistics, subprocess, sys, tempfile, threading, time, urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
S = os.path.join(ROOT, "bin", "sankofa")
D = os.environ.get("SANKOFA_DAEMON_BIN", os.path.join(ROOT, "bin", "out", "sankofad"))
H = os.path.join(ROOT, "bin", "out", "sankofa-hub")
PATHS = [f"src/{m}/{f}.c" for m in ("net", "ui", "db", "core") for f in ("a", "b", "c")] + ["docs/plan.md", "README.md"]


def free_port():
    s = socket.socket(); s.bind(("127.0.0.1", 0)); p = s.getsockname()[1]; s.close(); return p


class Proxy:
    """TCP proxy in front of the hub: up, cut (refuse and reset) or blackhole (accept, never answer)."""

    def __init__(self, target):
        self.target, self.port, self.mode = target, free_port(), "up"
        self.conns, self.lock, self.lsock = [], threading.Lock(), None
        self.listen()

    def listen(self):
        self.lsock = socket.socket(); self.lsock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.lsock.bind(("127.0.0.1", self.port)); self.lsock.listen(64)
        threading.Thread(target=self.accept, args=(self.lsock,), daemon=True).start()

    def accept(self, ls):
        while True:
            try: c, _ = ls.accept()
            except OSError: return
            if self.mode == "blackhole":
                with self.lock: self.conns.append(c)
                continue
            try: u = socket.create_connection(("127.0.0.1", self.target), timeout=2)
            except OSError: c.close(); continue
            with self.lock: self.conns += [c, u]
            for a, b in ((c, u), (u, c)): threading.Thread(target=self.pipe, args=(a, b), daemon=True).start()

    def pipe(self, a, b):
        try:
            while True:
                d = a.recv(65536)
                if not d: break
                if self.mode == "up": b.sendall(d)
                elif self.mode == "blackhole": continue   # swallowed: the sender hears nothing back
                else: break
        except OSError: pass
        for s in (a, b):
            try: s.close()
            except OSError: pass

    def drop_all(self):
        with self.lock:
            for c in self.conns:
                try: c.close()
                except OSError: pass
            self.conns = []

    def set(self, mode):
        if mode == self.mode: return
        if self.mode == "cut": self.listen()
        self.mode = mode
        if mode == "cut":
            self.lsock.close(); self.drop_all()
        elif mode == "blackhole":
            # established connections go silent: stop forwarding without closing (a dead route)
            pass
        else:
            self.drop_all()


class Device:
    def __init__(self, name, base, hub_port, token, env):
        self.name, self.base = name, base
        self.proxy = Proxy(hub_port)
        self.sock, self.db = os.path.join(base, f"{name}.sock"), os.path.join(base, f"{name}.db")
        self.conf = os.path.join(base, f"{name}.conf")
        self.repo = os.path.join(base, name, "repo"); os.makedirs(self.repo)
        self.env = dict(env, SANKOFA_SOCKET=self.sock, SANKOFA_HUB_CONF=self.conf)
        self.p = None
        self.log = open(os.path.join(base, f"{name}.log"), "a")
        self.cli("hub", "join", f"http://127.0.0.1:{self.proxy.port}", token, name)

    def start(self):
        self.p = subprocess.Popen([D, "serve", "--socket", self.sock, "--db", self.db], env=self.env, stderr=self.log)
        for _ in range(300):
            if subprocess.run([D, "send", "--socket", self.sock], input='{"verb":"ping"}', text=True, capture_output=True).returncode == 0: return
            time.sleep(0.01)
        raise SystemExit(f"sankofad {self.name} did not start")

    def kill(self):
        self.p.send_signal(signal.SIGKILL); self.p.wait(); self.p = None

    def stop(self):
        if self.p: self.p.send_signal(signal.SIGTERM); self.p.wait(); self.p = None

    def cli(self, *a, session="-", env=None):
        e = dict(self.env, SANKOFA_SESSION=session, **(env or {}))
        return subprocess.run([S, *a], cwd=self.repo, env=e, capture_output=True, text=True, timeout=30)

    def gate(self, session, path):
        payload = json.dumps({"session_id": session, "cwd": self.repo, "hook_event_name": "PreToolUse", "tool_name": "Edit",
                              "tool_input": {"file_path": os.path.join(self.repo, path), "old_string": "a", "new_string": "b"}})
        t = time.perf_counter()
        r = subprocess.run([D, "gate", "claude-code"], input=payload, env=self.env, capture_output=True, text=True)
        return (time.perf_counter() - t) * 1000, ("deny" in r.stdout)

    def ledger(self):
        with open(os.path.join(self.repo, ".sankofa", "ledger.jsonl")) as f:
            return [json.loads(l) for l in f if l.strip()]


def mcp(port, token, calls):
    sess = None
    def rpc(method, params, id):
        nonlocal sess
        h = {"Authorization": f"Bearer {token}", "Content-Type": "application/json"}
        if sess: h["Mcp-Session-Id"] = sess
        body = {"jsonrpc": "2.0", "method": method, "params": params}
        if id is not None: body["id"] = id
        with urllib.request.urlopen(urllib.request.Request(f"http://127.0.0.1:{port}/mcp", json.dumps(body).encode(), h), timeout=5) as r:
            sess = r.headers.get("Mcp-Session-Id") or sess
            d = r.read()
            return json.loads(d) if d else None
    rpc("initialize", {"protocolVersion": "2025-06-18", "clientInfo": {"name": "cloud-routine"}, "capabilities": {}}, 1)
    return [rpc("tools/call", {"name": n, "arguments": a}, 10 + i)["result"] for i, (n, a) in enumerate(calls)]


def one(seed, days, tmp):
    rnd = random.Random(seed)
    base = tempfile.mkdtemp(prefix=f"hub{seed}-", dir=tmp)
    env = dict(os.environ, SANKOFA_HUB_TOKENS=os.path.join(base, "tokens"), SANKOFA_HUB_DATA=os.path.join(base, "hubdata"),
               SANKOFA_HUB_BREAKER="0.3", SANKOFA_HUB_RETRY_MAX="0.5", SANKOFA_HUB_TIMEOUT="1", SANKOFA_HUB_SILENCE="1.5",
               SANKOFA_HUB_KEEPALIVE="0.3", SANKOFA_RESCAN="3600")
    for k in ("CLAUDE_CODE_SESSION_ID", "SANKOFA_NO_DAEMON", "SANKOFA_AGENT", "SANKOFA_REQUIRE_NOTE"): env.pop(k, None)
    hub_port = free_port()
    tok = lambda d: subprocess.run([H, "token", "add", d], env=env, capture_output=True, text=True).stdout.strip()
    tokens = {d: tok(d) for d in ("desk", "lap", "cloud")}
    hub = {"p": None}
    hlog = open(os.path.join(base, "hub.log"), "a")

    def hub_start():
        hub["p"] = subprocess.Popen([H, "serve", "--listen", f"http://127.0.0.1:{hub_port}/", "--db", os.path.join(base, "hub.db")],
                                    env=env, stderr=hlog)
        for _ in range(300):
            try: urllib.request.urlopen(f"http://127.0.0.1:{hub_port}/healthz", timeout=1); return
            except OSError: time.sleep(0.02)
        raise SystemExit("hub did not start")

    def hub_stop():
        hub["p"].send_signal(signal.SIGTERM); hub["p"].wait()

    hub_start()
    desk = Device("desk", base, hub_port, tokens["desk"], env)
    lap = Device("lap", base, hub_port, tokens["lap"], env)
    devs = [desk, lap]
    for d in devs:
        d.start()
        d.cli("init"); r = d.cli("hub", "on", "github.com/me/project")
        assert r.returncode == 0, r.stderr

    acked = {}           # nonce-less writes are matched by their unique text
    st = dict(writes=0, queued=0, claims=0, denied=0, offline_claims=0, gate_online=[], gate_offline=[], cuts=0,
              blackholes=0, hub_restarts=0, daemon_kills=0, mcp_calls=0)
    open_items = []      # (text, device) of todos/awaiting that were acknowledged
    n = 0
    t_start = time.time()

    def write(d, kind, session):
        nonlocal n
        n += 1
        text = f"s{seed} {d.name} {kind} {n}"
        if kind == "done":
            if not open_items: return
            i = rnd.randrange(len(open_items)); t, _ = open_items[i]
            ids = [e["id"] for e in d.ledger() if e.get("text") == t]
            if not ids: return
            r = d.cli("done", ids[-1], text, session=session)
            if r.returncode == 0: open_items.pop(i)
        else:
            r = d.cli(kind, text, session=session)
            if r.returncode == 0 and kind in ("todo", "awaiting"): open_items.append((text, d.name))
        if r.returncode == 0:
            acked[text] = d.name; st["writes"] += 1
            if not d.proxy.mode == "up" or d.p is None: st["queued"] += 1

    for day in range(days):
        # the laptop's day: some of it offline
        lap_off = rnd.random() < 0.8
        off_at, on_at = rnd.randint(10, 40), rnd.randint(50, 80)
        desk_blip = rnd.random() < 0.3
        restart_hub = rnd.random() < 0.25
        kill_lap = rnd.random() < 0.2
        for step in range(90):
            if lap_off and step == off_at:
                mode = rnd.choice(["cut", "blackhole"]); lap.proxy.set(mode); st["cuts" if mode == "cut" else "blackholes"] += 1
            if lap_off and step == on_at: lap.proxy.set("up")
            if desk_blip and step == 30: desk.proxy.set("cut"); st["cuts"] += 1
            if desk_blip and step == 36: desk.proxy.set("up")
            if restart_hub and step == 60: hub_stop(); st["hub_restarts"] += 1
            if restart_hub and step == 63: hub_start()
            if kill_lap and step == 20 and lap.p: lap.kill(); st["daemon_kills"] += 1
            if kill_lap and step == 26 and not lap.p: lap.start()
            d = rnd.choice(devs)
            session = f"{d.name}-d{day}-{rnd.randint(0, 2)}"
            r = rnd.random()
            if r < 0.35: write(d, rnd.choice(["note", "note", "todo", "awaiting"]), session)
            elif r < 0.5: write(d, "done", session)
            elif r < 0.85:
                path = rnd.choice(PATHS)
                offline = d.proxy.mode != "up" or hub["p"].poll() is not None
                if d.p is None: continue
                ms, denied = d.gate(session, path)
                st["claims"] += 1; st["denied"] += denied
                (st["gate_offline"] if offline else st["gate_online"]).append(ms)
                if offline: st["offline_claims"] += 1
            elif r < 0.92:
                if d.p: subprocess.run([D, "send", "--socket", d.sock], input=json.dumps({"verb": "finish", "session": session, "text": "session ended"}),
                                       text=True, capture_output=True)
            else:
                if hub["p"].poll() is None:
                    try:
                        mcp(hub_port, tokens["cloud"], [["note", {"repo": "github.com/me/project", "text": f"s{seed} cloud note {n}"}],
                                                        ["claim", {"repo": "github.com/me/project", "glob": rnd.choice(PATHS)}]])
                        acked[f"s{seed} cloud note {n}"] = "cloud"; st["mcp_calls"] += 2; n += 1
                    except OSError: pass
        for dv in devs: dv.proxy.set("up")
        if not lap.p: lap.start()
        print(f"  seed {seed} day {day}: {st['writes']} writes, {st['claims']} claims, {time.time() - t_start:.0f} s", file=sys.stderr)

    # quiesce: everyone online, wait for the queues to drain and the replicas to catch up
    hub_file = os.path.join(base, "hubdata", "repos", "github.com", "me", "project", ".sankofa", "ledger.jsonl")
    def hub_ledger():
        with open(hub_file) as f: return [json.loads(l) for l in f if l.strip()]
    deadline = time.time() + 30
    while time.time() < deadline:
        hl = [e["id"] for e in hub_ledger()]
        if all([e["id"] for e in d.ledger()] == hl for d in devs): break
        time.sleep(0.2)
    hl = hub_ledger()
    texts = [e.get("text") for e in hl]
    res = dict(st)
    res["acknowledged"] = len(acked)
    res["lost"] = sum(1 for t in acked if texts.count(t) == 0)
    for t in acked:
        if texts.count(t) == 0: print(f"  LOST {t} ({acked[t]})", file=sys.stderr)
    res["doubled"] = sum(1 for t in acked if texts.count(t) > 1)
    res["q ids left"] = sum(1 for d in devs for e in d.ledger() if e["id"].startswith("q"))
    res["diverged"] = sum(1 for d in devs if [e["id"] for e in d.ledger()] != [e["id"] for e in hl])
    queued = [e for e in hl if e["type"] in ("claim", "renew", "contend") and "was offline)" in e.get("text", "")]
    contends = [e for e in queued if e["type"] == "contend"]
    flags = [e for e in hl if e["type"] == "flag" and "without a lease" in e.get("text", "")]
    res["queued claims replayed"] = len(queued)
    res["offline claims on held paths"] = len(contends)
    res["flagged"] = len(flags)
    res["hub events"] = len(hl)
    res["devices on events"] = sorted({e.get("device", "-") for e in hl})
    for d in devs: d.stop()
    hub_stop()
    shutil.rmtree(base, ignore_errors=True)
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seeds", type=int, default=3)
    ap.add_argument("--days", type=int, default=7)
    ap.add_argument("--out")
    a = ap.parse_args()
    tmp = tempfile.mkdtemp(prefix="sk-hub-", dir=os.environ.get("XDG_RUNTIME_DIR") or "/tmp")
    t0 = time.time()
    runs = []
    for s in range(a.seeds):
        r = one(s, a.days, tmp)
        print(f"seed {s}: " + ", ".join(f"{k} {v if not isinstance(v, list) or not v or isinstance(v[0], str) else len(v)}" for k, v in r.items()), file=sys.stderr)
        runs.append(r)
    shutil.rmtree(tmp, ignore_errors=True)
    tot = lambda k: sum(r[k] for r in runs)
    allv = lambda k: [x for r in runs for x in r[k]]
    q = lambda v, p: sorted(v)[min(len(v) - 1, int(len(v) * p))] if v else float("nan")
    rows = [("acknowledged writes (CLI, gate flags, MCP)", tot("acknowledged")), ("of those written while offline", tot("queued")),
            ("lost", tot("lost")), ("doubled", tot("doubled")), ("queued ids left on a device", tot("q ids left")),
            ("devices whose replica differs from the hub", tot("diverged")),
            ("edit-gate claims", tot("claims")), ("denied (another holder, any device)", tot("denied")),
            ("claims made while the sim had the network down", tot("offline_claims")),
            ("queued claims replayed on the hub", tot("queued claims replayed")),
            ("of those, on paths another holder had meanwhile", tot("offline claims on held paths")),
            ("flagged on the hub", tot("flagged")),
            ("network cuts", tot("cuts")), ("blackholes (silent dead routes)", tot("blackholes")),
            ("hub restarts", tot("hub_restarts")), ("laptop sankofad killed", tot("daemon_kills")),
            ("cloud MCP calls", tot("mcp_calls")), ("hub events", tot("hub events"))]
    on, off = allv("gate_online"), allv("gate_offline")
    out = [f"# Accelerated week (hub): {a.seeds} seeds x {a.days} days, desk + lap + a cloud MCP session", "",
           "| measure | total |", "|---|---|"] + [f"| {k} | {v} |" for k, v in rows] + ["",
           f"Edit gate through the hub, online: median {statistics.median(on):.1f} ms, p99 {q(on, .99):.1f} ms over {len(on)} edits",
           f"Edit gate offline (allow-and-flag): median {statistics.median(off):.1f} ms, p99 {q(off, .99):.1f} ms over {len(off)} edits" if off else "",
           "", f"Wall time {time.time() - t0:.0f} s"]
    text = "\n".join(out) + "\n"
    print(text)
    if a.out:
        with open(a.out, "w") as f: f.write(text)
    sys.exit(1 if tot("lost") + tot("doubled") + tot("q ids left") + tot("diverged")
             + abs(tot("flagged") - tot("offline claims on held paths")) else 0)


if __name__ == "__main__":
    main()
