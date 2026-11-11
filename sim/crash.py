#!/usr/bin/env python3
"""Kill handoff and pickup at every step, then check nothing is lost or taken twice.

A handoff or pickup is a run of git commands and ledger writes. Each network call (push, fetch,
ls-remote) is also failed in turn: before it reaches the remote, and after the remote did it but
before the answer arrived. And this kills the sankofa process
(its whole process group, like a closed terminal or a session that ran out) just before, and
just after, each git command and each call to sankofad in turn: real git, a bare remote, a
sankofad per person. After each crash the person who was cut off does what a person does: looks
at sankofa open, then runs the same command again. Then the others act.

Checks, against git and the ledgers, after every crash:
  told       a crash that left work half done is reported by sankofa open on that machine
  delivered  the handoff reaches exactly one teammate, at the sender's commit, with the letter
  once       nobody else can pick it up afterwards; the letter's open items become todos once
  own        the sender's bridge never offers the sender their own letter
  closed     the sender's handoff is closed by their bridge, naming who picked it up
  clean      no journal is left behind once the command has been run again

Each runs three ways: through nothing but the git remote; with a hub as well; and with a hub
and a remote that refuses refs/sankofa/* (the hub ledger alone carries the handoff).

Then, per mode, two people pick up the same handoff at the same moment (--races times): exactly
one gets it, the other is told.

Usage: sim/crash.py [--only handoff|pickup] [--mode git|hub|hub-refuse|all] [--races 15] [--out FILE]
"""
import argparse, json, os, shutil, signal, subprocess, sys, tempfile, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import handoff   # noqa: E402
from handoff import Person, D   # noqa: E402
S = handoff.S

REAL_GIT = shutil.which("git")
SHIM = """#!/bin/sh
# count this call; kill the whole sankofa process group before or after call number $CRASH_AT
exec 8>>"$CRASH_COUNT.lock"; flock 8
n=$(( $(cat "$CRASH_COUNT" 2>/dev/null || echo 0) + 1 )); echo "$n" > "$CRASH_COUNT"
echo "$n $(basename "$0") $*" >> "$CRASH_COUNT.log"
flock -u 8
pg=$(ps -o pgid= -p $$ | tr -d ' ')
at=no; [ "$n" = "${CRASH_AT:-0}" ] && at=yes
# a call that talks to the remote, for the network failures
net=no; case " $* " in *" push "*|*" fetch "*|*" ls-remote "*) net=yes ;; esac
[ $at = yes ] && [ "$CRASH_WHEN" = before ] && kill -9 -- "-$pg"
if [ $at = yes ] && [ $net = yes ] && [ "$CRASH_WHEN" = fail-before ]; then
  echo "$n NETFAIL" >> "$CRASH_COUNT.log"; echo "fatal: unable to access remote: Connection timed out" >&2; exit 128
fi
"$REAL" "$@"; rc=$?
[ $at = yes ] && [ "$CRASH_WHEN" = after ] && kill -9 -- "-$pg"
if [ $at = yes ] && [ $net = yes ] && [ "$CRASH_WHEN" = fail-after ]; then
  # the remote did it, the answer never arrived
  echo "$n NETFAIL" >> "$CRASH_COUNT.log"; echo "fatal: the remote end hung up unexpectedly" >&2; exit 128
fi
exit $rc
"""


HUB_SPEED = dict(SANKOFA_HUB_BREAKER="0.3", SANKOFA_HUB_RETRY_MAX="0.5", SANKOFA_HUB_TIMEOUT="1")
REFUSE = """#!/bin/sh
# a remote that will not keep sankofa's refs (some hosts reject refs outside heads and tags)
while read -r old new ref; do case $ref in refs/sankofa/*) echo "refs/sankofa/* not allowed" >&2; exit 1 ;; esac; done
"""


def free_port():
    import socket
    s = socket.socket(); s.bind(("127.0.0.1", 0)); p = s.getsockname()[1]; s.close(); return p


