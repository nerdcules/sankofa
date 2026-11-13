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

Dropped (2026-11-12, e202): the Qt app and `sankofa dash --qt` were removed
once the terminal dashboard (0.10) and the window (0.12) covered it; its
fixtures moved to `tests/fixtures`. The outcome below is kept as history.

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
`sankofa dash` opens it; the Qt app stayed as `sankofa dash --qt` until 0.12.

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

0.10.2: colour and motion. Rows take the colour of their kind and panels an
accent of their own (the 16 ANSI colours, so the terminal's theme decides the
shades); a row that just arrived shows in reverse, then bold, then plain over
six seconds while its panel's border flashes; a live line counts repos and
events and ages the last one. The sankofa bird, drawn in half blocks from the
Adinkra silhouette, fades in from dark to gold at start and sits in About.

## 0.11 · the scenarios nobody had run

Deliver: run what the earlier simulations left out, and fix what it finds.
Handoff and pickup killed partway; handoffs through a hub, races included;
the sender's leases after a handoff; branches rewritten or deleted after one,
remotes not called origin, names with slashes; an upgrade over an old
database, and teams that have not all upgraded; the terminal dashboard under
a year of ledger, bursts, small and odd terminals; clocks that disagree.

Tests: `tests/0.11` (the two worst crash points, leases, git states),
`ALongLedgerFoldsInLinearTime`; simulations `sim/crash.py`,
`sim/handoff.py --hub`, `sim/compat.py`.

Outcome (0.11.0, 2026-11-11):

- Crashes (`sim/crash.py`: the command's process group killed before and
  after each of its git and sankofad calls, then run again, with and without
  a hub, and with a hub and a remote that refuses `refs/sankofa/*`). Against
  0.10.2 a pickup killed just after its claim lost the handoff: the letter was
  gone from the remote, nobody was on the branch, and nothing said so. Now
  handoff and pickup keep a journal in `.sankofa/` while they run; running the
  command again finishes it, and `sankofa open` reports one left behind. Pickup
  removes the letter and leaves a marker (`refs/sankofa/taken/<branch>`, a tag
  naming the letter, tagged by the taker) in one atomic push, so after a crash
  either side can tell whether a letter it lost track of was taken or never
  arrived. A finished handoff run again changes nothing (it used to write a
  second letter). The sender's bridge knows its own letters by
  `refs/sankofa/handoff/<branch>` in its own repo, so a letter whose ledger
  event a crash prevented is never offered back to its sender. Network
  failures too: each push, fetch and ls-remote failed before reaching the
  remote, and after the remote acted but before the answer came back. That
  found one more: a claim push lost on the way was taken for a server without
  atomic pushes, and the claim fell back to one without its marker; it now
  falls back only when the server says so (no atomic push, or the marker ref
  refused), and otherwise asks for pickup to be run again. Final run: 396
  cases across the three modes and 45 races, none failing.
- Through a hub (`sim/crash.py`, `sim/handoff.py --hub`): a pickup through
  git could be taken again from the hub's copy before its done arrived (two
  people on the branch); a ledger-only pickup now checks the remote's marker
  first. A handoff run again on a branch already on offer through a hub kept
  both open; a new handoff now finishes the sender's earlier ones for that
  branch. Then 10 seeds x 300 steps through a hub: 239 handoffs, 66 rehands,
  185 pickups, 33 races (none won twice), no commit lost, open handoffs right
  on every device, 915 toasts, none on the sender and none missed; and 15
  simultaneous pickups per mode, each won by exactly one.
- Leases: a handoff finishes the leases its session holds in the repo, so a
  teammate picking up through a hub is not blocked until they expire.
- Git states: a branch rewritten after the handoff, or deleted from the
  remote, still picks up at the handed-off commit and says what became of the
  branch; with several remotes, none called origin, and a branch tracking
  none, handoff refuses instead of pushing to the first in alphabetical order.
  Todos carried from a letter skip ones already open (the same item in an
  earlier letter, or a pickup finished after a crash): the git-only run now
  carries 1335, all expected.
- Versions (`sim/compat.py`, old releases built from their tags): a v0.8.1
  database and ledger open in 0.11 with the todo open and the lease listed;
  v0.10.2 and 0.11 hand off to each other both ways; 0.11 devices on a
  v0.10.2 hub, and a v0.10.2 device on a 0.11 hub, sending and picking up.
- The terminal dashboard: every lease done looked at every lease the repo
  ever had, so a 200,000-event ledger took 25 s to open (50,000: 0.75 s); now
  0.66 s. A burst of 20,000 events kept it at 90% CPU and never on screen,
  since each list was cleared and refilled row by row; lists are now replaced
  in one step, and the burst shows within half a second. Resolved items and
  finished leases are dropped from the fold, and the record of rows seen is
  pruned: a 90 s soak at 400 events/s held at 42 to 48 MB and 8% CPU. 40x12,
  20x6, resizing, `NO_COLOR`, `TERM=dumb` and `vt100`, a truncated, replaced or
  deleted ledger and garbage lines all render and keep running.
- Clocks: lease expiry with a clock 90 s ahead was already simulated
  (`sim/hub.py --docker --skew`); the dashboard shows a timestamp from the
  future as "now".

## 0.12 · the dashboard in a window, everywhere

Deliver: the four designs from the mock-ups (constellation for Windows, device
map for macOS, lease heatmap for Linux, radar for Omarchy) as one program
that runs on all three operating systems, with free licences and the least
new code.

