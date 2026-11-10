#!/usr/bin/env python3
"""Accelerated week for sankofad: simulated weeks (sim/week.py: real CLI, real Stop hook,
real git trees) in several repos at once, every write going through one private sankofad
that a chaos thread kills with SIGKILL every few seconds and restarts after a pause. Sessions
also lease paths: claim their own file, sometimes a shared header, finish with done, and
sometimes crash and leave the lease to expire.

Checks, per run ("no lost notes"):
  every acknowledged note/todo/awaiting/done/stop is in the ledger exactly once, in order
  ledger ids are e1..eN with no gaps or repeats; no write nonce appears twice
  the daemon's database and each ledger.jsonl agree entry for entry
Measures: crashes, writes that went to the fallback, lease outcomes, ask latency via the CLI.

Usage: sim/daemon.py [--seeds 3] [--repos 3] [--crash-every 2,6] [--down 0.3,1.5] [--tmp DIR] [--out FILE]
"""
import argparse, collections, json, os, random, signal, sqlite3, statistics, subprocess, sys, tempfile, threading, time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import week  # noqa: E402

SANKOFAD = os.environ.get("SANKOFA_DAEMON_BIN", os.path.join(week.ROOT, "bin", "out", "sankofad"))
WRITES = ("note", "todo", "awaiting", "done")


class Daemon:
    def __init__(self, sock, db, log):
        self.sock, self.db, self.log, self.p, self.lock = sock, db, log, None, threading.Lock()
        self.crashes = 0

    def start(self):
        with self.lock:
            self.p = subprocess.Popen([SANKOFAD, "serve", "--socket", self.sock, "--db", self.db],
                                      stderr=open(self.log, "a"))
        for _ in range(500):
            r = subprocess.run([SANKOFAD, "send", "--socket", self.sock], input='{"verb":"ping"}',
                               capture_output=True, text=True)
            if r.returncode == 0:
                return
            time.sleep(0.01)
        raise RuntimeError("sankofad did not start")

    def kill(self, sig=signal.SIGKILL):
        with self.lock:
            if self.p and self.p.poll() is None:
                self.p.send_signal(sig); self.p.wait()
                self.crashes += sig == signal.SIGKILL


