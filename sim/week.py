#!/usr/bin/env python3
"""Accelerated week for the Stop rule: replay a synthetic week of Claude Code
sessions against the real `sankofa` CLI and Stop hook, and score the rule.

The repo is a real git working tree. Each session has a real transcript file. Agents
edit files with the Edit tool (logged in the transcript) or through Bash (only the
tree changes), commit sometimes, fan out subagents that edit too, run several
sessions side by side in one tree, and write to the ledger through the real CLI. When
a stop is blocked the agent complies: it writes a note, then stops again with
stop_hook_active=true, as Claude Code does.

Rules:
  hook     the hook in bin/sankofa as it is now (measured, not modeled)
  0.1.0    the first shipped rule, modeled: no ledger write by anyone since the last
           main stop. Cross-checked against the real 0.1.0 hook (mismatch was 0).

Ground truth for every blocked stop (the hook cannot know which turn is last):
  useful  last turn of the session, with edits not yet written up: a handoff saved
  early   mid-session, with edits not yet written up: friction, the note may go stale
  nag     nothing was owed; the forced note is filler
Missed handoff: a session ends with edits it never wrote up.

Usage: sim/week.py [--seeds N] [--p-note 0.2,0.5,0.8] [--days 7] [--rules hook,0.1.0] [--out FILE]
"""
import argparse, datetime, json, os, random, shutil, subprocess, sys, tempfile, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLI = os.path.join(ROOT, "bin", "sankofa")
ARCHETYPES = [("ask", 25), ("work", 35), ("finish", 15), ("resume", 10), ("fanout", 10), ("human-step", 5)]
CLEAN_ENV = ("CLAUDE_CODE_SESSION_ID", "CLAUDE_SESSION_ID", "SANKOFA_SESSION", "SANKOFA_AGENT",
             "SANKOFA_REQUIRE_NOTE")


def ms_now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


class Repo:
    """A git working tree with a sankofa ledger, plus a transcript directory outside it."""

    def __init__(self, enforce, base=None):
        self.dir = tempfile.mkdtemp(prefix="sankofa-week-", dir=base)
        self.tx = tempfile.mkdtemp(prefix="sankofa-tx-", dir=base)
        self.enforce = enforce
        self.git("init", "-q", ".")
        self.git("config", "user.email", "sim@sim"); self.git("config", "user.name", "sim")
        os.makedirs(os.path.join(self.dir, "src"))
        self.touch("src/README", "start\n"); self.git("add", "-A"); self.git("commit", "-qm", "init")
        self.cli(None, "init")

    def cleanup(self):
        shutil.rmtree(self.dir, ignore_errors=True); shutil.rmtree(self.tx, ignore_errors=True)

    def git(self, *args):
        subprocess.run(["git", *args], cwd=self.dir, check=True, capture_output=True)

    def touch(self, rel, text):
        with open(os.path.join(self.dir, rel), "a") as f:
            f.write(text)

    def env(self, session):
        e = {k: v for k, v in os.environ.items() if k not in CLEAN_ENV}
        e["SANKOFA_SESSION"] = session or "-"
        if not self.enforce:
            e["SANKOFA_REQUIRE_NOTE"] = "0"
        return e

    def cli(self, session, *args, stdin=None):
        r = subprocess.run([CLI, *args], cwd=self.dir, env=self.env(session), input=stdin,
                           capture_output=True, text=True)
        return r.returncode, r.stdout.strip()

    def transcript(self, session):
        return os.path.join(self.tx, f"{session}.jsonl")

    def tool(self, session, name):
        entry = {"type": "assistant", "timestamp": ms_now(), "sessionId": session,
                 "message": {"content": [{"type": "tool_use", "name": name, "input": {}}]}}
        with open(self.transcript(session), "a") as f:
            f.write(json.dumps(entry) + "\n")
        time.sleep(0.002)

    def stop(self, session, active):
        payload = json.dumps({"session_id": session, "transcript_path": self.transcript(session),
                              "stop_hook_active": active, "last_assistant_message": "turn done",
                              "hook_event_name": "Stop"})
        rc = self.cli(session, "hook", "stop", stdin=payload)[0]
        time.sleep(0.002)
        return rc

    def subagent_stop(self, session, name):
        payload = json.dumps({"session_id": session, "agent_name": name, "last_assistant_message": "report"})
        return self.cli(session, "hook", "subagent-stop", stdin=payload)[0]


class Session:
    def __init__(self, sid):
        self.sid = sid
        self.unwritten_edits = 0  # ground truth: edits since this session's last ledger write


def plan_week(rng, days):
    """Scripted sessions per day: (sid, kind, turns, interleave_with_previous)."""
    week, n_sid, resumable = [], 0, []
    for day in range(days):
        today = []
        for _ in range(rng.randint(3, 8)):
            kind = rng.choices([a for a, _ in ARCHETYPES], [w for _, w in ARCHETYPES])[0]
            if kind == "resume" and resumable:
                sid = rng.choice(resumable)
            else:
                n_sid += 1
                sid = f"d{day+1}-s{n_sid}"
                kind = "work" if kind == "resume" else kind
            turns = rng.randint(1, 3) if kind == "ask" else rng.randint(3, 8)
            today.append((sid, kind, turns, rng.random() < 0.2))
            if kind in ("work", "fanout", "finish"):
                resumable.append(sid)
        week.append(today)
    return week