Choice (2026-11-12): C# on Avalonia 12 (MIT). It reuses `Board` and
`BoardFiles` from Sankofa.Core unchanged, so the folding rules are written
once for the terminal and the window. It builds with the toolchain the daemon
already needs (`dotnet`), as Native AOT on Linux and as one self-contained
file for Windows and macOS cross-built from Linux (`make gui-dist`). The Qt
window would have meant a second copy of the folding in C++, a Qt SDK on every
build machine, and LGPL obligations to track when shipping. MAUI has no
Linux. Electron, Tauri and Flutter would mean a second language.

Tests: `tests/0.12/test_gui.sh`. The scene comes from the fixtures and from a
live ledger the CLI writes. All 16 look and picture pairs are rendered
headless to PNGs. The Omarchy look is checked by pixel against a theme file.
`tests/0.1/test_install.sh` checks the launcher entry.

So far (2026-11-12): built and passing `make test` on Linux. The
Native AOT window is 20 MB beside Skia and HarfBuzz (13 MB together), answers
`version` in about 14 ms, and renders one picture headless in about 120 ms.
The self-contained single files for Windows, macOS and Linux arm64 are 47 to
50 MB each (`make gui-dist`); they are built here and not yet run on those
systems. `packaging/arch/PKGBUILD` builds and installs the window when the
tree has it. The four pictures rendered from the fixtures are in
`docs/images` (made by `sankofa-gui render`; the only images the repo keeps).
Exit: run the window on a Windows and a macOS machine against a hub replica.

## Security review (2026-11-12)

Every shell script, the daemon, the hub, the gates, the MCP server, the bridge
and the three dashboards were read for what text written by someone else can
do: ledger lines, hook payloads, git output, handoff letters fetched from a
remote, hub answers. `dotnet list package --vulnerable` is clean for every
project. Fixed, each with a test (`tests/0.12/test_hostile_text.sh`,
`GlobTests`, `MiniHttpTests`):

- `pickup` built a sed program out of the letter and the tagger's name, both
  fetched from the remote; a name with a slash could turn the letter into a
  command. The open items are now parsed as data (awk, the name in the
  environment), and letters, tagger names and `sankofa last` drop control
  bytes before the terminal sees them. The same for every dashboard: text is
  made printable, and the command a row offers to copy (`sankofa done <id>`,
  `sankofa ask <path>`) quotes an id, glob or file name that is not plainly
  safe.
- `**` in a glob was matched by recursion, exponential in the number of `**`;
  one claim could stall the daemon under its lock. The matcher now backs off
  from the last `**` only (linear in glob times path).
- A hub answer with a negative or giant Content-Length, or a bad chunk size,
  threw on a pool thread and ended the device's daemon; the reader now refuses
  it as an I/O fault (header lines over 64 KB and bodies over 64 MB too), and
  a reconcile that fails is logged and trips the breaker.
- A follow stream and the hub's event listing sent one page of 10,000 events
  and then went live, so a replica away for longer kept a hole; both page
  through the backlog.
- The hub reads at most 16 MB of request body (413 above), clamps `last n`
  to 200 (the MCP server too), and a queued line it cannot apply becomes a
  flag instead of failing the batch.
- The daemon refuses to put its socket in a directory it does not own
  (`/tmp/sankofa-<user>` on a host without `XDG_RUNTIME_DIR` could be
  pre-created by someone else).
- Smaller: SQLite text is bound by byte count (an embedded NUL no longer makes
  the row differ from the JSONL mirror); the pre-commit gate drains git's
  stderr (a chatty git could hang the commit); `git fetch` gets `--` before
  the remote name; the watcher's inotify mask has `IN_DONT_FOLLOW` and its
  thread survives a failed scan; the MCP server kills a CLI call after 120 s
  and caps output at 1 MB; `notify-send` gets `--` before the title;
  `install.sh` keeps its settings backups at mode 600; the window clamps
  `--size`.
- Found by the new test: `sankofa open`, the handoff letter and the toasts
  rendered text through jq's `@tsv`, which doubles every backslash, so a todo
  with `\2` in it came out as `\\2` and as `\\\\2` after a pickup. They join
  the fields with a tab instead.

Noted and left as is: device tokens are attribution, not authorization (by
design, see OBJECTIVE); the edit gate compares paths as spelled, so a symlinked
parent is a different path (cooperative control); the git pre-commit gate
skips when `sankofad` is not installed (documented); `git worktree list` is
parsed by line, so a worktree path with a newline is dropped; the MCP `repo`
argument may name any directory the user can read; the hub speaks plain HTTP
and relies on the private network; the Qt window printed summaries raw and
was dropped the same day (e202).

## Later / maybe

- Test runner subscriber (run on `done`, write `tests` event).
- Lease by module (a path prefix list) rather than single paths.

## 0.13 · get it

Deliver: pre-built packages, the way PowerShell is handed out: a one-line
installer (`get.sh`), a tarball that installs from wherever it is unpacked, a
`.deb`, the Arch package, and the window zipped for Windows and macOS, all
published by a GitHub Actions release on every tag; `make test` on every push.
The documentation split by audience: a short README, `INSTALL.md` for someone
trying it, `GUIDE.md` for someone using it, `DEVELOPING.md` for someone
changing it, `STORY.md` for someone wondering why it exists. Plain words
throughout (`#11`).

Tests: `tests/repo/test_dist.sh` (the tarball lists what it must and nothing
else, installs from where it is unpacked and uninstalls, `get.sh` fetches and
upgrades over itself, `--packaged` and `sankofa setup`, the `.deb`'s members,
control file and layout, and `dpkg-deb --info` where dpkg exists); the release
workflow runs it on x64 and arm64 and inspects the `.deb` with dpkg.

Exit: a release on GitHub that a stranger installs on a clean machine with one
line, and runs the sixty seconds in the README. Then the Mac port.
