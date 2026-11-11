#!/usr/bin/env python3
"""Old and new sankofa side by side: an upgrade over an old database, and teams where one person
has not upgraded yet. Old releases are built from their git tags into a cache
(~/.cache/sankofa-sim/<tag>, a few minutes the first time).

  upgrade      a ledger, a todo and a lease written by an old sankofad; the new sankofad opens the
               same database: the todo is open, the lease is listed, and a handoff works
  old sends    an old CLI and sankofad hand off; the new one picks up; the old bridge sees it taken
  new sends    the new one hands off; the old one picks up; the new bridge sees it taken
  old hub      new devices through an old hub: handoff and pickup by id
  old device   a new hub, one old device and one new: the old device hands off, the new picks up

Usage: sim/compat.py [--old v0.10.2] [--upgrade-from v0.8.1] [--out FILE]
"""
import argparse, json, os, shutil, signal, subprocess, sys, tempfile, time, urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CACHE = os.path.join(os.environ.get("XDG_CACHE_HOME") or os.path.expanduser("~/.cache"), "sankofa-sim")


def build(tag):
    """The CLI and sankofad of an old release, built once."""
    d = os.path.join(CACHE, tag)
    if os.path.exists(os.path.join(d, "bin", "out", "sankofad")): return d
    shutil.rmtree(d, ignore_errors=True); os.makedirs(d)
    subprocess.run(f"git -C {ROOT} archive {tag} | tar -x -C {d}", shell=True, check=True)
    print(f"building {tag} ...", file=sys.stderr)
    subprocess.run(["dotnet", "publish", "daemon/sankofad", "-c", "Release", "-r", "linux-x64", "-o", "bin/out", "--nologo", "-v", "quiet"],
                   cwd=d, check=True, capture_output=True)
    return d


class Box:
    """One machine: a version (its CLI and sankofad), a home, a daemon, a clone."""
    def __init__(self, base, name, tree, remote=None):
        self.name, self.tree = name, tree
        self.home = os.path.join(base, "home-" + name); os.makedirs(self.home)
        self.sock, self.db = os.path.join(self.home, "d.sock"), os.path.join(self.home, "d.db")
        self.cli, self.d = os.path.join(tree, "bin", "sankofa"), os.path.join(tree, "bin", "out", "sankofad")
        self.env = dict(os.environ, SANKOFA_SOCKET=self.sock, SANKOFA_SESSION="s-" + name, GIT_AUTHOR_NAME=name, GIT_AUTHOR_EMAIL=name + "@x",
                        GIT_COMMITTER_NAME=name, GIT_COMMITTER_EMAIL=name + "@x", SANKOFA_BRIDGE_TICK="3600", SANKOFA_RESCAN="3600",
                        SANKOFA_SETTLE="3600", SANKOFA_DAEMON_BIN=self.d, XDG_CONFIG_HOME=os.path.join(self.home, "config"),
                        XDG_STATE_HOME=os.path.join(self.home, "state"), XDG_DATA_HOME=os.path.join(self.home, "data"),
                        SANKOFA_HUB_BREAKER="0.3", SANKOFA_HUB_RETRY_MAX="0.5", SANKOFA_HUB_TIMEOUT="1")
        for k in ("XDG_BIN_HOME", "CLAUDE_CODE_SESSION_ID", "CLAUDE_SESSION_ID", "SANKOFA_AGENT", "SANKOFA_NO_DAEMON"): self.env.pop(k, None)
        self.dir = os.path.join(base, name)
        if remote: subprocess.run(["git", "clone", "-q", remote, self.dir], env=self.env, check=True, capture_output=True)
        else: os.makedirs(self.dir)
        self.p = None

    def start(self):
        self.p = subprocess.Popen([self.d, "serve", "--socket", self.sock, "--db", self.db], env=self.env, stdout=subprocess.DEVNULL,
                                  stderr=open(os.path.join(self.home, "d.log"), "a"))
        for _ in range(300):
            if subprocess.run([self.d, "send", "--socket", self.sock], input='{"verb":"ping"}', text=True, capture_output=True).returncode == 0: return
            time.sleep(0.01)
        raise SystemExit(f"{self.name}: sankofad did not start")

    def stop(self):
        if self.p and self.p.poll() is None: self.p.send_signal(signal.SIGTERM); self.p.wait()

    def sk(self, *a):
        return subprocess.run([self.cli, *a], cwd=self.dir, env=self.env, capture_output=True, text=True)

    def git(self, *a):
        return subprocess.run(["git", *a], cwd=self.dir, env=self.env, capture_output=True, text=True).stdout.strip()

    def ledger(self):
        return [json.loads(l) for l in open(os.path.join(self.dir, ".sankofa", "ledger.jsonl")) if l.strip()]


