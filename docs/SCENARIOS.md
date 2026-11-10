# Scenarios

Each scenario is something that has happened or will happen on this machine.
Each names the failure sankofa prevents and the iteration that covers it.

## S1 · One session, many days

A desktop app (Qt, qmake, PKGBUILD) is built over a week. Sessions get cleared,
compacted, resumed. On day four the session does not know that the package was
rebuilt but not installed because install needs sudo.

- Failure: lost state between sessions. The agent re-does work or asks the
  human questions already answered.
- Needs: a handoff note on every stop, with an "awaiting human" field.
- Covered by: iteration 0.1 (handoff hook), 0.2 (notifier).

## S2 · Main session plus background subagents

The main session delegates two background subagents to different parts of one
repo. One of them also edits a shared header. The main session, reading only
the final reports, merges both and discovers the clobber at build time.

- Failure: silent overwrite inside one client. Reports do not mention paths
  the agent considered incidental.
- Needs: a lease per path, checked before each edit; an overlap flag.
- Covered by: 0.3 (daemon with leases), 0.4 (edit gate), 0.6 (watcher).

## S3 · Agent team on parallel features

A lead and three teammates. The task list on disk says who claimed which task,
but tasks are described in prose, not paths. Two teammates touch the same
module. The team does not survive a `/resume`, and the lead has to reconstruct
what landed.

- Failure: ownership is by task, not by path; and it evaporates on resume.
- Needs: leases keyed by path or glob, persisted outside the client.
- Covered by: 0.3, 0.4.

## S4 · Two clients on one repo

Claude Code in one terminal, Codex in another (both installed here). They have
no channel between them at all. Each has its own instruction file and its own
hook system.

- Failure: no shared memory of any kind across vendors.
- Needs: one ledger reachable as an MCP tool and as a CLI; one `AGENTS.md`
  stanza symlinked to each client's instruction file; a git pre-commit gate as
  the universal enforcement when a client has no blocking hook.
- Covered by: 0.4 (git gate), 0.5 (MCP server).

## S5 · A cloud run lands overnight

A routine or a cloud session pushes a branch at 03:00. The desktop session in
the morning starts from stale assumptions. The cloud session cannot message
back and cannot reach a local socket.

- Failure: one-way routes leave no trace on the device that needs it.
- Needs: a bridge that polls the remote and records arrivals as events; a
  notifier that surfaces them.
- Covered by: 0.6 (bridge), later 0.7 (hub removes the need for the bridge).

## S6 · Desktop and laptop

Same repo, two machines, sometimes the same day. Leases on the desktop mean
nothing to the laptop.

- Failure: two local daemons with two opinions about who holds a path.
- Needs: one authoritative hub on an always-on box, reached over a private
  network; device tokens; allow-and-flag when offline.
- Covered by: 0.7 (hub).

## S7 · Crash or `/clear` mid-task

The session dies with a lease held and a half-written file. The next session
must know the lease is stale and that the file is suspect.

- Failure: a wedge, or a half-edit that looks finished.
- Needs: lease expiry; a "dirty" marker set on claim and cleared on done.
- Covered by: 0.3.

## S8 · Human-in-the-loop step

Some steps need the human: `sudo pacman -U`, a login, a merge decision. Agents
stop at that point. Without a record, the next session repeats the run-up.

- Failure: the human's queue is invisible.
- Needs: an `awaiting` event type and a notifier that shows the human queue.
- Covered by: 0.1, 0.2.

## Out of scope (for now)

- Two humans on one repo. Tokens identify devices, not people.
- Real-time co-editing. sankofa is about ownership and handoff, not CRDTs.
