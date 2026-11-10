# Working in this repo with an AI agent

This repo keeps a handoff ledger in `.sankofa/`. Use the `sankofa` command;
never edit the ledger by hand.

- Start by reading what was left: `sankofa open` and `sankofa last`.
- When you change or decide something worth knowing later: `sankofa note "<text>"`.
- When you leave something unfinished: `sankofa todo "<text>"` (prints an id).
- When a step needs the human (sudo, a login, a decision): `sankofa awaiting "<text>"`.
- When an open item is finished: `sankofa done <id> "<text>"`.

A Stop hook will refuse to let the session end while open items exist and
this session has written no note. Writing the note is the fix.
