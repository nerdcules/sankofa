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

Iteration 0.2: shell ledger, Stop hooks, and desktop toasts for items that
need you. See `docs/ROADMAP.md` for what comes next. Each iteration ships with
tests, and its usage gate is run as an accelerated simulation (`sim/`) against
the real code before the next starts.

## Quick start

```sh
./install.sh            # links ~/.local/bin/sankofa{,-watch}, adds Stop + SubagentStop hooks,
                        # enables the sankofa-watch systemd user service
cd ~/Work/some-repo
sankofa init            # creates .sankofa/ (add it to .gitignore or commit it, your call)
sankofa watch add       # toast this repo's awaiting items on the desktop
sankofa todo "write the PKGBUILD"
sankofa awaiting "sudo pacman -U the built package"
sankofa open
sankofa note "built and tested; package not yet installed"
make test               # from this repo: runs tests/*/*.sh
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

## Simulations

```sh
python3 sim/week.py     # Stop rule: blocks, nags, missed handoffs per simulated week
python3 sim/notify.py   # notifier: exactly-once delivery, latency, toasts per day
```

Both drive the real CLI, hook, and watcher with synthetic sessions.

## Docs

- `docs/OBJECTIVE.md` – what sankofa must do, measured, and what it must not do
- `docs/SCENARIOS.md` – the concrete situations it exists for
- `docs/GAP-ANALYSIS.md` – what Claude Code and other clients already give, what is missing, what sankofa adds
- `docs/ROADMAP.md` – iterations, each with its tests and exit criteria
- `docs/agent-channels.html` – the channel map and framework diagrams

## Language

Hooks and the first iterations are POSIX shell + jq (nothing to compile, runs
on every edit). The daemon is C# with Native AOT (single binary, fast start,
official MCP SDK). The hub transport is HTTP so moving to a server is config.
