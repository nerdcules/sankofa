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

Iteration 0: design only. See `docs/ROADMAP.md` for the build order.
Each iteration ships with tests and is used for real before the next starts.

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
