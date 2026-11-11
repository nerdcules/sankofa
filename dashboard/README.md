# sankofa-dash

One window for every sankofa ledger you follow: what waits on you, what is
open, who holds which paths (and which edits were left half-finished), the
last handoff each session wrote, and the overlaps, landings and flags that
happened on their own. It reads; it never writes to a ledger or asks the
daemon to change anything.

To see it without your own data, render it from the bundled fixtures:

```sh
dashboard/bin/build
dashboard/build/sankofa-dash render --out dash.png --now 2026-11-11T10:00:00.000Z \
  dashboard/tests/fixtures/api.jsonl dashboard/tests/fixtures/web.jsonl
```

## Use

```sh
sankofa dash                 # the repos `sankofa watch add` lists (the notifier's registry)
sankofa dash ~/src/api ~/src/web
sankofa-dash summary [--json] [repo...]   # the same, as text, no display needed
```

It follows each repo's `.sankofa/ledger.jsonl`, the daemon's mirror of its
event log, so it works with or without `sankofad`, and for repos shared
through a hub (whose replica it is). A ledger rewritten by a hub reconcile is
read again from the start. Without the daemon nobody writes `expire` events,
so a lease past its time is shown as expired anyway.

| Key | Does |
|---|---|
| Tab / Shift+Tab | next / previous repo, or all of them |
| ← → or H L | move between panels |
| ↑ ↓ or K J | move within a panel |
| Space | copy: `sankofa done <id>` for an item, the paths of a lease |
| R | read the ledgers again |
| ? | show the keys |
| Q | quit |

It follows the Omarchy accent and window rounding live, and the language
picked under Setup > Language (Danish and German so far; anything else shows
English).

## Build, test, install

```sh
dashboard/bin/build     # qmake6 + make -> dashboard/build/sankofa-dash
dashboard/bin/test      # Qt Test, offscreen: folding, file following, the window from fixtures, keys
dashboard/bin/install   # makepkg -fsi: the binary, .desktop entry and icon (Super+Space finds it)
```

`make test` at the repo root runs these tests too when Qt's `qmake6` is
installed.