class World:
    def __init__(self, tmp, tag, hub=False, refuse=False):
        self.base = tempfile.mkdtemp(prefix=tag + "-", dir=tmp)
        self.hub, self.hp = hub, None
        self.remote = os.path.join(self.base, "remote.git")
        subprocess.run(["git", "init", "-q", "--bare", "-b", "main", self.remote], check=True)
        if refuse:
            hook = os.path.join(self.remote, "hooks", "pre-receive")
            with open(hook, "w") as f: f.write(REFUSE)
            os.chmod(hook, 0o755)
        seed = os.path.join(self.base, "seed")
        subprocess.run(["git", "clone", "-q", self.remote, seed], check=True, capture_output=True)
        with open(os.path.join(seed, ".gitignore"), "w") as f: f.write(".sankofa/\n")
        g = lambda *a: subprocess.run(["git", "-c", "user.name=s", "-c", "user.email=s@x", *a], cwd=seed, check=True, capture_output=True)
        g("add", "-A"); g("commit", "-qm", "init"); g("push", "-q", "origin", "main")
        self.people = [Person(n, self.base, self.remote) for n in ("Alice", "Bob", "Carol")]
        if hub:
            hd = os.path.join(self.base, "hub"); os.makedirs(hd)
            self.url = f"http://127.0.0.1:{free_port()}"
            self.henv = dict(os.environ, SANKOFA_HUB_TOKENS=os.path.join(hd, "tokens"), SANKOFA_HUB_DATA=os.path.join(hd, "data"))
            self.hubdb = os.path.join(hd, "hub.db")
            self.start_hub()
        for p in self.people:
            if hub:
                p.env.update(HUB_SPEED)
                tok = subprocess.run([D, "hub", "token", "add", p.name.lower() + "-pc"], env=self.henv, capture_output=True, text=True).stdout.strip()
                r = p.sk("hub", "join", self.url, tok, p.name.lower() + "-pc"); assert r.returncode == 0, r.stderr
            else:
                # each person is their own device: the device name is what tells one pickup from another
                os.makedirs(os.path.join(p.env["XDG_CONFIG_HOME"], "sankofa"), exist_ok=True)
                with open(os.path.join(p.env["XDG_CONFIG_HOME"], "sankofa", "hub.conf"), "w") as f: f.write(f"device={p.name.lower()}-pc\n")
            p.start(); p.sk("init")
            if hub:
                r = p.sk("hub", "on", "demo"); assert r.returncode == 0, r.stdout + r.stderr
            assert p.sk("bridge", "on", "1h").returncode == 0
        self.alice, self.bob, self.carol = self.people
        shims = os.path.join(self.base, "shims"); os.makedirs(shims)
        for name, real in (("git", REAL_GIT), ("sankofad", D)):
            path = os.path.join(shims, name)
            with open(path, "w") as f: f.write(SHIM.replace('"$REAL"', f'"{real}"'))
            os.chmod(path, 0o755)
        self.shims = shims

    def start_hub(self):
        self.hp = subprocess.Popen([D, "hub", "serve", "--listen", self.url + "/", "--db", self.hubdb], env=self.henv,
                                   stdout=subprocess.DEVNULL, stderr=open(os.path.join(self.base, "hub.log"), "a"))
        import urllib.request
        for _ in range(300):
            try: urllib.request.urlopen(self.url + "/healthz", timeout=0.2); return
            except Exception: time.sleep(0.02)
        raise SystemExit("hub did not start")

    def stop_hub(self):
        if self.hp and self.hp.poll() is None: self.hp.send_signal(signal.SIGTERM); self.hp.wait()

    def settle(self, cond, secs=4):
        """Wait for the hub to carry writes across (cond: a function returning True when it has)."""
        end = time.time() + secs
        while time.time() < end:
            if cond(): return True
            time.sleep(0.05)
        return cond()

    def sync(self):
        """Through a hub: wait until every device's replica holds the same events."""
        if not self.hub: return
        ids = lambda p: [e["id"] for e in p.ledger()]
        self.settle(lambda: ids(self.alice) == ids(self.bob) == ids(self.carol))

    def stop(self):
        for p in self.people: p.stop()
        self.stop_hub()
        shutil.rmtree(self.base, ignore_errors=True)

    def crashing(self, p, at, when, *args):
        """Run sankofa as p, killed before or after counted call number at (0: never). Returns
        (killed, CompletedProcess-like, calls)."""
        count = os.path.join(self.base, "count")
        for x in (count, count + ".log"):
            if os.path.exists(x): os.remove(x)
        env = dict(p.env, PATH=self.shims + ":" + p.env["PATH"], CRASH_COUNT=count, CRASH_AT=str(at), CRASH_WHEN=when,
                   SANKOFA_DAEMON_BIN=os.path.join(self.shims, "sankofad"))
        r = subprocess.run([S, *args], cwd=p.dir, env=env, capture_output=True, text=True, start_new_session=True)
        calls = open(count + ".log").read().splitlines() if os.path.exists(count + ".log") else []
        if when.startswith("fail"):   # it happened if the call at that number was a network one
            return any(c == f"{at} NETFAIL" for c in calls), r, [c for c in calls if not c.endswith("NETFAIL")]
        return r.returncode == -signal.SIGKILL, r, calls


