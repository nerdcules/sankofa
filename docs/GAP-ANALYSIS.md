# Gap analysis

What each need looks like today, what the clients already provide, what is
missing, and which sankofa part fills it. Facts about Claude Code are from its
docs as of 2026-11-10; facts about other clients vary by version and are
marked as such.

| Need | Claude Code today | Other clients | Gap | sankofa part | Iter |
|------|-------------------|---------------|-----|------------|------|
| Durable handoff between sessions | Transcripts and `--resume`; subagent transcripts resumable by ID; 30-day retention | Varies; most keep a session log | State is inside transcripts, not readable without re-loading them; nothing cross-client | `sankofa note` + Stop/SubagentStop hooks writing `.sankofa/ledger.md` | 0.1 |
| Human queue ("awaiting sudo") | None | None | Nothing records what the human owes | `awaiting` event type, notifier | 0.1, 0.2 |
| Surface events on the desktop | Notification hook (cannot block) | Varies | Only this client's events; no cross-client view | inotify/event-bus notifier → `notify-send` | 0.2 |
| Ownership of paths | Team task list (by task, in prose); file locks on claims; lost on resume | None | No lease by path; nothing survives resume; single-client | `sankofad` leases with expiry, SQLite | 0.3 |
| Block an edit on a leased path | PreToolUse hook can deny (exit 2) | Hook support varies; some notify-only | Enforcement differs per client; some have none | Bash hook adapter per client; git pre-commit as universal gate | 0.4 |
| Require a report before an agent stops | SubagentStop can block; Stop can block | Varies | Only where the client supports it | Hook adapters; otherwise detection only | 0.4 |
| Shared tool interface for any client | MCP supported | MCP widely supported | None once an MCP server exists | `sankofad --mcp` stdio server | 0.5 |
| Detect overlap across worktrees | Worktree isolation on spawn | Varies | Isolation yes, detection no | inotify watcher in daemon, overlap event | 0.6 |
| Learn that a cloud run landed | Cloud session cannot SendMessage back; routines run unattended | n/a | Nothing on the device notices | Remote bridge: fetch loop, records arrivals | 0.6 |
| Same truth across devices | Cross-session messaging via Anthropic servers (text only, no state) | n/a | No shared state at all | HTTP hub on an always-on box, device tokens | 0.7 |
| See the whole picture | None | None | No single view | Dashboard subscribing to events | 0.8 |

## What already works and sankofa must not duplicate

- Spawning, resuming and messaging agents inside one client. sankofa never
  carries messages.
- Worktree isolation. sankofa watches worktrees; it does not create them.
- Git as the code truth. sankofa records what landed; it does not merge.
- Permissions. Each session keeps its own. A ledger entry never grants
  anything.

## Risks

| Risk | Mitigation |
|------|------------|
| Hook latency on every edit | Local socket, sub-millisecond answer, 50 ms timeout, allow-and-flag on timeout |
| Agents ignore the stanza | Gates enforce; the stanza is advice, the hook is law |
| Daemon becomes an orchestrator by drift | Rule G5 in OBJECTIVE; every new verb must be a record or a query |
| Multi-client hook support changes | Keep adapters tiny and separate; git pre-commit is the floor |
| Hub becomes a single point of failure | Allow-and-flag offline; commit gate still catches real conflicts |