def remote(base):
    r = os.path.join(base, "remote.git")
    subprocess.run(["git", "init", "-q", "--bare", "-b", "main", r], check=True)
    seed = os.path.join(base, "seed")
    subprocess.run(["git", "clone", "-q", r, seed], check=True, capture_output=True)
    with open(os.path.join(seed, ".gitignore"), "w") as f: f.write(".sankofa/\n")
    for a in (["add", "-A"], ["commit", "-qm", "init"], ["push", "-q", "origin", "main"]):
        subprocess.run(["git", "-c", "user.name=s", "-c", "user.email=s@x", *a], cwd=seed, check=True, capture_output=True)
    return r


def work(b, branch, text):
    b.git("switch", "-q", "-c", branch)
    with open(os.path.join(b.dir, branch + ".txt"), "w") as f: f.write(text)
    b.git("add", "-A"); b.git("commit", "-qm", text)
    return b.git("rev-parse", "HEAD")


class Hub:
    def __init__(self, base, tree):
        import socket
        s = socket.socket(); s.bind(("127.0.0.1", 0)); self.url = f"http://127.0.0.1:{s.getsockname()[1]}"; s.close()
        hd = os.path.join(base, "hub"); os.makedirs(hd)
        self.d = os.path.join(tree, "bin", "out", "sankofad")
        self.env = dict(os.environ, SANKOFA_HUB_TOKENS=os.path.join(hd, "tokens"), SANKOFA_HUB_DATA=os.path.join(hd, "data"))
        self.p = subprocess.Popen([self.d, "hub", "serve", "--listen", self.url + "/", "--db", os.path.join(hd, "hub.db")], env=self.env,
                                  stdout=subprocess.DEVNULL, stderr=open(os.path.join(hd, "log"), "a"))
        for _ in range(300):
            try: urllib.request.urlopen(self.url + "/healthz", timeout=0.2); return
            except Exception: time.sleep(0.02)
        raise SystemExit("hub did not start")

    def join(self, b):
        tok = subprocess.run([self.d, "hub", "token", "add", b.name], env=self.env, capture_output=True, text=True).stdout.strip()
        r = b.sk("hub", "join", self.url, tok, b.name); assert r.returncode == 0, r.stderr

    def stop(self):
        if self.p.poll() is None: self.p.send_signal(signal.SIGTERM); self.p.wait()


def wait(cond, secs=5):
    end = time.time() + secs
    while time.time() < end:
        if cond(): return True
        time.sleep(0.05)
    return cond()


def case_upgrade(base, old, new, bad):
    r = remote(base)
    o = Box(base, "alice", old, r); o.start()
    o.sk("init"); o.sk("note", "written by the old release")
    todo = o.sk("todo", "an old todo").stdout.strip()
    lease = o.sk("claim", "src/*", "2h").stdout.split()[0] if o.sk("claim", "src/*", "2h").returncode == 0 else ""
    o.stop()
    # the same machine after the upgrade: new CLI and sankofad, the same database and ledger
    n = Box.__new__(Box); n.__dict__.update(o.__dict__)
    n.cli, n.d = os.path.join(new, "bin", "sankofa"), os.path.join(new, "bin", "out", "sankofad")
    n.env = dict(o.env, SANKOFA_DAEMON_BIN=n.d); n.p = None
    n.start()
    try:
        out = n.sk("open").stdout
        if todo not in out: bad(f"upgrade: the old todo {todo} is not open: {out!r}")
        ls = n.sk("leases").stdout
        if lease and lease not in ls: bad(f"upgrade: the old lease {lease} is not listed: {ls!r}")
        work(n, "feat", "after the upgrade")
        h = n.sk("handoff", "handed off after the upgrade")
        if h.returncode != 0: bad(f"upgrade: handoff failed: {h.stderr.strip()}")
        ev = [e for e in n.ledger() if e["type"] == "handoff"]
        if not ev or not ev[-1].get("letter"): bad(f"upgrade: the handoff event has no letter: {ev}")
        d = n.sk("done", todo)
        if d.returncode != 0: bad(f"upgrade: done on the old todo failed: {d.stderr.strip()}")
    finally:
        n.stop()