def setup_handoff(w, token):
    a = w.alice
    a.git("switch", "-q", "-c", "feat")
    with open(os.path.join(a.dir, "f.txt"), "w") as f: f.write(token + "\n")
    a.git("add", "f.txt"); a.git("commit", "-qm", "work " + token)
    assert a.sk("todo", "finish " + token).returncode == 0


def remote_state(w, p):
    out = p.git("ls-remote", "origin", "refs/heads/feat", "refs/sankofa/*")
    refs = {}
    for line in out.splitlines():
        sha, ref = line.split("\t")
        if not ref.endswith("^{}"): refs[ref] = sha
    return refs


def journals(p):
    return [j for j in ("handoff", "pickup") if os.path.exists(os.path.join(p.dir, ".sankofa", j + ".json"))]


def check_delivered(w, token, sender_sha, bad, winner=None):
    """Exactly one of Bob and Carol has feat at the sender's commit with the letter's todo, once."""
    have = []
    for q in (w.bob, w.carol):
        todos = [e for e in q.open_items("todo") if ("finish " + token) in e["text"]]
        on = q.branch() == "feat" and q.git("rev-parse", "HEAD") == sender_sha
        if on: have.append(q)
        if len(todos) > 1: bad(f"{q.name} carried the letter's todo {len(todos)} times")
        if on and len(todos) != 1: bad(f"{q.name} picked up but carried {len(todos)} todos")
        if journals(q): bad(f"{q.name} has a journal left: {journals(q)}")
    if len(have) != 1: bad(f"picked up by {[q.name for q in have]} (want exactly one)")
    if winner and have and have[0] is not winner: bad(f"picked up by {have[0].name}, want {winner.name}")
    refs = remote_state(w, w.alice)
    if "refs/sankofa/handoff/feat" in refs: bad("the letter is still on the remote after pickup")
    return have[0] if len(have) == 1 else None


def check_sender_closed(w, who, bad):
    a = w.alice
    a.sk("bridge", "fetch")
    w.sync()
    led = a.ledger()
    hs = [e for e in led if e["type"] == "handoff"]
    if len([e for e in hs if e.get("session") != "sankofad"]) != 1: bad(f"Alice has {len(hs)} handoff events: {[(e['id'], e['session']) for e in hs]}")
    if any(e.get("session") == "sankofad" for e in hs): bad("Alice's bridge offered Alice her own letter")
    if a.open_items("handoff"): bad("Alice's handoff is still open after the pickup")
    if who:
        dones = [e for e in led if e["type"] == "done" and any(h["id"] == e.get("ref") for h in hs)]
        say = f"picked up on {who.name.lower()}-pc" if w.hub else f"by {who.name}"
        if not any(say in d["text"] for d in dones): bad(f"Alice's handoff not closed as {say!r}: {[d['text'] for d in dones]}")
    if journals(a): bad(f"Alice has a journal left: {journals(a)}")


