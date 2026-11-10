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

Iteration 0.3: shell ledger, Stop hooks, desktop toasts for items that need
you, and `sankofad`, a daemon with an SQLite event log and path leases. See `docs/ROADMAP.md` for what comes next. Each iteration ships with
tests, and its usage gate is run as an accelerated simulation (`sim/`) against
the real code before the next starts.

## Quick start

```sh
./install.sh            # builds sankofad if needed, links ~/.local/bin/sankofa{,d,-watch},
                        # adds Stop + SubagentStop hooks, enables the sankofad and
                        # sankofa-watch systemd user services
cd ~/Work/some-repo
sankofa init            # creates .sankofa/ (add it to .gitignore or commit it, your call)
sankofa watch add       # toast this repo's awaiting items on the desktop
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
half-finished. Two holders conflict when their globs can match a common path;
a session and its subagent are different holders. `sankofa events --follow`
streams every event as it happens, which is how later parts will subscribe.

## Simulations

```sh
python3 sim/week.py     # Stop rule: blocks, nags, missed handoffs per simulated week
python3 sim/notify.py   # notifier: exactly-once delivery, latency, toasts per day
python3 sim/daemon.py   # sankofad under SIGKILL every few seconds: no lost or doubled writes
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
