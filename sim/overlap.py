#!/usr/bin/env python3
"""Accelerated subagent days for the overlap watcher: real sankofad (inotify only; the
periodic rescan is pushed out to an hour), real git worktrees. A home tree and its worktrees
(one per subagent) take turns: append to a file (a few files are hot, most are cold), commit,
throw a change away (git checkout), make a new directory, write build output git ignores.
Then the sim waits past the watcher's quiet time and compares the ledger with its own model
of which paths each tree has changed.

Checks, per run:
  missed      a path changed in two or more trees (by the model) with no overlap event
  false       an overlap event for a path the model never had in two trees
  repeated    a second event for an overlap that stood the whole time with the same trees
  latency     from the last write of a burst to the overlap event

Usage: sim/overlap.py [--seeds 3] [--steps 400] [--trees 4] [--out FILE]
"""
import argparse, json, os, random, shutil, signal, statistics, subprocess, sys, tempfile, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
S = os.path.join(ROOT, "bin", "sankofa")
D = os.environ.get("SANKOFA_DAEMON_BIN", os.path.join(ROOT, "bin", "out", "sankofad"))


def git(cwd, *a, check=True):
    return subprocess.run(["git", "-c", "user.name=a", "-c", "user.email=a@x", *a], cwd=cwd,
                          check=check, capture_output=True, text=True).stdout


def overlaps(home):
    with open(os.path.join(home, ".sankofa", "ledger.jsonl")) as f:
        return [e for e in (json.loads(l) for l in f if l.strip()) if e["type"] == "overlap"]


