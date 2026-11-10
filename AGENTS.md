# Working in this repo with an AI agent

This repo keeps a handoff ledger in `.sankofa/`. Use the `sankofa` command;
never edit the ledger by hand.

- Start by reading what was left: `sankofa open` and `sankofa last`.
- When you change or decide something worth knowing later: `sankofa note "<text>"`.
- When you leave something unfinished: `sankofa todo "<text>"` (prints an id).
- When a step needs the human (sudo, a login, a decision): `sankofa awaiting "<text>"`.
- When an open item is finished: `sankofa done <id> "<text>"`.
- Before editing paths another session or subagent might also touch:
  `sankofa claim "<glob>"` (prints a lease id; exit 2 and the holder if someone
  else has it). Check one path with `sankofa ask <path>`. When the edit is
  finished: `sankofa done <lease-id>`. A lease you abandon expires on its own,
  and the next claimant is told the edit may be half-finished.

A Stop hook will refuse to let the session end while open items exist and
this session has written no note. Writing the note is the fix.
