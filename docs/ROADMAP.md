# Roadmap

Rules: one iteration at a time; every iteration has tests that run with
`make test`; each ships as a git tag. An iteration's usage gate ("used for a
week") is run as an accelerated simulation under `sim/` that drives the real
code with synthetic sessions; real use continues alongside but does not block
the next iteration (decided 2026-11-10).

## 0.1 · handoff (shell)

Deliver: `bin/sankofa` (POSIX sh) with `note`, `todo`, `awaiting`, `done`,
`open`, `last`, `log`; hooks `hooks/stop.sh` and `hooks/subagent-stop.sh`
that append a structured entry to `.sankofa/ledger.jsonl` in the repo (JSONL,
not markdown: jq can query it and `log` renders it); `AGENTS.md` stanza;
install script that wires the hooks into Claude Code settings.

Tests (`tests/0.1/*.sh`, plain sh, no framework):
- feeding a Stop-hook JSON payload produces one ledger entry with timestamp,
  session id, summary, open items;
- `sankofa open` lists only unresolved items; `sankofa last` prints the newest;
- hook exits 2 when the session tries to stop with no note and the ledger
  has open items older than this session (configurable off).

Exit: used for a week on a real project. Decide if the "exit 2" rule is a help
or a nag.

Outcome (0.1.1, `sim/week.py`): the first rule (block when nothing was written
since the last stop) was a nag, about 40 pointless blocks a week. The rule now
blocks only when this session edited files (transcript Edit/Write, or a changed
git working tree) since its own last ledger write.

## 0.2 · notify (shell + systemd user unit)

Deliver: `sankofa-watch` service: inotify on every registered ledger, toast on
new `awaiting` or `note` entries; `sankofa watch add <repo>`.

Tests: fake `notify-send` on PATH; append an entry; assert one toast with the
expected title.

Outcome (0.2.0, `sim/notify.py`): exactly-once delivery across a simulated
reboot, sub-second latency. Note toasts were about 97% of all toasts, so the
default is `awaiting` only and notes are opt-in (`SANKOFA_TOAST_TYPES`).
Offsets persist in `$XDG_STATE_HOME/sankofa/offsets`; bursts become one
summary toast.

## 0.3 · sankofad (C#, Native AOT)

Deliver: daemon on a Unix socket; SQLite; events table (append-only); leases
table (path glob, holder, expires); verbs `claim`, `release`, `ask`, `note`,
`done`, `events --follow`. `bin/sankofa` becomes a thin client; falls back to
the 0.1 markdown ledger when no daemon is running.

Tests: xUnit unit tests for lease logic (overlap, expiry, renew); integration
test that starts the daemon on a temp socket and drives it with the CLI.

Outcome (0.3.0): the ledger stays the per-repo JSONL file, now mirrored by the
daemon and imported from when the fallback wrote it, so 0.1 and 0.2 work
unchanged and every shell test passes both with and without the daemon. Leases
are claim events (expiry, dirty mark, renew, release, done); a session and its
subagent are separate holders. Writes carry a nonce so an answer lost in a
crash is never written twice. `sim/daemon.py` ran three repos for a simulated
week each against one daemon killed with SIGKILL about eight times a run:
about 1,000 acknowledged writes a run, about 320 through the fallback, none
lost, doubled, or out of order, and database and files agreed. `sankofad
bench`: startup 4 ms median, `ask` 0.07 ms median on an open socket and 2.5 ms
as a fresh `sankofad send` process. Through the shell CLI (sh + jq) `ask` takes
about 15 ms, so the 0.4 edit gate should call `sankofad send` directly.

## 0.4 · gates

Deliver: `hooks/pre-edit.sh` (PreToolUse for Edit/Write/MultiEdit): asks
`claim`, denies with a message naming the holder; `hooks/git-pre-commit`:
refuses commits touching paths leased to another holder; allow-and-flag when
the daemon is unreachable.

