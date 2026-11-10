#!/usr/bin/env python3
"""Accelerated week for the notifier: run simulated weeks (sim/week.py, real CLI and
Stop hook) in several repos at once while the real sankofa-watch runs live, with a
fake notify-send that records every toast. One "reboot" takes the watcher down for a
whole day mid-week.

Checks, per run:
  exact      every awaiting and note entry in a watched ledger is toasted exactly once,
             one by one or inside a burst summary; nothing from the unwatched repo
  latency    time from an awaiting entry to its toast (watcher up)
  noise      toasts per simulated day, split by type

Usage: sim/notify.py [--seeds N] [--watched 3] [--down-day 4] [--out FILE]
"""
import argparse, json, os, re, signal, statistics, subprocess, sys, tempfile, threading, time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import week  # noqa: E402

WATCH = os.path.join(week.ROOT, "bin", "sankofa-watch")
FAKE = """#!/bin/sh
urg=normal
while [ $# -gt 2 ]; do case $1 in -u) urg=$2; shift 2 ;; -a) shift 2 ;; *) shift ;; esac; done
python3 -c 'import json,sys,time; print(json.dumps({"t":time.time(),"u":sys.argv[1],"title":sys.argv[2],"body":sys.argv[3]}))' "$urg" "$1" "$2" >> "$TOASTS"
"""


