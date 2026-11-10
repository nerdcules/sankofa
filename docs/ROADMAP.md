# Roadmap

Rules: one iteration at a time; every iteration has tests that run with
`make test`; an iteration is used for real work before the next begins; each
ships as a git tag.

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

## 0.2 · notify (shell + systemd user unit)

Deliver: `sankofa-watch` service: inotify on every registered ledger, toast on
new `awaiting` or `note` entries; `sankofa watch add <repo>`.

Tests: fake `notify-send` on PATH; append an entry; assert one toast with the
expected title.

## 0.3 · sankofad (C#, Native AOT)

Deliver: daemon on a Unix socket; SQLite; events table (append-only); leases
table (path glob, holder, expires); verbs `claim`, `release`, `ask`, `note`,
`done`, `events --follow`. `bin/sankofa` becomes a thin client; falls back to
the 0.1 markdown ledger when no daemon is running.

Tests: xUnit unit tests for lease logic (overlap, expiry, renew); integration
test that starts the daemon on a temp socket and drives it with the CLI.

## 0.4 · gates

Deliver: `hooks/pre-edit.sh` (PreToolUse for Edit/Write/MultiEdit): asks
`claim`, denies with a message naming the holder; `hooks/git-pre-commit`:
refuses commits touching paths leased to another holder; allow-and-flag when
the daemon is unreachable.

Tests: two holders, same path, second edit denied; expired lease, second edit
allowed; daemon down, edit allowed and a `flag` event written on reconnect.

## 0.5 · mcp

Deliver: `sankofad --mcp` stdio server exposing the same verbs; config snippets
for Claude Code and Codex.

Tests: drive the server with a minimal MCP client script; same assertions as
0.3 through the tool interface.

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
