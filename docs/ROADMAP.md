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

## 0.7 · hub

Deliver: HTTP listener (MCP streamable HTTP + a small JSON API), device
tokens, systemd unit for a server; client config for a remote hub; offline
allow-and-flag.

Tests: two clients against one hub, lease contention honored; hub down, local
allow-and-flag, reconciliation on return.

## 0.8 · dashboard (optional)

Deliver: Omarchy-style Qt app or a TUI subscribing to `events --follow`.

## Later / maybe

- Test runner subscriber (run on `done`, write `tests` event).
- Lease by module (a path prefix list) rather than single paths.