Tests: two holders, same path, second edit denied; expired lease, second edit
allowed; daemon down, edit allowed and a `flag` event written on reconnect.

Outcome (0.4.0): the gate logic lives in `sankofad gate` (about 4 ms per edit
as a process), with one adapter per client: Claude Code (`agent_id` names the
subagent) and Codex (`apply_patch` headers name the files). SubagentStop and
SessionEnd finish the holder's leases. `sim/gates.py` ran up to five agents at
once on one repo, with and without the gate. Without it, about 17 clobbers per
run; with it, none. The first version let a subagent take over its parent's
file at once, and that clobbered the parent's in-flight edit about 3 times a
run; a subagent now takes over only after the parent has left the file alone
for `handover_idle` (default 5s), and the rerun had no clobbers across about 4
handovers a run.

Exit, a real run (2026-11-10): two Claude Code background subagents edited one
shared header at once. In the first run, beta's edits were denied with alpha
named as the holder, and nothing was lost. But SubagentStop runs in the main
session's directory, so it finished alpha's leases in the wrong repo, and beta
was locked out for the full lease. Leases now end with their holder in every
repo. In the rerun, beta was denied twice while alpha held the header, got it
1.5 s after alpha finished, and the file kept both agents' lines. The run also
confirmed that `agent_id` is the same in PreToolUse and SubagentStop.

## 0.5 · mcp

Deliver: `sankofad --mcp` stdio server exposing the same verbs; config snippets
for Claude Code and Codex.

Tests: drive the server with a minimal MCP client script; same assertions as
0.3 through the tool interface.

Outcome (0.5.0): `sankofa-mcp`, also run as `sankofad mcp`, is built on the
official C# SDK's low-level API and runs the CLI for each tool. It is a
separate binary, because linking the SDK into `sankofad` slowed the edit gate
from 3.3 to 5.1 ms. `sim/mcp.py` ran four clients at once, two named Claude
Code and two Codex, against a daemon killed twice a run. It found duplicate
ids from concurrent fallback writers, now fixed by a lock on the ledger file;
after the fix, about 540 writes a run with none lost or doubled. Exit, real:
`claude -p` and `codex exec` wrote to one ledger on one repo in the same
sitting, and Codex's claim was refused with the Claude Code holder named. Codex
starts MCP servers without `XDG_RUNTIME_DIR`, so the socket path now falls back
to `/run/user/<uid>`. Codex needs `default_tools_approval_mode = "approve"` on
the server to call the tools from `codex exec`.

## 0.6 · watcher and bridge

Deliver: inotify over registered worktrees; `overlap` event when two trees
touch one path; `git fetch` loop recording `landed` events per branch.

Tests: two temp worktrees, same path touched, one overlap event; a bare remote
receives a push, one landed event.

Outcome (0.6.0): both run inside `sankofad`. Overlaps: inotify (direct, one
watch per source directory; `.git/`, ignored directories and nested repos are
skipped) wakes a git scan 300 ms after a burst settles; a tree's changes count
from its merge base with the home tree, so worktree commits count until merged.
`sim/overlap.py` (5 trees, 1200 steps, inotify only) expected 261 overlaps and
recorded exactly those: none missed, false or repeated, about 350 ms after the
last write. Bridge: remote branches are compared with what the daemon last
saw, kept in SQLite, so pushes made while the laptop slept or was off are
reported on the first fetch after waking, and a boot fetches at once.
`sim/overnight.py` (42 nights, SIGSTOP or reboot each night, a quarter of
mornings with the network down) had 94 branches moved by routines and 94
landed events, each toasted; it found that a human's own push followed by one
more local commit was reported as a landing, now fixed by asking whether any
local branch contains the tip. Exit, real: on this machine a routine pushed to
a bare remote while `sankofad` was stopped; on start the landed event was
recorded and the installed watcher toasted it. The GitHub remote of this repo
is bridged; the overnight check against a real scheduled push is still open.

