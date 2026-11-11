#!/usr/bin/env python3
"""Accelerated team weeks for sankofa handoff / pickup: real git, a bare remote, one clone and
one sankofad per person (Dan runs no daemon at all), real sankofa-watch with a fake
notify-send. Each step one person does one thing:

  work      commit on their feature branch (now and then leaving an edit uncommitted)
  todo      leave an open item
  handoff   out of credits: sankofa handoff (refused with uncommitted changes, or when the
            remote branch has commits they lack)
  pickup    sankofa pickup, by branch or with no argument (refused with a dirty tree, when
            several are on offer, or when their own copy of the branch has diverged)
  race      two people pick up the same handoff at the same moment
  rehand    the sender gets credits back, works on, and hands the same branch off again
  fetch     the bridge fetches; the watcher toasts

Every outcome is checked against git itself (status, ls-remote, merge-base), not a model.
Checks: no handed-off commit is lost; a pickup lands on a commit containing the handoff; no
handoff is picked up twice; the letter's open items become todos exactly once; at the end the
open handoffs on every machine with a bridge match what is still on the remote; each handoff
the bridge records is toasted once on that machine and never on the sender's.

Usage: sim/handoff.py [--seeds 3] [--steps 150] [--out FILE]
"""
import argparse, json, os, random, shutil, signal, subprocess, sys, tempfile, threading, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
S = os.path.join(ROOT, "bin", "sankofa")
D = os.environ.get("SANKOFA_DAEMON_BIN", os.path.join(ROOT, "bin", "out", "sankofad"))
WATCH = os.path.join(ROOT, "bin", "sankofa-watch")
FAKE = """#!/bin/sh
urg=normal
while [ $# -gt 2 ]; do case $1 in -u) urg=$2; shift 2 ;; -a) shift 2 ;; *) shift ;; esac; done
python3 -c 'import json,sys; print(json.dumps({"u":sys.argv[1],"title":sys.argv[2],"body":sys.argv[3]}))' "$urg" "$1" "$2" >> "$TOASTS"
"""
PEOPLE = ["Alice", "Bob", "Carol", "Dan"]   # Dan: no daemon, no bridge, no watcher


class Person:
    def __init__(self, name, base, remote):
        self.name, self.dir = name, os.path.join(base, name.lower())
        home = os.path.join(base, "home-" + name.lower())
        fakebin = os.path.join(home, "bin"); os.makedirs(fakebin)
        with open(os.path.join(fakebin, "notify-send"), "w") as f: f.write(FAKE)
        os.chmod(os.path.join(fakebin, "notify-send"), 0o755)
        self.toasts = os.path.join(home, "toasts.jsonl"); open(self.toasts, "w").close()
        self.daemon = name != "Dan"
        self.sock, self.db = os.path.join(home, "d.sock"), os.path.join(home, "d.db")
        env = dict(os.environ, SANKOFA_SOCKET=self.sock, SANKOFA_SESSION="s-" + name.lower(), TOASTS=self.toasts,
                   GIT_AUTHOR_NAME=name, GIT_AUTHOR_EMAIL=f"{name.lower()}@x", GIT_COMMITTER_NAME=name,
                   GIT_COMMITTER_EMAIL=f"{name.lower()}@x", SANKOFA_BRIDGE_TICK="3600", SANKOFA_RESCAN="3600",
                   SANKOFA_SETTLE="3600", SANKOFA_TOAST_BURST="1000", XDG_CONFIG_HOME=os.path.join(home, "config"),
                   XDG_STATE_HOME=os.path.join(home, "state"), XDG_DATA_HOME=os.path.join(home, "data"),
                   PATH=fakebin + ":" + os.environ["PATH"])
        for k in ("XDG_BIN_HOME", "CLAUDE_CODE_SESSION_ID", "CLAUDE_SESSION_ID", "SANKOFA_AGENT"): env.pop(k, None)
        if not self.daemon: env["SANKOFA_NO_DAEMON"] = "1"
        self.env = env
        subprocess.run(["git", "clone", "-q", remote, self.dir], env=env, check=True, capture_output=True)
        self.p = None
        self.branch_n = 0
        self.dirty = False

    def start(self):
        if not self.daemon: return
        # never on our stdout: a daemon left behind by a crash would hold a pipe open
        self.p = subprocess.Popen([D, "serve", "--socket", self.sock, "--db", self.db], env=self.env, stdout=subprocess.DEVNULL,
                                  stderr=open(os.path.join(os.path.dirname(self.sock), "d.log"), "a"))
        for _ in range(300):
            if subprocess.run([D, "send", "--socket", self.sock], input='{"verb":"ping"}', text=True,
                              capture_output=True).returncode == 0:
                return
            time.sleep(0.01)
        raise SystemExit("sankofad did not start")

    def stop(self):
        if self.p and self.p.poll() is None: self.p.send_signal(signal.SIGTERM); self.p.wait()

    def git(self, *a, check=True):
        r = subprocess.run(["git", *a], cwd=self.dir, env=self.env, capture_output=True, text=True)
        if check and r.returncode: raise SystemExit(f"{self.name}: git {' '.join(a)}: {r.stderr}")
        return r.stdout.strip()

    def sk(self, *a):
        return subprocess.run([S, *a], cwd=self.dir, env=self.env, capture_output=True, text=True)

    def ledger(self):
        with open(os.path.join(self.dir, ".sankofa", "ledger.jsonl")) as f:
            return [json.loads(l) for l in f if l.strip()]

    def open_items(self, typ):
        led = self.ledger()
        done = {e.get("ref") for e in led if e["type"] == "done"}
        return [e for e in led if e["type"] == typ and e["id"] not in done]

    def branch(self): return self.git("symbolic-ref", "-q", "--short", "HEAD", check=False)

    def tracked_dirty(self):
        return self.git("status", "--porcelain", "--untracked-files=no", "--", ".", ":(exclude).sankofa") != ""

    def watch(self):
        if self.daemon: subprocess.run([WATCH, "--once"], env=self.env, capture_output=True)

    def toast_list(self):
        return [json.loads(l) for l in open(self.toasts)]