def one(seed, n_repos, crash_every, down, tmp):
    rng = random.Random(seed)
    base = tempfile.mkdtemp(prefix="sankofa-dsim-", dir=tmp)
    rundir = os.environ.get("XDG_RUNTIME_DIR") or tempfile.gettempdir()
    sock = os.path.join(rundir, f"sk-dsim-{os.getpid()}-{seed}.sock")
    d = Daemon(sock, os.path.join(base, "sankofad.db"), os.path.join(base, "sankofad.log"))
    d.start()

    stop_chaos = threading.Event()
    def chaos():
        while not stop_chaos.wait(rng.uniform(*crash_every)):
            d.kill()
            if stop_chaos.wait(rng.uniform(*down)):
                break
            d.start()
    chaos_t = threading.Thread(target=chaos, daemon=True)

    # every acknowledged write, per repo, in the order the repo's (single) thread made it
    acked = collections.defaultdict(list)
    lease = collections.Counter()
    ask_ms = []
    repo_dirs = {}
    orig_cli = week.Repo.cli

    def cli(self, session, *args, stdin=None):
        rc, out = orig_cli(self, session, *args, stdin=stdin)
        key = self.dir
        if rc == 0 and args and args[0] in WRITES:
            text = args[2] if args[0] == "done" else args[1]
            acked[key].append((args[0], session or "-", text if args[0] != "done" else (text if len(args) > 2 else "resolved")))
        elif rc == 0 and args[:2] == ("hook", "stop"):
            acked[key].append(("stop", session, "turn done"))
        elif rc == 0 and args[:2] == ("hook", "subagent-stop"):
            acked[key].append(("stop", session, "report"))
        return rc, out
    week.Repo.cli = cli

    held = collections.defaultdict(list)   # (repo, sid) -> lease ids

    def run_cli(repo, sid, *args, agent=None):
        env = repo.env(sid)
        if agent:
            env["SANKOFA_AGENT"] = agent
        return subprocess.run([week.CLI, *args], cwd=repo.dir, env=env, capture_output=True, text=True)

    def on_turn(repo, sid, kind, t, final):
        repo_dirs[repo.dir] = repo
        r = random.Random(hash((sid, t, seed)) & 0xffffffff)
        if kind in ("work", "finish", "fanout", "human-step"):
            targets = []
            if t == 0:
                targets.append(f"src/{sid}.txt")
            if r.random() < 0.3:
                targets.append("include/shared.h")
            for g in targets:
                p = run_cli(repo, sid, "claim", g, "4s")
                if p.returncode == 2:
                    lease["denied"] += 1
                elif "not recorded" in p.stderr:
                    lease["flagged (daemon down)"] += 1
                elif p.returncode == 0 and p.stdout:
                    lid, verb = p.stdout.split()[:2]
                    lease["renewed" if verb == "renewed" else "granted"] += 1
                    if "warning:" in p.stderr:
                        lease["granted over a half-finished edit"] += 1
                    if verb != "renewed":
                        held[(repo.dir, sid)].append(lid)
            t0 = time.perf_counter()
            run_cli(repo, sid, "ask", f"src/{sid}.txt")
            ask_ms.append((time.perf_counter() - t0) * 1000)
        if final:
            crashed = r.random() < 0.05   # the session dies: its leases are left to expire
            for lid in held.pop((repo.dir, sid), []):
                if crashed:
                    lease["left to expire (session crashed)"] += 1
                    continue
                p = run_cli(repo, sid, "done", lid, "lease work finished")
                if p.returncode == 0:
                    lease["done"] += 1
                    acked[repo.dir].append(("done", sid, "lease work finished"))
                else:
                    lease["done refused (expired or daemon down)"] += 1

    threads, results = [], {}
    def worker(i):
        s = seed * 100 + i
        results[i] = week.run("hook", week.plan_week(random.Random(s), 7), 0.5, s, base=base,
                              on_turn=on_turn, socket=sock, on_start=lambda repo: repo_dirs.setdefault(repo.dir, repo))
    snapshots = {}
    orig_cleanup = week.Repo.cleanup
    def cleanup(self):
        # let a live daemon import anything the fallback wrote, then keep the final ledger
        for _ in range(100):
            if subprocess.run([SANKOFAD, "send", "--socket", sock],
                              input=json.dumps({"verb": "sync", "repo": os.path.realpath(self.dir)}),
                              capture_output=True, text=True).returncode == 0:
                break
            time.sleep(0.05)
        snapshots[self.dir] = (os.path.realpath(self.dir),
                               [json.loads(l) for l in open(os.path.join(self.dir, ".sankofa", "ledger.jsonl"))])
        orig_cleanup(self)
    week.Repo.cleanup = cleanup

    chaos_t.start()
    t_start = time.time()
    for i in range(n_repos):
        th = threading.Thread(target=worker, args=(i,)); th.start(); threads.append(th)
    for th in threads[:-1]:
        th.join()
    stop_chaos.set(); chaos_t.join()
    if d.p.poll() is not None:
        d.start()
    threads[-1].join()
    elapsed = time.time() - t_start
    d.kill(signal.SIGTERM)
    week.Repo.cli, week.Repo.cleanup = orig_cli, orig_cleanup

    # ---- verify
    problems = []
    con = sqlite3.connect(d.db)
    fallback = 0
    for key, (real, entries) in snapshots.items():
        name = os.path.basename(key)
        ids = [e["id"] for e in entries]
        if ids != [f"e{i + 1}" for i in range(len(ids))]:
            problems.append(f"{name}: ids are not e1..e{len(ids)}")
        nonces = [e["nonce"] for e in entries if e.get("nonce")]
        dup = [n for n, c in collections.Counter(nonces).items() if c > 1]
        if dup:
            problems.append(f"{name}: {len(dup)} writes appear twice")
        want = [w for w in acked[key] if w[1] not in ("seed", "human")]
        got = [(e["type"], e["session"], e["text"]) for e in entries if e["type"] in WRITES + ("stop",)
               and e["session"] not in ("seed", "human") and e["agent"] != "sankofad"]
        if got != want:
            missing = collections.Counter(want) - collections.Counter(got)
            extra = collections.Counter(got) - collections.Counter(want)
            problems.append(f"{name}: {sum(missing.values())} acknowledged writes missing, {sum(extra.values())} extra")
        rows = con.execute("SELECT id, type, text, source FROM events WHERE repo=? ORDER BY seq", (real,)).fetchall()
        if [(r[0], r[1], r[2]) for r in rows] != [(e["id"], e["type"], e["text"]) for e in entries]:
            problems.append(f"{name}: database ({len(rows)}) and ledger.jsonl ({len(entries)}) disagree")
        fallback += sum(1 for r in rows if r[3] == "import")
    con.close()
    writes = sum(len(v) for v in acked.values())
    return dict(seconds=elapsed, crashes=d.crashes, writes=writes, fallback=fallback, problems=problems,
                lease=lease, ask=ask_ms, entries=sum(len(v[1]) for v in snapshots.values()))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seeds", type=int, default=3)
    ap.add_argument("--repos", type=int, default=3)
    ap.add_argument("--crash-every", default="2,6")
    ap.add_argument("--down", default="0.3,1.5")
    ap.add_argument("--tmp")
    ap.add_argument("--out")
    a = ap.parse_args()
    if not os.access(SANKOFAD, os.X_OK):
        sys.exit(f"build sankofad first: {SANKOFAD}")
    ce = tuple(float(x) for x in a.crash_every.split(",")); dn = tuple(float(x) for x in a.down.split(","))
    runs = []
    for seed in range(a.seeds):
        r = one(seed, a.repos, ce, dn, a.tmp)
        runs.append(r)
        print(f"seed {seed}: {r['writes']} acknowledged writes, {r['crashes']} crashes, "
              f"{r['fallback']} via fallback, problems: {r['problems'] or 'none'}", file=sys.stderr)
    n = len(runs)
    lease = collections.Counter()
    for r in runs:
        lease.update(r["lease"])
    ask = sorted(x for r in runs for x in r["ask"])
    lines = [f"# Accelerated week (sankofad): {n} seeds x {a.repos} repos x 7 days, "
             f"SIGKILL every {a.crash_every} s, down {a.down} s", "",
             "| measure | per run |", "|---|---|",
             f"| acknowledged writes (note, todo, awaiting, done, stop) | {sum(r['writes'] for r in runs) / n:.0f} |",
             f"| daemon crashes (SIGKILL) | {sum(r['crashes'] for r in runs) / n:.1f} |",
             f"| entries written by the fallback, imported later | {sum(r['fallback'] for r in runs) / n:.0f} |",
             f"| acknowledged writes lost or duplicated | {sum(len(r['problems']) for r in runs)} problems |",
             f"| wall time | {sum(r['seconds'] for r in runs) / n:.0f} s |", "",
             "| lease outcome | per run |", "|---|---|"]
    lines += [f"| {k} | {v / n:.1f} |" for k, v in sorted(lease.items())]
    lines += ["", f"`sankofa ask` through the CLI (sh + jq + sankofad send): median {statistics.median(ask):.1f} ms, "
              f"p99 {ask[int(len(ask) * 0.99) - 1]:.1f} ms over {len(ask)} calls"]
    probs = [p for r in runs for p in r["problems"]]
    if probs:
        lines += ["", "Problems:"] + [f"- {p}" for p in probs]
    report = "\n".join(lines) + "\n"
    print(report)
    if a.out:
        open(a.out, "w").write(report)
    sys.exit(1 if probs else 0)


if __name__ == "__main__":
    main()