def case_handoff(tmp, at, when, mode):
    w = World(tmp, f"h{at}{when[0]}", hub=mode != "git", refuse=mode == "hub-refuse")
    errs = []; bad = errs.append
    try:
        token = f"tok{at}{when[0]}"
        setup_handoff(w, token)
        sha = w.alice.git("rev-parse", "HEAD")
        killed, r, calls = w.crashing(w.alice, at, when, "handoff", "letter " + token)
        if not killed: return (None if at > len(calls) else False), calls, errs
        refs = remote_state(w, w.alice)
        o = w.alice.sk("open")
        half = refs.get("refs/heads/feat") == sha and not any(e["type"] == "handoff" for e in w.alice.ledger())
        if half and "interrupted" not in o.stdout + o.stderr: bad(f"crash left the branch pushed and no handoff, and open says nothing: {o.stdout.strip()[:100]}")
        # the sender's bridge runs before they notice: it must not offer them their own letter
        w.alice.sk("bridge", "fetch")
        if any(e["type"] == "handoff" and e["session"] == "sankofad" for e in w.alice.ledger()): bad("bridge offered Alice her own cut-off letter")
        r2 = w.alice.sk("handoff", "letter " + token)
        if r2.returncode != 0: bad(f"handoff again failed: {(r2.stdout + r2.stderr).strip()[:160]}")
        if journals(w.alice): bad(f"journal left after handoff again: {journals(w.alice)}")
        w.sync()
        r3 = w.bob.sk("pickup")
        if r3.returncode != 0: bad(f"Bob's pickup failed: {(r3.stdout + r3.stderr).strip()[:160]}")
        elif "letter " + token not in r3.stdout: bad("Bob's pickup did not print the letter")
        w.sync()
        r4 = w.carol.sk("pickup")
        if "nothing handed off" not in r4.stdout: bad(f"Carol could pick up too: {(r4.stdout + r4.stderr).strip()[:120]}")
        who = check_delivered(w, token, sha, bad, winner=w.bob)
        check_sender_closed(w, who, bad)
        return True, calls, errs
    finally:
        w.stop()


def case_pickup(tmp, at, when, mode):
    w = World(tmp, f"p{at}{when[0]}", hub=mode != "git", refuse=mode == "hub-refuse")
    errs = []; bad = errs.append
    try:
        token = f"tok{at}{when[0]}"
        setup_handoff(w, token)
        sha = w.alice.git("rev-parse", "HEAD")
        r = w.alice.sk("handoff", "letter " + token)
        assert r.returncode == 0, r.stderr
        w.sync()
        killed, r, calls = w.crashing(w.bob, at, when, "pickup")
        if not killed: return (None if at > len(calls) else False), calls, errs
        w.sync()
        refs = remote_state(w, w.bob)
        if mode == "hub-refuse":
            claimed = not w.bob.open_items("handoff")
        else:
            claimed = "refs/sankofa/handoff/feat" not in refs
        finished = w.bob.branch() == "feat" and any(("finish " + token) in e["text"] for e in w.bob.open_items("todo")) and not journals(w.bob)
        o = w.bob.sk("open")
        if claimed and not finished and "interrupted" not in o.stdout + o.stderr:
            bad(f"Bob's claim landed but the pickup did not finish, and open says nothing: {o.stdout.strip()[:100]}")
        # Carol tries while Bob is still looking: she may take it only if Bob's claim never landed
        r4 = w.carol.sk("pickup")
        carol_got = r4.returncode == 0 and "picked up feat" in r4.stdout
        if carol_got and claimed: bad("Carol picked up a handoff Bob had already claimed")
        w.sync()
        r3 = w.bob.sk("pickup")
        out = r3.stdout + r3.stderr
        if carol_got:
            if r3.returncode == 0 and "picked up feat" in r3.stdout: bad("Bob also picked it up after Carol")
            winner = w.carol
        else:
            if r3.returncode != 0 and not finished: bad(f"Bob's pickup again failed: {out.strip()[:160]}")
            if r3.returncode == 0 and finished and "nothing handed off" not in out: bad(f"Bob's second pickup after a finished one: {out.strip()[:120]}")
            winner = w.bob
        w.sync()
        who = check_delivered(w, token, sha, bad, winner=winner)
        if journals(w.bob): bad(f"Bob's journal left: {journals(w.bob)}")
        check_sender_closed(w, who, bad)
        return True, calls, errs
    finally:
        w.stop()