## 0.7 · hub

Deliver: HTTP listener (MCP streamable HTTP + a small JSON API), device
tokens, systemd unit for a server; client config for a remote hub; offline
allow-and-flag.

Tests: two clients against one hub, lease contention honored; hub down, local
allow-and-flag, reconciliation on return.

Outcome (0.7.0): `sankofa-hub` (also `sankofad hub`) serves the socket
protocol as JSON over HTTP, a live event stream, and MCP streamable HTTP, with
device tokens (hashes only). A device's `sankofad` forwards an attached repo's
verbs to the hub and keeps `.sankofa/ledger.jsonl` as a replica, so hooks and
the watcher are unchanged. Offline writes are queued in the replica (`q...`
ids) and replayed in order on return; the hub maps the ids, dedupes by nonce,
and turns a queued claim on a path someone else held into contention plus a
flag. The hub is a separate binary and the device side uses a small HTTP
client of its own: HttpClient doubled `sankofad` and slowed the gate from 3.5
to 4.4 ms. `sim/hub.py` ran 5 simulated weeks of desk, laptop and a cloud MCP
session through proxies that cut or silently blackhole the network, with hub
restarts and a killed laptop daemon: 1749 acknowledged writes, 242 of them
offline, none lost or doubled, replicas identical to the hub at the end, and
all 382 replayed claims on held paths flagged. It found a queued `done` whose
todo had gone up in an earlier reconcile being refused (ids are now mapped
across batches), and edit-gate claims waiting behind a slow reconcile (the gate
now has its own connection and never waits for one). Edit gate through the
hub on one machine: median 5.0 ms, p99 7 ms; offline, median 4.3 ms.

Exit, simulated on separate machines (2026-11-11): `sim/hub.py --docker` runs
the same week with the hub, the desktop and the laptop as three containers on
their own network, sharing nothing else. Each box is set up the way a real one
is: `install.sh` (`--hub` on the hub box), `sankofad hub token add`,
`sankofa hub join http://hub:8787 ...`, `sankofa hub on`, with the daemons
started from the units `install.sh` wrote. On top of the local run's failures,
the laptop is unplugged from the network, firewalled into silence, paused with
its lid shut (frozen processes, stale connections on waking) and rebooted, and
the hub box is killed outright as well as restarted. With the laptop's clock 90 s
ahead (libfaketime), 3 seeds x 7 days: 1042 acknowledged writes, 87 of them
offline, none lost or doubled, both replicas identical to the hub, all 82
replayed claims on held paths flagged; the same without the skew. Edit gate in
a container: median 13 ms, of which the hub adds about 1 ms (a local repo in
the same box takes 11.7 ms; the rest is process start inside a container). A
gate on a silent dead route waits out its 250 ms budget once, then the breaker
answers in about 6 ms. Not covered: Tailscale itself, and whether one shared
ledger helps in daily use across two machines; that is left to real use.

## 0.8 · dashboard (optional)

Deliver: Omarchy-style Qt app or a TUI subscribing to `events --follow`.

Outcome (0.8.0): `dashboard/`, `sankofa-dash`, a Qt Quick app on the Omarchy
app template (theme accent and rounding live, language live, keyboard
first), opened with `sankofa dash`. It follows the ledger files rather than
the daemon's stream: each file is the stream's mirror, and the only copy for
repos shared through a hub (the local daemon forwards those) and for repos
with no daemon running. Panels: waiting on you, open, who holds what
(half-finished edits marked), last handoff per session, and recent overlaps,
landings and flags. Space copies `sankofa done <id>` or a lease's paths.
`sankofa-dash summary [--json]` and `render --out` need no display. Tests
(Qt Test, offscreen): folding, following appended, half-written and replaced
files, the window rendered from a fixture stream with no daemon, and the
keys. Pointed at this repo's own ledger it showed at once what reading the
file by hand had not: 13 leases the edit gate took for this session, expired
and still marked half-finished although their edits were committed.

