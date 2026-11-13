# Developing sankofa

For someone who wants to build it, run its tests and simulations, cut a
release, or change it. Using it: [GUIDE.md](GUIDE.md). Why it exists and what
it must never become: [OBJECTIVE.md](OBJECTIVE.md). What each iteration
delivered, with its tests and the simulation that gated it: [ROADMAP.md](ROADMAP.md).

## Layout

```
bin/sankofa            the CLI (POSIX sh + jq); bin/sankofa-watch, the desktop notifier
hooks/                 the client adapters: claude-code/, codex/, and the git pre-commit gate
daemon/Sankofa.Core    the store (SQLite), leases, the hub link, the watcher, the ledger fold (Board)
daemon/sankofad        the daemon, the per-edit gate, the hub (also sankofa-hub)
daemon/sankofa-mcp     the MCP server (a binary of its own, so the gate stays small)
daemon/sankofa-tui     the dashboard in the terminal (Terminal.Gui)
daemon/sankofa-gui     the dashboard in a window (Avalonia), Windows, macOS, Linux
systemd/               the user units; install.sh and the packages install them
packaging/             arch/ (PKGBUILD), deb/ (build.sh), linux/ (the launcher entry)
tests/<iteration>/     shell tests per iteration, xUnit tests under tests/0.3, fixtures/
sim/                   the simulations: a week of use in minutes, against the real code
docs/                  everything below
```

## Build and test

```sh
make build              # bin/out/{sankofad,sankofa-mcp,sankofa-hub,sankofa-tui,sankofa-gui}: Native AOT, this machine's Linux
make test               # builds, then every shell test twice (local ledger, then through sankofad) and the xUnit tests
make dist               # dist/sankofa-linux-<arch>.tar.gz and dist/sankofa_<version>_<arch>.deb (tests/repo/test_dist.sh checks them)
make gui-dist           # the window for Windows, macOS and Linux arm64, one zip each
make bench              # sankofad startup and ask latency
./install.sh            # from this checkout: links ~/.local/bin, hooks, services, MCP registration
```

Needs the .NET 10 SDK, clang (Native AOT), jq, git, sqlite, inotify-tools,
libnotify; the tests also use tmux and python3. `.github/workflows/ci.yml`
runs `make test` on every push and pull request.

## Rules

- One iteration at a time; each has tests that run with `make test`, and an
  outcome written into `ROADMAP.md`. Each iteration's "used for a week" gate is
  run as an accelerated simulation under `sim/` that drives the real code.
- Every new daemon verb is a record or a query. The daemon never decides
  (G5 in `OBJECTIVE.md`). Gates live at the client edge.
- Nothing binary in git except the pictures under `images`
  (`tests/repo/test_no_binaries.sh`). Docs are Markdown and render on GitHub.
- Text that comes from elsewhere (ledger lines, hook payloads, git output,
  letters from a remote, hub answers) is data: see the security review in
  `ROADMAP.md` and `tests/0.12/test_hostile_text.sh`.

## Cutting a release

1. Set the version in `bin/sankofa` (`VERSION=`) and `packaging/arch/PKGBUILD`
   (`pkgver=`), write the iteration's outcome in `ROADMAP.md`, commit.
2. Tag and push: `git tag v1.2.3 && git push origin main v1.2.3`.
3. `.github/workflows/release.yml` builds on Linux x64 and arm64 (tarball and
   .deb each, checked with dpkg and `test_dist.sh`), builds the window for
   the other platforms, and publishes a GitHub Release with the files and an
   install table. The one-line installer (`get.sh`) and the links in
   `INSTALL.md` point at the latest release.

Run the workflow by hand (Actions, "release", Run workflow) to build the
packages without publishing: they stay as workflow artifacts.

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

## Simulations

```sh
python3 sim/week.py     # Stop rule: blocks, nags, missed handoffs per simulated week
python3 sim/notify.py   # notifier: exactly-once delivery, latency, toasts per day
python3 sim/daemon.py   # sankofad under SIGKILL every few seconds: no lost or doubled writes
python3 sim/gates.py    # agents editing one repo at once: clobbers with and without the gates
python3 sim/mcp.py      # Claude Code and Codex MCP clients on one ledger, daemon killed mid-run
python3 sim/overlap.py  # agents in several worktrees: overlaps missed, false, or reported twice
python3 sim/overnight.py # routines pushing overnight while the laptop sleeps: one landed and one toast each
python3 sim/hub.py      # desk, laptop and a cloud session on one hub, with cuts, dead routes and restarts
python3 sim/handoff.py  # four people handing branches to each other through one git remote:
                        # nothing lost, nothing picked up twice (also when two race for one);
                        # --hub: the same with the ledger shared through a hub
python3 sim/crash.py    # handoff and pickup killed before and after every step, then run again;
                        # with and without a hub, and a remote that refuses refs/sankofa/*
python3 sim/compat.py   # an upgrade over an old database, and teams that have not all upgraded
python3 sim/hub.py --docker --skew 90  # the same with hub, desk and laptop as separate containers:
                        # set up by install.sh, unplugged, firewalled, paused (lid shut), rebooted,
                        # the hub box crashed, the laptop's clock 90 s ahead (needs docker)
```

Each drives the real CLI, hook, watcher, and daemon with synthetic sessions.

## Language

Hooks and the first iterations are POSIX shell + jq (nothing to compile, runs
on every edit). The daemon is C# with Native AOT: one 5 MB binary that starts
in about 6 ms and calls the system libsqlite3 directly, with no other runtime
dependency (`sankofad bench`, 0.11: startup 6 ms median, `ask` 0.09 ms on an
open socket, the edit gate 4 ms as a process). The hub transport is HTTP so
moving to a server is config.
## The documents

- `GUIDE.md`: using it, the reference
- `INSTALL.md`: getting it on each platform
- `STORY.md`: how this came about
- `OBJECTIVE.md`: what sankofa must do, measured, and what it must not do
- `SCENARIOS.md`: the concrete situations it exists for
- `GAP-ANALYSIS.md`: what Claude Code and other clients already give, what is missing, what sankofa adds
- `ROADMAP.md`: iterations, each with its tests, outcome and exit criterion
- `agent-channels.md`: how sessions and agents talk to each other (figures in `images/`)
- `PRIOR-ART.md`: what else sits between sessions, and where sankofa differs
- `../AGENTS.md`: the stanza an agent reads when it works in a repo with a ledger