def case_race(tmp, n, mode):
    """Bob and Carol pick up the same handoff at the same moment: exactly one gets it."""
    import threading
    w = World(tmp, f"r{n}", hub=mode != "git", refuse=mode == "hub-refuse")
    errs = []; bad = errs.append
    try:
        token = f"race{n}"
        setup_handoff(w, token)
        sha = w.alice.git("rev-parse", "HEAD")
        assert w.alice.sk("handoff", "letter " + token).returncode == 0
        w.sync()
        res = {}
        go = lambda q: res.__setitem__(q.name, q.sk("pickup"))
        ts = [threading.Thread(target=go, args=(q,)) for q in (w.bob, w.carol)]
        for t in ts: t.start()
        for t in ts: t.join()
        won = [k for k, r in res.items() if r.returncode == 0 and "picked up feat" in r.stdout]
        if len(won) != 1: bad(f"race won by {won}: " + " | ".join(f"{k}: {(r.stdout + r.stderr).strip()[:120]}" for k, r in res.items()))
        w.sync()
        who = check_delivered(w, token, sha, bad)
        check_sender_closed(w, who, bad)
        return True, [], errs
    finally:
        w.stop()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", choices=["handoff", "pickup"])
    ap.add_argument("--mode", choices=["git", "hub", "hub-refuse", "all"], default="all",
                    help="git: nothing but the remote; hub: a hub too; hub-refuse: a hub, and a remote that refuses refs/sankofa/*")
    ap.add_argument("--out")
    ap.add_argument("--races", type=int, default=15, help="simultaneous pickups per mode")
    ap.add_argument("--verbose", action="store_true")
    a = ap.parse_args()
    tmp = tempfile.mkdtemp(prefix="sk-crash-", dir=os.environ.get("XDG_RUNTIME_DIR") or "/tmp")
    global S
    S, _ = handoff.snapshot(tmp)
    t0 = time.time()
    rows, failures = [], []
    modes = ["git", "hub", "hub-refuse"] if a.mode == "all" else [a.mode]
    for mode, (cname, case) in [(m, c) for m in modes for c in (("handoff", case_handoff), ("pickup", case_pickup))]:
        if a.only and a.only != cname: continue
        name = f"{cname} ({mode})"
        n = {"crash points": 0, "cases": 0, "failed": 0}
        at = 1
        while True:
            done = True
            for when in ("before", "after", "fail-before", "fail-after"):
                ran, calls, errs = case(tmp, at, when, mode)
                if ran is None: continue
                done = False
                if ran is False: continue   # a network failure at a call that is not a network one
                n["cases"] += 1
                step = calls[at - 1].split(" ", 1)[1][:70] if len(calls) >= at else "?"
                if errs:
                    n["failed"] += 1
                    failures.append(f"{name} killed {when} call {at} ({step}):")
                    failures += [f"    {e}" for e in errs]
                    print(f"  ! {name} {when} {at} ({step}): " + "; ".join(errs)[:400], file=sys.stderr)
                elif a.verbose:
                    print(f"  ok {name} {when} {at} ({step})", file=sys.stderr)
            if done: break
            n["crash points"] = at
            at += 1
        rows.append((name, n))
        print(f"{name}: {n}", file=sys.stderr)
    for mode in modes:
        if a.only: break
        name = f"race ({mode})"
        n = {"crash points": "-", "cases": 0, "failed": 0}
        for i in range(a.races):
            _, _, errs = case_race(tmp, i, mode)
            n["cases"] += 1
            if errs:
                n["failed"] += 1
                failures.append(f"{name} #{i}:"); failures += [f"    {e}" for e in errs]
                print(f"  ! {name} #{i}: " + "; ".join(errs)[:400], file=sys.stderr)
        rows.append((name, n))
        print(f"{name}: {n}", file=sys.stderr)
    shutil.rmtree(tmp, ignore_errors=True)
    out = ["# Crashes in handoff and pickup", "",
           "Killed before and after each git command and sankofad call, and each push, fetch and",
           "ls-remote failed before reaching the remote and after (it happened, the answer was lost),",
           "then run again;",
           "and races: two people pick up the same handoff at the same moment.", "",
           "| command | crash points | cases | failed |", "|---|---|---|---|"]
    out += [f"| {k} | {v['crash points']} | {v['cases']} | {v['failed']} |" for k, v in rows]
    if failures: out += ["", "Failures:", ""] + failures
    out += ["", f"Wall time {time.time() - t0:.0f} s"]
    text = "\n".join(out) + "\n"
    print(text)
    if a.out:
        with open(a.out, "w") as f: f.write(text)
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
