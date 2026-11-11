#!/usr/bin/env python3
"""Accelerated nights for the remote bridge: real sankofad, real git, real sankofa-watch
(with a fake notify-send that records toasts). Each simulated night:

  evening  the human pushes their own work (and sometimes commits more on top before the
           next fetch), and the laptop sleeps: sankofad is frozen
           (SIGSTOP) or, some nights, shut down (a reboot)
  night    0-6 scheduled routines push to a bare remote: new branches, more commits on
           yesterday's branch, now and then a force push
  morning  the laptop wakes (SIGCONT or a fresh sankofad); some mornings the network is
           down for the first fetch; the human sometimes runs their own git fetch first

Checks, per night: every branch the routines moved has exactly one landed event, with the
branch and the sha it ended the night at; the human's own pushes have none; the watcher
toasts every landing (one by one, or one summary for a burst).

Usage: sim/overnight.py [--seeds 3] [--nights 7] [--out FILE]
"""
import argparse, json, os, random, shutil, signal, statistics, subprocess, sys, tempfile, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
S = os.path.join(ROOT, "bin", "sankofa")
D = os.environ.get("SANKOFA_DAEMON_BIN", os.path.join(ROOT, "bin", "out", "sankofad"))
WATCH = os.path.join(ROOT, "bin", "sankofa-watch")
FAKE = """#!/bin/sh
urg=normal
while [ $# -gt 2 ]; do case $1 in -u) urg=$2; shift 2 ;; -a) shift 2 ;; *) shift ;; esac; done
python3 -c 'import json,sys; print(json.dumps({"u":sys.argv[1],"title":sys.argv[2],"body":sys.argv[3]}))' "$urg" "$1" "$2" >> "$TOASTS"
"""


def git(cwd, *a, check=True, who="Routine"):
    return subprocess.run(["git", "-c", f"user.name={who}", "-c", f"user.email={who.lower()}@x", *a], cwd=cwd,
                          check=check, capture_output=True, text=True).stdout.strip()


def ledger(repo):
    with open(os.path.join(repo, ".sankofa", "ledger.jsonl")) as f:
        return [json.loads(l) for l in f if l.strip()]


class Daemon:
    def __init__(self, tmp, env):
        self.sock, self.db, self.env, self.p = os.path.join(tmp, "d.sock"), os.path.join(tmp, "d.db"), env, None
        self.log = open(os.path.join(tmp, "d.log"), "a")

    def start(self):
        self.p = subprocess.Popen([D, "serve", "--socket", self.sock, "--db", self.db], env=self.env, stderr=self.log)
        for _ in range(300):
            if subprocess.run([D, "send", "--socket", self.sock], input='{"verb":"ping"}', text=True,
                              capture_output=True).returncode == 0:
                return
            time.sleep(0.01)
        raise SystemExit("sankofad did not start")

    def stop(self):
        self.p.send_signal(signal.SIGTERM); self.p.wait()


