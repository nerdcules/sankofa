# sankofa

*Sankofa* (Akan): "go back and fetch it." The bird that looks backward to pick
up what was left behind.

A small ledger that lets many AI coding sessions work on one codebase without
stepping on each other. Each session goes back to the ledger for what the last
one left: who holds what, what was done, what is open, what to do next.

sankofa is a substrate, not an agent. It records and answers. It never decides.

## Why

AI compresses a dev team into one technician driving several sessions:
subagents, forks, teams, a second client, a cloud run, a laptop. The channels
between those sessions are plain text, ephemeral, and blind to each other
(see `docs/agent-channels.html`). Nothing durable sits between them. sankofa is
that durable thing.

## Shape

```
agents (any client, any device)
   │  claim · release · note · done · ask        (CLI, MCP tool)
   ▼
sankofad ── SQLite, append-only events, leases with expiry
   │
   ├── hook gates   (PreToolUse lease check, Stop/SubagentStop handoff check)
   ├── git gates    (pre-commit refuses paths leased to someone else)
   └── event bus    (notifier, dashboard, test runner subscribe)
```

## Status

Iteration 0.9: shell ledger, Stop hooks, desktop toasts for items that need
you, `sankofad` (an SQLite event log and path leases), the gates (every edit
is leased before it happens; git refuses commits on someone else's paths), an
MCP server so any MCP client can use the ledger without a shell, and detection
where prevention is not possible: two worktrees changing one path, and work
that landed on a remote while you were away. A hub shares one ledger and one
lease table between your machines, and with cloud sessions over MCP. A
dashboard (`sankofa dash`, `dashboard/`) shows all of it in one window, and a
handoff passes a branch with its context to a teammate. See `docs/ROADMAP.md` for what comes next. Each iteration ships with
tests, and its usage gate is run as an accelerated simulation (`sim/`) against
the real code before the next starts.

## Quick start

```sh
./install.sh            # builds sankofad if needed, links ~/.local/bin/sankofa{,d,-watch},
                        # adds the Stop, SubagentStop, SessionEnd and edit-gate hooks,
                        # enables the sankofad and sankofa-watch systemd user services
cd ~/Work/some-repo
sankofa init            # creates .sankofa/ (add it to .gitignore or commit it, your call)
sankofa watch add       # toast this repo's awaiting items on the desktop
sankofa gate install    # git pre-commit gate for this repo
sankofa todo "write the PKGBUILD"
sankofa awaiting "sudo pacman -U the built package"
sankofa open
sankofa note "built and tested; package not yet installed"
sankofa claim 'src/net/**'   # lease paths; exit 2 names the holder if they are taken
sankofa ask src/net/socket.c # held / free, and any half-finished edit under it
sankofa done e7              # finish a lease (or resolve a todo); clears its dirty mark
make test               # from this repo: builds sankofad, runs every test
```

On Arch, `packaging/arch/PKGBUILD` builds a package with everything in
`/usr/bin` and both user units:

```sh
cd packaging/arch && makepkg -si
systemctl --user enable --now sankofad sankofa-watch
```

The Stop hook refuses to end a turn when the ledger has open items and this
session changed something it has not written up: an Edit/Write call in the
transcript, or a different git working tree, since the session's own last
ledger write. A reply that only answers a question is never blocked. Turn it
off per repo with `require_note=0` in `.sankofa/config`, or once with
`SANKOFA_REQUIRE_NOTE=0`.

`sankofa-watch` toasts new `awaiting` entries (urgent) in every watched repo.
Set `SANKOFA_TOAST_TYPES="awaiting note"` in the service environment to see
every handoff note too; a simulated week put that at about 40 toasts a day.
More than five new entries in one scan, such as after a reboot, become one
summary toast.

## sankofad

The daemon listens on `$XDG_RUNTIME_DIR/sankofa/sankofad.sock` and keeps every
repo's events in `$XDG_DATA_HOME/sankofa/sankofa.db`. Requests are one JSON
object per line. When it answers, the CLI writes through it; when it does not,
the CLI appends to `.sankofa/ledger.jsonl` as in 0.1, and the daemon imports
those lines on its next touch of that repo. The daemon mirrors every event to
that file, so the Stop hook and the watcher keep reading one place. Each write
carries a nonce, so a write whose answer was lost in a crash is never repeated.

Leases are claim events with an expiry (default 30 minutes) and a dirty mark.
`done` ends a lease and clears the mark. `release` ends it and keeps the mark.
An expired lease keeps it too, so the next claimant hears that the edit may be
half-finished, until git shows otherwise: about once a minute sankofad finishes
every ended lease still marked whose paths have no uncommitted change in any
worktree of the repo (`sankofa leases settle` does it now). Files git ignores
count as clean; outside git the mark stays. Two holders conflict when their globs can match a common path;
a session and its subagent are different holders. `sankofa events --follow`
streams every event as it happens, which is how later parts will subscribe.

## Gates

Gates are the only enforcement, and they live at the client edge; the daemon
only records and answers.

- **Edit gate** (`sankofad gate claude-code`, a Claude Code `PreToolUse` hook
  on Edit, Write, MultiEdit and NotebookEdit). Each edit claims the file for
  the session, or for the subagent by its `agent_id`. When another holder has
  it, the edit is denied with the holder's name. A subagent may take over a file
  its own parent session holds once the parent has not edited it for
  `handover_idle` (default 5s; handing over mid-edit clobbered the parent's
  edit in `sim/gates.py`); the parent then waits for the subagent. The
  subagent's leases end at SubagentStop, the session's at SessionEnd, and
  anything abandoned expires (`lease_ttl` in `.sankofa/config`, default 30m).
  If the daemon does not answer within 50 ms the edit goes ahead and one
  `flag` is written to the ledger, which the daemon imports when it is back.
  About 4 ms per edit.
- **Codex** (`sankofad gate codex`): the same gate for `apply_patch`; see
  `hooks/codex/README.md`.
- **Commit gate** (`sankofa gate install`): a git pre-commit hook that refuses
  staged paths someone else holds, and refuses when the daemon is down. It is
  the floor for every client, hooks or not. Override once with
  `SANKOFA_GATE=0` or `git commit --no-verify`.

Turn the gates off for a repo with `gate=0` in `.sankofa/config`.

## MCP

`sankofad mcp` (the same as `sankofa-mcp`, a separate binary so the per-edit
gate stays small and fast) is a stdio MCP server with the CLI's verbs as tools: `open`,
`last`, `note`, `todo`, `awaiting`, `done`, `claim`, `release`, `ask`,
`leases`. Each takes an optional `repo` (default: the client's working
directory). Each tool runs the CLI, so MCP writes go through the daemon or the
same local fallback. A failure is a tool error (`isError`), never a dead
server. A refused claim is an answer: it names the holder. `install.sh`
registers it with both clients; by hand:

```sh
claude mcp add --scope user sankofa -- sankofad mcp
```

```toml
# ~/.codex/config.toml
[mcp_servers.sankofa]
command = "sankofad"
args = ["mcp"]
# optional: let `codex exec` call these tools without asking (it refuses otherwise)
# default_tools_approval_mode = "approve"
```

Claude Code passes its session id to MCP servers, so a claim made through MCP
and an edit checked by the gate have the same holder. Codex passes no session
id, so every Codex session is one holder, `- [mcp:codex-mcp-client]`: Codex
sessions are kept apart from Claude Code sessions, not from each other.

## Overlaps and landings

Leases prevent two agents editing one file in one tree. Agents in separate
worktrees never ask each other, and two remote agents never touch your disk,
so for those sankofa detects and tells you.

```sh
sankofa trees on        # watch this repo and every git worktree of it (found again as they come and go)
sankofa trees add ../other-clone
sankofa trees           # the trees, their branches, how many paths each changed
sankofa bridge on 5m    # fetch the remotes every 5 minutes
sankofa watch add       # toast awaiting, handoff, overlap and landed events on the desktop
```

- `overlap`: a path changed in two or more trees. A tree's changes count from
  where it left the home tree's history (the merge base), so a worktree's
  commits count until they are merged. The daemon watches each tree with
  inotify (skipping `.git/`, ignored directories and nested repos) and asks git
  after each burst of writes settles (300 ms), and rescans every 30 s for
  commits and anything inotify missed. Each overlap is recorded once, again
  only when another tree joins or after it has cleared. The event's `glob` is
  the path.
- `landed`: a remote branch moved, with `branch` (`origin/x`) and `sha`, the
  commit count, and the newest subject and author. What the remote branches
  pointed at is kept in the daemon's database, so a push made while the laptop
  slept is reported on the first fetch after waking, even if your own
  `git fetch` saw it first. A tip that is already in one of your local
  branches (your own push, or work you pulled) is not reported. The first
  fetch only takes a baseline. A failed fetch is retried after 30 s;
  `sankofa bridge` shows the last error.

Both are daemon-only: with `sankofad` down, nothing is watched or fetched, and
the bridge catches up when it returns.

## Handing work to a teammate

When you run out of credits (or time) mid-task, hand the branch over:

```sh
sankofa handoff "parser done; next: error messages"   # on your machine
sankofa pickup                                         # on theirs
```

`handoff` refuses while tracked files have uncommitted changes (they would
stay behind), pushes the branch, and writes a letter: your text, or the last
agent reply when you give none, plus this repo's open items. The letter is a
tag object pushed to `refs/sankofa/handoff/<branch>` on the same remote, so
the teammate needs nothing but the git remote; it is also a `handoff` event
in the ledger, an open item that a hub shares at once. No credentials or
accounts change hands: the teammate's agent carries on under their own.

`pickup` fetches, takes the one handoff on offer (or the branch or id you
name), switches to the branch (creating it to track the remote, or
fast-forwarding yours), prints the letter, and finishes the handoff. Without a
hub the letter's open items become todos in the teammate's ledger. It then
removes the ref from the remote, so nobody picks it up twice. The bridge
(`sankofa bridge on`) also fetches handoff refs: a new one is recorded and
toasted ("handed off to you", with the `pickup` line), never on the machine
that sent it, and when the ref is gone from the remote the sender's open
handoff is finished as picked up.

## Hub: one ledger for every device

On an always-on box reachable over a private network (Tailscale, WireGuard;
the hub speaks plain HTTP and relies on that network for encryption):

```sh
SANKOFA_HUB_LISTEN=http://100.64.0.7:8787/ ./install.sh --hub   # sankofa-hub.service
sankofad hub token add desk          # prints a token once; only its hash is kept
sankofad hub token add laptop
sankofad hub token add cloud         # for sessions that reach the hub over MCP
```

On each device, then in each repo:

```sh
sankofa hub join http://100.64.0.7:8787 <token> desk
sankofa hub on                       # the repo's hub name defaults to its git remote
sankofa hub                          # online / offline, writes queued
```

The first device to attach a repo seeds the hub with its ledger; later
devices start from the hub's (their own is kept in
`.sankofa/ledger.pre-hub.jsonl`). From then on `sankofad` forwards that
repo's verbs to the hub, and `.sankofa/ledger.jsonl` is a replica kept
current by a live event stream, so the hooks, the watcher and `sankofa last`
work as before. Every event records its `device` (from the token) and
`client` (`cli`, `gate:claude-code`, `mcp:<name>`), and a denied claim names
the device that holds the path.