def schedule(today):
    """Flatten a day into turns; an interleaved session alternates with the one before it."""
    slots, i = [], 0
    while i < len(today):
        cur = today[i]
        if i + 1 < len(today) and today[i + 1][3]:
            a = [(cur, t) for t in range(cur[2])]
            b = [(today[i + 1], t) for t in range(today[i + 1][2])]
            while a or b:
                if a: slots.append(a.pop(0))
                if b: slots.append(b.pop(0))
            i += 2
        else:
            slots += [(cur, t) for t in range(cur[2])]
            i += 1
    return slots


def run(rule, week, p_note, seed, base=None, on_start=None, on_day_end=None):
    """Replay one week in a fresh repo. on_start(repo) runs before the first session;
    on_day_end(day_index) runs after each day (used by sim/notify.py)."""
    rng = random.Random(seed * 1000 + 7)  # behaviour rng, identical across rules
    repo = Repo(enforce=(rule == "hook"), base=base)
    open_items = [repo.cli("seed", "todo", "decide on the exit-2 rule")[1]]
    if on_start:
        on_start(repo)
    awaiting = set()
    st = dict(stops=0, blocks=0, useful=0, early=0, nag=0, sessions=0, missed=0)
    written_since_main_stop = True  # state for the modeled 0.1.0 rule
    sessions = {}

    def write(s, verb, *args):
        nonlocal written_since_main_stop
        out = repo.cli(s.sid if s else "human", verb, *args)[1]
        written_since_main_stop = True
        if s:
            s.unwritten_edits = 0
        return out

    def edit(s, who="main"):
        if who == "main" and rng.random() < 0.6:
            repo.tool(s.sid, "Edit")
        elif who == "main":
            repo.tool(s.sid, "Bash")
        repo.touch(f"src/{s.sid}.txt", f"{who} {ms_now()}\n")
        s.unwritten_edits += 1

    try:
        for today in week:
            for (sid, kind, turns, _), t in schedule(today):
                s = sessions.setdefault(sid, Session(sid))
                st["sessions"] += (t == 0)
                final = (t == turns - 1)
                if kind == "ask":
                    repo.tool(sid, rng.choice(["Read", "Bash", "Grep"]))
                elif rng.random() < 0.8:
                    edit(s)
                if kind == "fanout" and t in (1, 2):
                    for k in range(rng.randint(2, 4)):
                        if rng.random() < 0.5:
                            edit(s, who=f"worker{k}")
                        repo.subagent_stop(sid, f"worker{k}")
                if kind == "finish" and final and open_items:
                    write(s, "done", open_items.pop(0), "finished")
                if kind == "human-step" and t == 1:
                    aid = write(s, "awaiting", f"sudo pacman -U the package ({sid})")
                    open_items.append(aid); awaiting.add(aid)
                if kind == "work" and final and rng.random() < 0.3:
                    open_items.append(write(s, "todo", "follow-up left open"))
                if s.unwritten_edits and rng.random() < p_note:
                    write(s, "note", "proactive handoff note")
                if kind in ("work", "finish") and final and rng.random() < 0.3:
                    repo.tool(sid, "Bash"); repo.git("add", "-A"); repo.git("commit", "-qm", f"{sid} work")

                owed, has_open = s.unwritten_edits > 0, bool(open_items)
                st["stops"] += 1
                rc = repo.stop(sid, False)
                blocked = (rc == 2) if rule == "hook" else (has_open and not written_since_main_stop)
                if blocked:
                    st["blocks"] += 1
                    st["useful" if owed and final else "early" if owed else "nag"] += 1
                    write(s, "note", "note forced by the stop hook")
                    assert repo.stop(sid, True) == 0
                written_since_main_stop = False
                if final and s.unwritten_edits:
                    st["missed"] += 1
            pending = [i for i in open_items if i in awaiting]
            if pending and rng.random() < 0.5:  # overnight the human clears one awaiting item
                open_items.remove(pending[0]); awaiting.discard(pending[0])
                write(None, "done", pending[0], "human did it")
            if on_day_end:
                on_day_end(week.index(today))
    finally:
        repo.cleanup()
    return st


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seeds", type=int, default=5)
    ap.add_argument("--p-note", default="0.2,0.5,0.8")
    ap.add_argument("--days", type=int, default=7)
    ap.add_argument("--rules", default="hook,0.1.0")
    ap.add_argument("--tmp", help="directory for temporary repos")
    ap.add_argument("--out")
    a = ap.parse_args()
    rows, n = [], a.seeds
    for p in [float(x) for x in a.p_note.split(",")]:
        for rule in a.rules.split(","):
            tot = {}
            for seed in range(n):
                for k, v in run(rule, plan_week(random.Random(seed), a.days), p, seed, a.tmp).items():
                    tot[k] = tot.get(k, 0) + v
            rows.append((p, rule, tot))
            print(f"p_note={p} rule={rule} done", file=sys.stderr)
    lines = [f"# Accelerated week (Stop rule): {n} seeds x {a.days} days, per-week averages", "",
             "| proactive note rate | rule | sessions | stops | blocks | useful | early | nag | missed handoffs |",
             "|---|---|---|---|---|---|---|---|---|"]
    for p, rule, t in rows:
        lines.append(f"| {p:.1f} | {rule} | {t['sessions']/n:.0f} | {t['stops']/n:.0f} | {t['blocks']/n:.1f} | "
                     f"{t['useful']/n:.1f} | {t['early']/n:.1f} | {t['nag']/n:.1f} | {t['missed']/n:.1f} |")
    report = "\n".join(lines) + "\n"
    print(report)
    if a.out:
        open(a.out, "w").write(report)


if __name__ == "__main__":
    main()
