# Working in this repo with an AI agent

This repo keeps a handoff ledger in `.sankofa/`. Use the `sankofa` command;
never edit the ledger by hand.

- Start by reading what was left: `sankofa open` and `sankofa last`.
- When you change or decide something worth knowing later: `sankofa note "<text>"`.
- When you leave something unfinished: `sankofa todo "<text>"` (prints an id).
- When a step needs the human (sudo, a login, a decision): `sankofa awaiting "<text>"`.
- When an open item is finished: `sankofa done <id> "<text>"`.
- Every file you edit is leased to you automatically by the edit gate. If an
  edit is denied because another session or subagent holds the file, do not
  work around it (no Bash rewrites of that file): work on something else, wait,
  or tell the human. `sankofa ask <path>` says who holds a path.
- To reserve a whole area before starting: `sankofa claim "<glob>"` (prints a
  lease id; exit 2 and the holder if someone else has it). When finished:
  `sankofa done <lease-id>`. Leases also end when your subagent or session
  ends; an abandoned lease expires, and the next holder is told the edit may be
  half-finished.
- A commit that touches a path someone else holds is refused by the git gate.
- Without a shell, the same verbs are MCP tools on the `sankofa` server
  (`sankofad mcp`): `open`, `last`, `note`, `todo`, `awaiting`, `done`,
  `claim`, `release`, `ask`, `leases`.

A Stop hook will refuse to let the session end while open items exist and
this session has written no note. Writing the note is the fix.