Offline, writes are allowed and queued in the replica with ids of their own
(`q...`); the edit gate allows and queues too. When the hub answers again the
queue is sent in order: refs follow the new ids, a queued claim on a path
another device held meanwhile is recorded as contention plus a flag, and the
replica's tail is replaced by the hub's copy. Reads that need the hub
(`leases`, the commit gate) fail while it is down; commits stay gated. A
revoked or unknown token is refused (401) and logged by the hub; nothing is
queued for it.

A client that cannot run `sankofad`, like a cloud session, speaks MCP to the
hub directly: `POST http://<hub>/mcp` with `Authorization: Bearer <token>`.
Its tools are the same as `sankofad mcp`'s, each with a `repo` argument (the
hub name). A cloud routine that records its work there needs no bridge.

## Dashboard

`sankofa dash` opens `sankofa-dash`, an Omarchy-style Qt app: what waits on
you, open items, who holds what (and edits left half-finished), the last
handoff per session, and recent overlaps, landings and flags, for every repo
`sankofa watch add` lists. `sankofa dash summary` prints the same as text.
It only reads the ledgers. See `dashboard/README.md`.

## Simulations

```sh
python3 sim/week.py     # Stop rule: blocks, nags, missed handoffs per simulated week
python3 sim/notify.py   # notifier: exactly-once delivery, latency, toasts per day
python3 sim/daemon.py   # sankofad under SIGKILL every few seconds: no lost or doubled writes
python3 sim/gates.py    # agents editing one repo at once: clobbers with and without the gates
python3 sim/mcp.py      # Claude Code and Codex MCP clients on one ledger, daemon killed mid-run
python3 sim/overlap.py  # agents in several worktrees: overlaps missed, false, or reported twice
python3 sim/overnight.py # routines pushing overnight while the laptop sleeps: one landed and one toast each
python3 sim/hub.py      # desk, laptop and a cloud session on one hub, with cuts, dead routes and restarts
python3 sim/hub.py --docker --skew 90  # the same with hub, desk and laptop as separate containers:
                        # set up by install.sh, unplugged, firewalled, paused (lid shut), rebooted,
                        # the hub box crashed, the laptop's clock 90 s ahead (needs docker)
```

Each drives the real CLI, hook, watcher, and daemon with synthetic sessions.

## Docs

- `docs/OBJECTIVE.md` – what sankofa must do, measured, and what it must not do
- `docs/SCENARIOS.md` – the concrete situations it exists for
- `docs/GAP-ANALYSIS.md` – what Claude Code and other clients already give, what is missing, what sankofa adds
- `docs/ROADMAP.md` – iterations, each with its tests and exit criteria
- `docs/agent-channels.html` – the channel map and framework diagrams

## Language

Hooks and the first iterations are POSIX shell + jq (nothing to compile, runs
on every edit). The daemon is C# with Native AOT: one 4 MB binary that starts
in about 4 ms and calls the system libsqlite3 directly, with no other runtime
dependency. The hub transport is HTTP so moving to a server is config.
