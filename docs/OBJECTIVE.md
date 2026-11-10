# Objective

## One sentence

Let one person run many AI coding sessions, across clients and devices, on one
codebase, and never lose track of who changed what, what is open, or what to do
next.

## Measurable goals

| # | Goal | Measure |
|---|------|---------|
| G1 | No silent overwrite | Two agents editing the same path concurrently is detected before commit, 100% of the time once gates are on |
| G2 | Cold-start handoff | After `/clear`, a crash, or switching device, the next session can state what is open from the ledger alone, without reading transcripts |
| G3 | Client-agnostic | Any client that speaks MCP or can run a shell command can write to the ledger with no client-specific code in the core |
| G4 | Cheap on the hot path | The per-edit lease check completes in under 5 ms locally and under 100 ms over a private network, with a safe fallback when the daemon is unreachable |
| G5 | Zero decisions | The daemon never chooses an action for an agent. Every output is a record or an answer to a question |
| G6 | Incremental | Each iteration is usable on its own and is used for real before the next begins |

## Non-goals

- Not an orchestrator. sankofa does not assign work, spawn agents, or route tasks.
- Not a replacement for git. Code truth stays in the repo and its remote.
- Not a chat bus between agents. Messages between agents stay on the client's
  own channel. sankofa holds state, not conversation.
- Not a dashboard first. Visibility tools come last and subscribe to events.
- Not multi-user. One technician, many sessions. Permissions are per device
  token, not per person.

## Design rules

1. Append-only. Nothing in the ledger is edited or deleted; corrections are new
   events.
2. Leases expire. A dead agent cannot wedge the repo.
3. Gates are the only enforcement, and gates live at the client edge (hooks,
   git). The daemon itself blocks nothing.
4. Unreachable daemon means allow-and-flag, never hard fail, except at commit.
5. The event log is the extension interface. New parts subscribe; they do not
   call each other.
6. Same binary, different listener. Local socket today, HTTP hub tomorrow.