def one(seed, nights, tmp):
    rnd = random.Random(seed)
    base = tempfile.mkdtemp(prefix=f"night{seed}-", dir=tmp)
    fakebin = os.path.join(base, "bin"); os.makedirs(fakebin)
    with open(os.path.join(fakebin, "notify-send"), "w") as f: f.write(FAKE)
    os.chmod(os.path.join(fakebin, "notify-send"), 0o755)
    toasts = os.path.join(base, "toasts.jsonl"); open(toasts, "w").close()
    env = dict(os.environ, SANKOFA_SOCKET=os.path.join(base, "d.sock"), SANKOFA_BRIDGE_TICK="0.1",
               SANKOFA_BRIDGE_RETRY="0.3", SANKOFA_RESCAN="3600", TOASTS=toasts,
               XDG_CONFIG_HOME=os.path.join(base, "config"), XDG_STATE_HOME=os.path.join(base, "state"),
               XDG_DATA_HOME=os.path.join(base, "data"), PATH=fakebin + ":" + os.environ["PATH"])
    for k in ("XDG_BIN_HOME", "CLAUDE_CODE_SESSION_ID", "SANKOFA_NO_DAEMON"): env.pop(k, None)
    remote, gone = os.path.join(base, "remote.git"), os.path.join(base, "remote.offline")
    repo, routine = os.path.join(base, "repo"), os.path.join(base, "routine")
    git(base, "init", "-q", "--bare", "-b", "main", remote)
    git(base, "clone", "-q", remote, repo)
    with open(os.path.join(repo, ".gitignore"), "w") as f: f.write(".sankofa/\n")
    git(repo, "add", "-A"); git(repo, "commit", "-qm", "init", who="Human"); git(repo, "push", "-q", "origin", "main")
    git(base, "clone", "-q", remote, routine)
    run = lambda *a, **k: subprocess.run([S, *a], cwd=repo, env=env, capture_output=True, text=True, **k)
    run("init")
    d = Daemon(base, env); d.start()
    assert run("bridge", "on", "1").returncode == 0
    run("watch", "add", ".")
    subprocess.run([WATCH, "--once"], env=env)   # first sight: offsets only

    stats = {"nights": 0, "branches moved": 0, "landed": 0, "missed": 0, "extra": 0, "wrong sha": 0,
             "own pushes": 0, "own reported": 0, "toasted": 0, "untoasted": 0, "reboots": 0, "network down": 0,
             "force pushes": 0, "human fetched first": 0}
    wake_lat = []
    branches = []
    for night in range(nights):
        stats["nights"] += 1
        seen = len(ledger(repo))
        # evening: the human's own push
        own = []
        if rnd.random() < 0.6:
            git(repo, "pull", "-q", "--no-rebase", "origin", "main", check=False, who="Human")
            git(repo, "commit", "-q", "--allow-empty", "-m", f"human day {night}", who="Human")
            git(repo, "push", "-q", "origin", "HEAD:main", who="Human")
            own.append("origin/main"); stats["own pushes"] += 1
            if rnd.random() < 0.5:   # and keeps working before the bridge's next fetch
                git(repo, "commit", "-q", "--allow-empty", "-m", f"human evening {night}", who="Human")
            time.sleep(1.5)   # the bridge notices it during the day (and must not report it)
        reboot = rnd.random() < 0.3
        if reboot: d.stop(); stats["reboots"] += 1
        else: d.p.send_signal(signal.SIGSTOP)
        # night: routines push
        moved = {}
        for _ in range(rnd.randint(0, 6)):
            if branches and rnd.random() < 0.5:
                b = rnd.choice(branches)
                git(routine, "checkout", "-q", b)
                if rnd.random() < 0.15:
                    git(routine, "commit", "-q", "--amend", "--allow-empty", "-m", f"redo {b} {night}")
                    git(routine, "push", "-qf", "origin", b); stats["force pushes"] += 1
                else:
                    git(routine, "commit", "-q", "--allow-empty", "-m", f"{b} night {night}")
                    git(routine, "push", "-q", "origin", b)
            else:
                b = f"routine/n{night}-{rnd.randint(0, 999)}"
                git(routine, "fetch", "-q", "origin", "main"); git(routine, "checkout", "-q", "-B", b, "origin/main")
                git(routine, "commit", "-q", "--allow-empty", "-m", f"start {b}")
                git(routine, "push", "-q", "origin", b); branches.append(b)
            moved["origin/" + b] = git(routine, "rev-parse", "HEAD")
        stats["branches moved"] += len(moved)
        # morning
        offline = rnd.random() < 0.25
        if offline: os.rename(remote, gone); stats["network down"] += 1
        if rnd.random() < 0.3 and not offline:
            git(repo, "fetch", "-q", who="Human"); stats["human fetched first"] += 1
        t0 = time.time()
        if reboot: d.start()
        else: d.p.send_signal(signal.SIGCONT)
        if offline:
            time.sleep(0.5); os.rename(gone, remote)
        deadline = time.time() + 8
        while time.time() < deadline:
            got = {e["branch"] for e in ledger(repo)[seen:] if e["type"] == "landed"}
            if set(moved) <= got: break
            time.sleep(0.05)
        if moved: wake_lat.append(time.time() - t0)
        time.sleep(1.2)   # one more fetch interval: nothing extra may appear
        new = [e for e in ledger(repo)[seen:] if e["type"] == "landed"]
        by = {}
        for e in new: by.setdefault(e["branch"], []).append(e)
        stats["landed"] += len(new)
        for b, sha in moved.items():
            if b not in by: stats["missed"] += 1
            elif by[b][-1]["sha"] != sha: stats["wrong sha"] += 1
        stats["extra"] += sum(len(v) - 1 for v in by.values()) + sum(len(v) for b, v in by.items() if b not in moved)
        stats["own reported"] += sum(1 for b in own if b in by and b not in moved)
        before = sum(1 for _ in open(toasts))
        subprocess.run([WATCH, "--once"], env=env)
        new_toasts = [json.loads(l) for l in open(toasts)][before:]
        covered = sum(int(t["title"].split(": ")[1].split()[0]) if "new entries" in t["title"] else 1 for t in new_toasts
                      if "landed" in t["title"] or "landed" in t["body"])
        stats["toasted"] += min(covered, len(new)); stats["untoasted"] += max(0, len(new) - covered)
    d.stop()
    shutil.rmtree(base, ignore_errors=True)
    return stats, wake_lat


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seeds", type=int, default=3)
    ap.add_argument("--nights", type=int, default=7)
    ap.add_argument("--out")
    a = ap.parse_args()
    tmp = tempfile.mkdtemp(prefix="sk-night-", dir=os.environ.get("XDG_RUNTIME_DIR") or "/tmp")
    t0 = time.time()
    runs, lat = [], []
    for s in range(a.seeds):
        st, l = one(s, a.nights, tmp)
        print(f"seed {s}: " + ", ".join(f"{k} {v}" for k, v in st.items()), file=sys.stderr)
        runs.append(st); lat += l
    shutil.rmtree(tmp, ignore_errors=True)
    tot = {k: sum(r[k] for r in runs) for k in runs[0]}
    rows = [
        ("nights", tot["nights"]),
        ("remote branches the routines moved", tot["branches moved"]),
        ("landed events", tot["landed"]),
        ("moved but not reported", tot["missed"]),
        ("reported with the wrong sha", tot["wrong sha"]),
        ("reported twice, or never moved", tot["extra"]),
        ("the human's own pushes", tot["own pushes"]),
        ("own pushes reported as landed", tot["own reported"]),
        ("landed events toasted", tot["toasted"]),
        ("landed events not toasted", tot["untoasted"]),
        ("nights the laptop rebooted (sankofad restarted)", tot["reboots"]),
        ("mornings the first fetch failed (network down)", tot["network down"]),
        ("mornings the human fetched before sankofad woke", tot["human fetched first"]),
        ("force pushes", tot["force pushes"]),
    ]
    out = [f"# Accelerated nights (bridge): {a.seeds} seeds x {a.nights} nights, fetch every 1 s", "",
           "| measure | total |", "|---|---|"] + [f"| {k} | {v} |" for k, v in rows]
    if lat:
        out += ["", f"Wake to every landing recorded: median {statistics.median(lat):.2f} s, "
                    f"max {max(lat):.2f} s (fetch interval 1 s, retry 0.3 s)"]
    out += ["", f"Wall time {time.time() - t0:.0f} s"]
    text = "\n".join(out) + "\n"
    print(text)
    if a.out:
        with open(a.out, "w") as f: f.write(text)
    bad = tot["missed"] + tot["wrong sha"] + tot["extra"] + tot["own reported"] + tot["untoasted"]
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