def case_mixed(base, sender_tree, picker_tree, label, bad):
    r = remote(base)
    a, b = Box(base, "alice", sender_tree, r), Box(base, "bob", picker_tree, r)
    try:
        for x in (a, b):
            x.start(); x.sk("init"); x.sk("bridge", "on", "1h")
        sha = work(a, "feat", label)
        a.sk("todo", "the next step " + label)
        h = a.sk("handoff", "letter " + label)
        if h.returncode != 0: bad(f"{label}: handoff failed: {h.stderr.strip()}"); return
        b.sk("bridge", "fetch")
        p = b.sk("pickup", "feat")
        if p.returncode != 0: bad(f"{label}: pickup failed: {(p.stdout + p.stderr).strip()[:200]}"); return
        if b.git("rev-parse", "HEAD") != sha: bad(f"{label}: picker not at the handed-off commit")
        if "letter " + label not in p.stdout: bad(f"{label}: the letter was not printed")
        if "the next step " + label not in b.sk("open").stdout: bad(f"{label}: the letter's todo was not carried")
        if b.git("ls-remote", "origin", "refs/sankofa/handoff/*"): bad(f"{label}: the letter is still on the remote")
        a.sk("bridge", "fetch")
        if " handoff " in a.sk("open").stdout:
            bad(f"{label}: the sender's handoff is still open after the pickup")
    finally:
        a.stop(); b.stop()


def case_hub(base, hub_tree, a_tree, b_tree, label, bad):
    r = remote(base)
    hub = Hub(base, hub_tree)
    a, b = Box(base, "desk", a_tree, r), Box(base, "lap", b_tree, r)
    try:
        for x in (a, b):
            hub.join(x); x.start(); x.sk("init")
            o = x.sk("hub", "on", "demo")
            if o.returncode != 0: bad(f"{label}: hub on failed on {x.name}: {o.stderr.strip()}"); return
        sha = work(a, "feat", label)
        a.sk("todo", "shared step " + label)
        h = a.sk("handoff", "letter " + label)
        if h.returncode != 0: bad(f"{label}: handoff failed: {h.stderr.strip()}"); return
        hid = h.stdout.split()[-1] if h.stdout else ""
        hid = next((w for w in h.stdout.split() if w[:1] in "eq" and w[1:].isdigit()), hid)
        if not wait(lambda: any(e["id"] == hid for e in b.ledger())): bad(f"{label}: {hid} never reached lap"); return
        p = b.sk("pickup", hid)
        if p.returncode != 0: bad(f"{label}: pickup {hid} failed: {(p.stdout + p.stderr).strip()[:200]}"); return
        if b.git("rev-parse", "HEAD") != sha: bad(f"{label}: lap not at the handed-off commit")
        if not wait(lambda: any(e["type"] == "done" and e.get("ref") == hid for e in a.ledger())): bad(f"{label}: desk never saw it taken")
        if "shared step " + label not in b.sk("open").stdout: bad(f"{label}: the shared todo is not open on lap")
    finally:
        a.stop(); b.stop(); hub.stop()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--old", default="v0.10.2")
    ap.add_argument("--upgrade-from", default="v0.8.1")
    ap.add_argument("--out")
    a = ap.parse_args()
    old, up = build(a.old), build(a.upgrade_from)
    tmp = tempfile.mkdtemp(prefix="sk-compat-", dir=os.environ.get("XDG_RUNTIME_DIR") or "/tmp")
    new = os.path.join(tmp, "new"); os.makedirs(os.path.join(new, "bin"))
    shutil.copy2(os.path.join(ROOT, "bin", "sankofa"), os.path.join(new, "bin"))
    os.symlink(os.path.join(ROOT, "bin", "out"), os.path.join(new, "bin", "out"))
    cases = [
        (f"upgrade from {a.upgrade_from}", lambda b, bad: case_upgrade(b, up, new, bad)),
        (f"{a.old} sends, new picks up", lambda b, bad: case_mixed(b, old, new, "oldsends", bad)),
        (f"new sends, {a.old} picks up", lambda b, bad: case_mixed(b, new, old, "newsends", bad)),
        (f"new devices, {a.old} hub", lambda b, bad: case_hub(b, old, new, new, "oldhub", bad)),
        (f"new hub, {a.old} device sends", lambda b, bad: case_hub(b, new, old, new, "olddev", bad)),
        (f"new hub, {a.old} device picks up", lambda b, bad: case_hub(b, new, new, old, "olddevpick", bad)),
    ]
    rows, fails = [], []
    for name, fn in cases:
        base = tempfile.mkdtemp(dir=tmp)
        errs = []
        try: fn(base, errs.append)
        except Exception as e: errs.append(f"crashed: {e!r}")
        rows.append((name, "ok" if not errs else "FAIL"))
        fails += [f"{name}: {e}" for e in errs]
        print(f"{name}: {'ok' if not errs else '; '.join(errs)}", file=sys.stderr)
    shutil.rmtree(tmp, ignore_errors=True)
    out = ["# Old and new side by side", "", "| case | result |", "|---|---|"] + [f"| {n} | {r} |" for n, r in rows]
    if fails: out += ["", "Failures:", ""] + [f"- {f}" for f in fails]
    text = "\n".join(out) + "\n"
    print(text)
    if a.out:
        with open(a.out, "w") as f: f.write(text)
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()