0.8.1: the daemon settles those. About once a minute (`SANKOFA_SETTLE`), each
ended lease still marked dirty is checked against git; when none of its paths
has an uncommitted change (staged, unstaged or untracked) in any worktree of
the repo, sankofad finishes it with a `done` saying so. In a repo shared
through a hub only the device that held the lease settles it, and only while
the hub answers, so a settle is never queued. On this repo it settled 18 stale
leases and kept the two whose files were being edited.

## 0.9 · handoff between people

Deliver: `sankofa handoff [text]` and `sankofa pickup [branch|id]`, so a
teammate can continue a branch when the first person runs out of credits or
time, with the context, over nothing but the git remote; the hub, the bridge,
the watcher and the dashboard carry it when present.

Tests: handoff refused with uncommitted changes; the branch and the letter
reach the remote; the receiver's bridge records one handoff and no landed
event; pickup switches, prints the letter, turns open items into todos, and
removes the ref; the sender's bridge finishes the handoff; a pickup with no
daemon; through a hub, pickup by id is seen on the sending device; toasts only
on the machines it is for.

Outcome (0.9.0): the letter is a tag object at `refs/sankofa/handoff/<branch>`
(no commit in the branch's history), and a `handoff` event, an open item with
`branch` and `sha`. GitHub accepts, serves and deletes `refs/sankofa/*` refs
(checked on this repo's remote, 2026-11-11: a tag object pushed, fetched by a
second clone with pickup's refspec, then removed with a lease).

0.10.1, simulated (`sim/handoff.py`, 2026-11-11): four people (one with no
daemon) on one bare remote, each with their own clone and sankofad, every
outcome checked against git itself. The first runs found two ways a handoff
could be picked up twice: two people picking up at once both switched (the
letter was removed only after switching), and a pickup offered a ledger copy
of a handoff someone else had already taken. Pickup now takes the handoff
first (removing the letter is the claim; only one removal succeeds) after
checking everything that could stop the switch, and without a hub offers only
letters still on the remote. They also found that the same commit handed off
again by someone else was taken for a repeat and never shown; handoff events
now carry their `letter` (the tag object) and are told apart by it. After the
fixes, 10 seeds x 300 steps: 244 handoffs and 65 re-handoffs, 179 pickups,
35 races for one handoff (none won twice; 2 where both were rightly refused),
every refusal (uncommitted 51, behind the remote 44, dirty 67, several on
offer 69, diverged 31, untracked file in the way 2) where git said it should
be, no commit lost, 3250 open items carried over exactly once, open handoffs
on every machine matching the remote at the end, 277 toasts, none on a
sender's own machine and none missed. Not simulated: handoffs through a hub.

## 0.10 · terminal dashboard

Deliver: `sankofa-tui`, the dashboard in the terminal, for everyone who is not
on Omarchy (and over SSH): Turbo Vision style, on Terminal.Gui v2, Native AOT.
`sankofa dash` opens it; the Qt app stays as `sankofa dash --qt`.

Tests: the fold (`Board`, in Sankofa.Core) on the Qt dashboard's fixtures,
following appended, half-written, replaced and emptied files; `summary` and
`summary --json`; the real binary in a real terminal (tmux): panels, a todo
written outside appearing, `d` resolving an item through the CLI with its
text in the ledger, help, about, zoom, quit.

Outcome (0.10.0): an AOT spike first: Terminal.Gui 2.5.0 published with no
trim or AOT warnings and ran without its Oniguruma library; 9.9 MB, about
85 ms to start and stop. The fold moved into Sankofa.Core so the terminal
dashboard and the daemon share the event types; the Qt dashboard keeps its
own C++ copy. A dialog with a fixed height let its button row hide the text
field (found driving it in tmux), so dialogs size themselves.

## Later / maybe

- Test runner subscriber (run on `done`, write `tests` event).
- Lease by module (a path prefix list) rather than single paths.