def remote_refs(p):
    out = p.git("ls-remote", "origin", "refs/heads/*", "refs/sankofa/handoff/*")
    heads, letters = {}, {}
    for line in out.splitlines():
        sha, ref = line.split("\t")
        if ref.endswith("^{}"): continue   # the peeled commit of a tag, listed beside it
        if ref.startswith("refs/heads/"): heads[ref[11:]] = sha
        elif ref.startswith("refs/sankofa/handoff/"): letters[ref[21:]] = sha
    return heads, letters


def one(seed, steps, tmp):
    team = []
    try:
        return week(seed, steps, tmp, team)
    finally:
        for p in team: p.stop()


def week(seed, steps, tmp, team):
    rnd = random.Random(seed)
    base = tempfile.mkdtemp(prefix=f"team{seed}-", dir=tmp)
    remote = os.path.join(base, "remote.git")
    subprocess.run(["git", "init", "-q", "--bare", "-b", "main", remote], check=True)
    seedrepo = os.path.join(base, "seed")
    subprocess.run(["git", "clone", "-q", remote, seedrepo], check=True, capture_output=True)
    with open(os.path.join(seedrepo, ".gitignore"), "w") as f: f.write(".sankofa/\n")
    g = lambda *a: subprocess.run(["git", "-c", "user.name=s", "-c", "user.email=s@x", *a], cwd=seedrepo, check=True, capture_output=True)
    g("add", "-A"); g("commit", "-qm", "init"); g("push", "-q", "origin", "main")
    team.extend(Person(n, base, remote) for n in PEOPLE)
    for p in team:
        p.start(); p.sk("init")
        if p.daemon:
            assert p.sk("bridge", "on", "1h").returncode == 0, p.sk("bridge", "on", "1h").stderr
            p.sk("watch", "add", "."); p.watch()

    st = {k: 0 for k in ["steps", "commits", "handoffs", "handoffs refused (uncommitted)", "handoffs refused (behind)",
                         "rehands", "pickups", "pickups by name", "pickups refused (dirty)", "pickups refused (several)",
                         "pickups refused (diverged)", "pickups refused (untracked in the way)", "nothing to pick up", "races", "race double pickups",
                         "race both lost", "resumes", "unexpected outcomes", "lost commits", "pickup not containing handoff",
                         "picked up twice", "todos carried", "todos expected", "open handoffs wrong at end",
                         "toasts", "toasts on sender", "bridge events untoasted", "toasted twice", "fetches"]}
    sent = []        # every successful handoff: dict(sender, branch, sha, letter_todos, picked=[])
    unexpected = []

    def bad(msg):
        st["unexpected outcomes"] += 1; unexpected.append(msg)

    def offered(q):
        """What git says q would be offered: letters on the remote that q did not send (at that commit)."""
        _, letters = remote_refs(q)
        mine = {h["letter"] for h in sent if h["sender"] is q}
        out = {}
        for b, tag in letters.items():
            if tag in mine: continue
            sha = q.git("rev-parse", "-q", "--verify", tag + "^{commit}", check=False) or None
            if sha is None:   # not fetched yet: the pickup fetches it
                q.git("fetch", "-q", "--no-tags", "origin", f"+refs/sankofa/handoff/{b}:refs/sim/{b}", check=False)
                sha = q.git("rev-parse", "-q", "--verify", f"refs/sim/{b}^{{commit}}", check=False)
            out[b] = sha
        return out

    def do_pickup(q, arg, expect_some=True):
        """Run one pickup and check it against git. Returns the handoff taken, or None."""
        dirty = q.tracked_dirty()
        offer = offered(q)
        target = arg if arg else (next(iter(offer)) if len(offer) == 1 else None)
        diverged = False
        if target and target in offer and q.git("show-ref", "-q", "--verify", f"refs/heads/{target}", check=False) == "" and \
                subprocess.run(["git", "show-ref", "-q", "--verify", f"refs/heads/{target}"], cwd=q.dir).returncode == 0:
            q.git("fetch", "-q", "origin", target, check=False)
            diverged = subprocess.run(["git", "merge-base", "--is-ancestor", f"refs/heads/{target}", offer[target]],
                                      cwd=q.dir).returncode != 0
        before_todos = len(q.open_items("todo"))
        r = q.sk("pickup", *([arg] if arg else []))
        out = r.stdout + r.stderr
        if dirty:
            if r.returncode == 0 or "commit or stash first" not in out: bad(f"{q.name} pickup with a dirty tree: {out.strip()[:120]}")
            else: st["pickups refused (dirty)"] += 1
            return None
        if not arg and len(offer) > 1:
            if r.returncode == 0 or "more than one" not in out: bad(f"{q.name} pickup with {len(offer)} on offer: {out.strip()[:120]}")
            else: st["pickups refused (several)"] += 1
            return None
        if not target or target not in offer:
            if "nothing handed off" not in out: bad(f"{q.name} pickup of nothing ({arg}): {out.strip()[:120]}")
            else: st["nothing to pick up"] += 1
            return None
        if diverged:
            if r.returncode == 0 or "commits of its own" not in out: bad(f"{q.name} pickup onto a diverged {target}: {out.strip()[:120]}")
            else: st["pickups refused (diverged)"] += 1
            return None
        if r.returncode != 0 and "move them first" in out:   # an untracked file of theirs in the way
            st["pickups refused (untracked in the way)"] += 1
            if q.branch() == target and subprocess.run(["git", "merge-base", "--is-ancestor", offer[target], "HEAD"], cwd=q.dir).returncode != 0:
                bad(f"{q.name} left half switched to {target}")
            return None
        if r.returncode != 0:
            if expect_some: bad(f"{q.name} pickup {target} failed: {out.strip()[:160]}")
            return None
        h = next((h for h in reversed(sent) if h["branch"] == target and h["sha"] == offer[target]), None)
        if h is None: bad(f"{q.name} picked up {target} that nobody handed off: offer {offer.get(target)} sent {[(x['branch'], x['sha'][:12]) for x in sent]}"); return None
        st["pickups"] += 1; st["pickups by name"] += bool(arg)
        h["picked"].append(q.name)
        if q.branch() != target: bad(f"{q.name} not on {target} after pickup")
        if subprocess.run(["git", "merge-base", "--is-ancestor", h["sha"], "HEAD"], cwd=q.dir).returncode != 0:
            st["pickup not containing handoff"] += 1
        if h["token"] not in out: bad(f"{q.name} pickup did not print the letter")
        got = len(q.open_items("todo")) - before_todos
        st["todos carried"] += got; st["todos expected"] += h["todos"]
        return h

    for step in range(steps):
        st["steps"] += 1
        p = rnd.choice(team)
        act = rnd.choices(["work", "todo", "handoff", "pickup", "race", "rehand", "fetch", "clean", "resume"],
                          [30, 8, 14, 16, 3, 5, 12, 6, 5])[0]
        if act == "work":
            b = p.branch()
            if b == "main" or not b:
                p.branch_n += 1
                b = f"{p.name.lower()}/f{p.branch_n}"
                p.git("switch", "-qc", b)
            fn = "work-" + b.replace("/", "-") + ".txt"
            with open(os.path.join(p.dir, fn), "a") as f: f.write(f"{p.name} step {step}\n")
            if rnd.random() < 0.2:
                p.dirty = True   # left uncommitted
            else:
                p.git("add", fn); p.git("commit", "-qm", f"{b} step {step}"); st["commits"] += 1
        elif act == "todo":
            p.sk("todo", f"{p.name} todo at step {step}")
        elif act == "resume":   # back to an old branch of theirs, which others may have moved on since
            locals_ = [b for b in p.git("for-each-ref", "--format=%(refname:short)", "refs/heads").split() if b != "main"]
            if not locals_ or p.tracked_dirty(): continue
            b = rnd.choice(locals_)
            if subprocess.run(["git", "switch", "-q", b], cwd=p.dir, env=p.env, capture_output=True).returncode: continue
            with open(os.path.join(p.dir, "resume.txt"), "a") as f: f.write(f"{p.name} {step}\n")
            p.git("add", "resume.txt"); p.git("commit", "-qm", f"resume {b} {step}"); st["commits"] += 1
            st["resumes"] += 1
        elif act == "clean":
            if p.tracked_dirty():
                p.git("commit", "-qam", f"tidy {step}"); st["commits"] += 1
        elif act in ("handoff", "rehand"):
            b = p.branch()
            if act == "rehand":   # credits back before anyone picked it up: on with it, then hand it off again
                mine = [h for h in sent if h["sender"] is p and not h["picked"]]
                if not mine or p.tracked_dirty(): continue
                b = mine[-1]["branch"]
                if subprocess.run(["git", "switch", "-q", b], cwd=p.dir, env=p.env, capture_output=True).returncode: continue
                with open(os.path.join(p.dir, "more.txt"), "a") as f: f.write(f"{step}\n")
                p.git("add", "more.txt"); p.git("commit", "-qm", f"more {step}"); st["commits"] += 1
            if not b or b == "main": continue
            dirty = p.tracked_dirty()
            p.git("fetch", "-q", "origin", check=False)
            behind = p.git("rev-parse", "-q", "--verify", f"refs/remotes/origin/{b}", check=False) and \
                subprocess.run(["git", "merge-base", "--is-ancestor", f"refs/remotes/origin/{b}", "HEAD"], cwd=p.dir).returncode != 0
            todos = len(p.open_items("todo"))
            token = f"[T{seed}-{step}]"
            r = p.sk("handoff", f"{p.name} hands off {b} {token}")
            out = r.stdout + r.stderr
            if dirty:
                if r.returncode == 0 or "commit first" not in out: bad(f"{p.name} handoff with uncommitted changes: {out[:120]}")
                else: st["handoffs refused (uncommitted)"] += 1
                continue
            if behind:
                if r.returncode == 0 or "pull them first" not in out: bad(f"{p.name} handoff behind the remote: {out[:120]}")
                else: st["handoffs refused (behind)"] += 1
                continue
            if r.returncode != 0: bad(f"{p.name} handoff {b} failed: {out[:160]}"); continue
            sha = p.git("rev-parse", "HEAD")
            heads, letters = remote_refs(p)
            if heads.get(b) != sha: bad(f"{p.name} handoff: remote {b} not at the commit")
            if b not in letters: bad(f"{p.name} handoff: no letter on the remote")
            sent.append({"sender": p, "branch": b, "sha": sha, "letter": letters.get(b), "todos": todos, "token": token, "picked": []})
            st["rehands" if act == "rehand" else "handoffs"] += 1
            if p.daemon:   # the sender's own watcher must stay quiet about it
                p.watch()
            # out of credits: they stop and go back to main, the branch is the teammate's now
            p.git("switch", "-q", "main")
        elif act == "pickup":
            q = p
            offer = offered(q)
            arg = rnd.choice(sorted(offer)) if offer and rnd.random() < 0.5 else None
            if q.dirty and rnd.random() < 0.6: pass   # tries anyway, with the edit lying around
            do_pickup(q, arg)
            q.dirty = q.tracked_dirty()
        elif act == "race":
            q1, q2 = rnd.sample([t for t in team if t is not p] + [p], 2)
            _, letters = remote_refs(q1)
            cands = [h for h in sent if not h["picked"] and h["branch"] in letters and h["sender"] not in (q1, q2)]
            if not cands or q1.tracked_dirty() or q2.tracked_dirty(): continue
            h = rnd.choice(cands)
            for q in (q1, q2):   # both look at the remote first, as two people would
                q.git("fetch", "-q", "--no-tags", "origin", f"+refs/sankofa/handoff/*:refs/sankofa/remote/origin/*", check=False)
            st["races"] += 1
            res = {}
            def go(q): res[q.name] = q.sk("pickup", h["branch"])
            ts = [threading.Thread(target=go, args=(q,)) for q in (q1, q2)]
            for t in ts: t.start()
            for t in ts: t.join()
            won = [n for n, r in res.items() if r.returncode == 0 and "picked up" in r.stdout]
            for n in won: h["picked"].append(n)
            if len(won) > 1: st["race double pickups"] += 1
            if not won:
                st["race both lost"] += 1
                unexpected.append("race both lost: " + " | ".join(f"{k}: {(r.stdout + r.stderr).strip()[:150]}" for k, r in res.items()))
            for q in (q1, q2): q.dirty = q.tracked_dirty()
        elif act == "fetch":
            if p.daemon:
                p.sk("bridge", "fetch"); p.watch(); st["fetches"] += 1

    # end of the week: everyone with a bridge looks twice
    for _ in range(2):
        for p in team:
            if p.daemon: p.sk("bridge", "fetch"); p.watch()

    # checks against git
    heads, letters = remote_refs(team[0])
    for h in sent:
        if subprocess.run(["git", "-C", remote, "merge-base", "--is-ancestor", h["sha"], f"refs/heads/{h['branch']}"],
                          capture_output=True).returncode != 0:
            st["lost commits"] += 1
        if len(h["picked"]) > 1: st["picked up twice"] += 1
    for p in team:
        if not p.daemon: continue
        # by letter: the same commit can be handed off twice, by two people
        open_h = {(e["branch"].split("/", 1)[1], e.get("letter")) for e in p.open_items("handoff")}
        want = set(letters.items())
        # a machine only knows a letter it fetched; one it never saw is not missing, one gone must be closed
        extra = open_h - want
        missing = {x for x in want - open_h if any(h["sender"] is p and (h["branch"], h["letter"]) == x for h in sent)}
        st["open handoffs wrong at end"] += len(extra) + len(missing)
        if extra or missing: unexpected.append(f"{p.name} open handoffs: extra {sorted(extra)} missing {sorted(missing)}")
        led = p.ledger()
        recorded = [e for e in led if e["type"] == "handoff" and e["session"] == "sankofad"]
        toasts = [t for t in p.toast_list() if t["title"].endswith("handed off to you")]
        st["toasts"] += len(toasts)
        for e in recorded:
            flat = lambda x: " ".join(x.split())
            n = sum(1 for t in toasts if flat(e["text"]) in flat(t["body"]))
            if n == 0: st["bridge events untoasted"] += 1
            if n > 1: st["toasted twice"] += 1
        own = [h["token"] for h in sent if h["sender"] is p]
        mine_t = [t for t in toasts if any(tok in t["body"] for tok in own)]
        st["toasts on sender"] += len(mine_t)
        for t in mine_t: unexpected.append(f"{p.name} toasted own: {t['body'][:100]!r}")
        if extra or missing:
            for e in led:
                if e["type"] in ("handoff", "done") and (e.get("branch", "").endswith(tuple(b for b, _ in extra | missing)) or e["type"] == "done"):
                    if e["type"] == "handoff" or any(x.get("id") == e.get("ref") and x["type"] == "handoff" for x in led):
                        unexpected.append(f"   {p.name} {e['id']} {e['type']} {e.get('branch','')} {e.get('sha','')[:8]} {e['session']} {e['text'][:60]!r} ref={e.get('ref')}")
    shutil.rmtree(base, ignore_errors=True)
    return st, unexpected


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seeds", type=int, default=3)
    ap.add_argument("--steps", type=int, default=150)
    ap.add_argument("--out")
    a = ap.parse_args()
    tmp = tempfile.mkdtemp(prefix="sk-team-", dir=os.environ.get("XDG_RUNTIME_DIR") or "/tmp")
    t0 = time.time()
    runs = []
    for s in range(a.seeds):
        st, un = one(s, a.steps, tmp)
        print(f"seed {s}: " + ", ".join(f"{k} {v}" for k, v in st.items() if v), file=sys.stderr)
        for u in un[:8]: print(f"  ! {u}", file=sys.stderr)
        runs.append(st)
    shutil.rmtree(tmp, ignore_errors=True)
    tot = {k: sum(r[k] for r in runs) for k in runs[0]}
    out = [f"# Accelerated team weeks (handoff): {a.seeds} seeds x {a.steps} steps, 4 people", "",
           "| measure | total |", "|---|---|"] + [f"| {k} | {v} |" for k, v in tot.items()]
    out += ["", f"Wall time {time.time() - t0:.0f} s"]
    text = "\n".join(out) + "\n"
    print(text)
    if a.out:
        with open(a.out, "w") as f: f.write(text)


if __name__ == "__main__":
    main()