def one(seed, n_watched, down_day, tmp):
    env_dir = tempfile.mkdtemp(prefix="sankofa-notify-", dir=tmp)
    toasts = os.path.join(env_dir, "toasts.jsonl")
    fakebin = os.path.join(env_dir, "bin"); os.makedirs(fakebin)
    with open(os.path.join(fakebin, "notify-send"), "w") as f:
        f.write(FAKE)
    os.chmod(os.path.join(fakebin, "notify-send"), 0o755)
    os.environ.update(SANKOFA_TOAST_TYPES="awaiting note", XDG_CONFIG_HOME=os.path.join(env_dir, "config"),
                      XDG_STATE_HOME=os.path.join(env_dir, "state"), TOASTS=toasts,
                      PATH=fakebin + ":" + os.environ["PATH"], SANKOFA_WATCH_TIMEOUT="5")
    open(toasts, "w").close()
    watcher = {"p": None}

    def start():
        watcher["p"] = subprocess.Popen([WATCH], stderr=subprocess.DEVNULL)

    def stop():
        watcher["p"].send_signal(signal.SIGTERM); watcher["p"].wait()

    n_repos = n_watched + 1
    repos, ready = {}, threading.Barrier(n_repos)
    days_done = {"n": 0}

    def day_action():
        d = days_done["n"]; days_done["n"] += 1
        time.sleep(0.5)  # let the watcher drain before the next day starts
        if d == down_day - 2:
            stop()       # the night before down_day: machine goes off
        elif d == down_day - 1:
            start()      # down_day's entries were written with no watcher running
    day_end = threading.Barrier(n_repos, action=day_action)

    def worker(i):
        def on_start(repo):
            name = os.path.basename(repo.dir)
            repos[i] = name
            if i < n_watched:
                repo.cli(None, "watch", "add", ".")
            ready.wait()  # every repo exists and is registered before the watcher starts
            if i == 0:
                start(); time.sleep(1)
            ready.wait()
        def on_day(_):
            day_end.wait()
        return week.run("hook", week.plan_week(week.random.Random(seed * 10 + i), 7), 0.5, seed * 10 + i,
                     base=tmp, on_start=on_start, on_day_end=on_day)

    snapshots = {}  # each repo's final ledger, read just before the repo is deleted
    orig_cleanup = week.Repo.cleanup
    def cleanup(self):
        snapshots[os.path.basename(self.dir)] = [json.loads(l) for l in open(
            os.path.join(self.dir, ".sankofa", "ledger.jsonl"))]
        orig_cleanup(self)
    week.Repo.cleanup = cleanup
    threads = [threading.Thread(target=worker, args=(i,)) for i in range(n_repos)]
    for t in threads: t.start()
    for t in threads: t.join()
    time.sleep(2); stop()
    week.Repo.cleanup = orig_cleanup

    shown = [json.loads(l) for l in open(toasts)]
    res = {"days": 7, "toasts": len(shown), "exact": True, "problems": [], "lat": [],
           "awaiting_toasts": 0, "note_toasts": 0, "summaries": 0, "unwatched_toasts": 0,
           "per_day_max": 0}
    for i in range(n_repos):
        name, entries = repos[i], snapshots[repos[i]]
        mine = [t for t in shown if t["title"].startswith(name + ":")]
        if i == n_watched:
            res["unwatched_toasts"] = len(mine)
            if mine: res["exact"] = False; res["problems"].append(f"unwatched repo toasted {len(mine)}x")
            continue
        want = {"awaiting": [e for e in entries if e["type"] == "awaiting"],
                "note": [e for e in entries if e["type"] == "note"]}
        got = {"awaiting": 0, "note": 0}
        for t in mine:
            m = re.match(r".*: (\d+) new entries$", t["title"])
            if m:
                res["summaries"] += 1
                for k, typ in re.findall(r"(\d+) (\w+)", t["body"].split("\n")[0]):
                    got[typ] = got.get(typ, 0) + int(k)
            elif t["title"].endswith(": awaiting you"):
                got["awaiting"] += 1; res["awaiting_toasts"] += 1
                e = next((e for e in want["awaiting"] if e["text"] == t["body"]), None)
                if e:
                    ts = time.mktime(time.strptime(e["ts"][:19], "%Y-%m-%dT%H:%M:%S")) - time.timezone \
                        + float("0." + e["ts"][20:23])
                    res["lat"].append(t["t"] - ts)
            else:
                got["note"] += 1; res["note_toasts"] += 1
        for typ in ("awaiting", "note"):
            if got[typ] != len(want[typ]):
                res["exact"] = False
                res["problems"].append(f"{name}: {len(want[typ])} {typ} entries, {got[typ]} toasted")
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seeds", type=int, default=3)
    ap.add_argument("--watched", type=int, default=3)
    ap.add_argument("--down-day", type=int, default=4)
    ap.add_argument("--tmp")
    ap.add_argument("--out")
    a = ap.parse_args()
    runs = []
    for seed in range(a.seeds):
        r = one(seed, a.watched, a.down_day, a.tmp)
        runs.append(r)
        print(f"seed {seed}: {r['toasts']} toasts, exact={r['exact']} {r['problems']}", file=sys.stderr)
    lat = sorted(x for r in runs for x in r["lat"])
    n = a.seeds
    per_day = sum(r["toasts"] for r in runs) / n / 7
    notes = sum(r["note_toasts"] for r in runs) / n / 7
    aw = sum(r["awaiting_toasts"] for r in runs) / n / 7
    lines = [f"# Accelerated week (notifier): {n} seeds, {a.watched} watched repos + 1 unwatched, "
             f"watcher down on day {a.down_day}", "",
             "| measure | value |", "|---|---|",
             f"| every awaiting and note toasted exactly once (watcher run with both types on) | {'yes' if all(r['exact'] for r in runs) else 'NO'} |",
             f"| toasts from the unwatched repo | {sum(r['unwatched_toasts'] for r in runs)} |",
             f"| toasts per day, all types | {per_day:.1f} |",
             f"| of which note toasts | {notes:.1f} |",
             f"| of which awaiting toasts | {aw:.1f} |",
             f"| burst summaries per week | {sum(r['summaries'] for r in runs) / n:.1f} |",
             f"| awaiting latency, median | {statistics.median(lat) if lat else float('nan'):.2f} s |",
             f"| awaiting latency, worst | {max(lat) if lat else float('nan'):.2f} s |"]
    probs = [p for r in runs for p in r["problems"]]
    if probs:
        lines += ["", "Problems:"] + [f"- {p}" for p in probs]
    report = "\n".join(lines) + "\n"
    print(report)
    if a.out:
        open(a.out, "w").write(report)


if __name__ == "__main__":
    main()