def one(seed, steps, ntrees, tmp):
    rnd = random.Random(seed)
    base = tempfile.mkdtemp(prefix=f"ov{seed}-", dir=tmp)
    env = dict(os.environ, SANKOFA_SOCKET=os.path.join(base, "d.sock"), SANKOFA_RESCAN="3600")
    home = os.path.join(base, "home")
    os.makedirs(os.path.join(home, "src"))
    files = [f"src/m{i}/f{j}.c" for i in range(8) for j in range(8)]
    hot = files[:4]
    for p in files:
        os.makedirs(os.path.dirname(os.path.join(home, p)), exist_ok=True)
        open(os.path.join(home, p), "w").write("x\n")
    open(os.path.join(home, ".gitignore"), "w").write(".sankofa/\nbuild/\n")
    git(home, "init", "-q", "-b", "main"); git(home, "add", "-A"); git(home, "commit", "-qm", "init")
    subprocess.run([S, "init"], cwd=home, env=env, capture_output=True)
    trees = [home]
    for i in range(1, ntrees):
        t = os.path.join(base, f"agent{i}")
        git(home, "worktree", "add", "-q", "-b", f"agent{i}", t); trees.append(t)
    log = open(os.path.join(base, "d.log"), "w")
    d = subprocess.Popen([D, "serve", "--socket", env["SANKOFA_SOCKET"], "--db", os.path.join(base, "d.db")],
                         env=env, stderr=log)
    for _ in range(300):
        if subprocess.run([S, "daemon"], cwd=home, env=env, capture_output=True).returncode == 0: break
        time.sleep(0.01)
    subprocess.run([S, "trees", "on"], cwd=home, env=env, check=True, capture_output=True)

    changed = {t: set() for t in trees}      # model: paths each tree changed since leaving main
    reported = {}                            # path -> trees set last reported (as the daemon should keep it)
    st = dict(steps=0, writes=0, commits=0, discards=0, newdirs=0, ignored=0, checks=0,
              expected=0, events=0, missed=0, false=0, repeated=0)
    lat = []
    seen = 0
    burst_end = time.time()
    for step in range(steps):
        st["steps"] += 1
        t = rnd.choice(trees)
        r = rnd.random()
        if r < 0.6:
            p = rnd.choice(hot) if rnd.random() < 0.3 else rnd.choice(files)
            with open(os.path.join(t, p), "a") as f: f.write(f"{seed}.{step}\n")
            changed[t].add(p); st["writes"] += 1
        elif r < 0.7 and changed[t] and t != home:
            git(t, "commit", "-qam", f"step {step}", check=False); st["commits"] += 1   # still changed since leaving main
        elif r < 0.8 and changed[t] & set(files):
            p = rnd.choice(sorted(changed[t] & set(files)))
            # throw away the uncommitted change; a path committed in a worktree stays changed
            git(t, "checkout", "-q", "--", p)
            if t == home or git(t, "diff", "--name-only", git(t, "merge-base", "HEAD", "main").strip(), "--", p).strip() == "":
                changed[t].discard(p)
            st["discards"] += 1
        elif r < 0.9:
            p = f"src/new{rnd.randint(0, 5)}/g.c"
            os.makedirs(os.path.dirname(os.path.join(t, p)), exist_ok=True)
            with open(os.path.join(t, p), "a") as f: f.write(f"{step}\n")
            changed[t].add(p); st["newdirs"] += 1
        else:
            os.makedirs(os.path.join(t, "build"), exist_ok=True)
            with open(os.path.join(t, "build", "out.o"), "a") as f: f.write(f"{step}\n")
            st["ignored"] += 1
        burst_end = time.time()
        if step % 10 != 9: continue
        # checkpoint: wait for the watcher, then compare
        st["checks"] += 1
        want = {}
        for tr, ps in changed.items():
            for p in ps: want.setdefault(p, set()).add(tr)
        want = {p: s for p, s in want.items() if len(s) >= 2}
        deadline = time.time() + 3
        while True:
            evs = overlaps(home)
            got = {e["glob"] for e in evs[seen:] if e.get("glob")}
            need = {p for p, s in want.items() if not (p in reported and s <= reported[p])}
            if need <= got or time.time() > deadline: break
            time.sleep(0.02)
        if need: lat.append(time.time() - burst_end)
        time.sleep(0.4)   # past the quiet time: anything extra would show now
        evs = overlaps(home)
        new = evs[seen:]; seen = len(evs)
        st["events"] += len(new)
        counts = {}
        for e in new: counts[e.get("glob")] = counts.get(e.get("glob"), 0) + 1
        for p, s in want.items():
            fresh = not (p in reported and s <= reported[p])
            if fresh:
                st["expected"] += 1
                if p not in counts: st["missed"] += 1
                reported[p] = s | reported.get(p, set())
            elif p in counts: st["repeated"] += 1
            if counts.get(p, 0) > 1: st["repeated"] += counts[p] - 1
        for p in counts:
            if p not in want: st["false"] += 1
        for p in list(reported):
            if p not in want: del reported[p]   # cleared: a later overlap is reported again
    d.send_signal(signal.SIGTERM); d.wait()
    shutil.rmtree(base, ignore_errors=True)
    return st, lat


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seeds", type=int, default=3)
    ap.add_argument("--steps", type=int, default=400)
    ap.add_argument("--trees", type=int, default=4)
    ap.add_argument("--out")
    a = ap.parse_args()
    tmp = tempfile.mkdtemp(prefix="sk-ov-", dir=os.environ.get("XDG_RUNTIME_DIR") or "/tmp")
    t0 = time.time()
    runs, lat = [], []
    for s in range(a.seeds):
        st, l = one(s, a.steps, a.trees, tmp)
        print(f"seed {s}: " + ", ".join(f"{k} {v}" for k, v in st.items()), file=sys.stderr)
        runs.append(st); lat += l
    shutil.rmtree(tmp, ignore_errors=True)
    tot = {k: sum(r[k] for r in runs) for k in runs[0]}
    rows = [("steps (one tree acts per step)", tot["steps"]), ("appends to tracked files", tot["writes"]),
            ("commits in worktrees", tot["commits"]), ("changes thrown away", tot["discards"]),
            ("files in new directories", tot["newdirs"]), ("writes to ignored build output", tot["ignored"]),
            ("checkpoints", tot["checks"]), ("overlaps the model expected", tot["expected"]),
            ("overlap events", tot["events"]), ("missed", tot["missed"]), ("false", tot["false"]),
            ("repeated", tot["repeated"])]
    out = [f"# Accelerated subagent days (overlaps): {a.seeds} seeds x {a.steps} steps, {a.trees} trees, inotify only", "",
           "| measure | total |", "|---|---|"] + [f"| {k} | {v} |" for k, v in rows]
    if lat:
        out += ["", f"Last write of a burst to its overlap events: median {statistics.median(lat) * 1000:.0f} ms, "
                    f"max {max(lat) * 1000:.0f} ms (quiet time 300 ms)"]
    out += ["", f"Wall time {time.time() - t0:.0f} s"]
    text = "\n".join(out) + "\n"
    print(text)
    if a.out:
        with open(a.out, "w") as f: f.write(text)
    sys.exit(1 if tot["missed"] + tot["false"] + tot["repeated"] else 0)


if __name__ == "__main__":
    main()
