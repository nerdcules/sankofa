# Get sankofa

Pick your system. The daemon, hooks and gates run on Linux today; the window
runs on Windows, macOS and Linux and reads a ledger (a hub replica, or a
ledger file kept there). Every file below is on the
[releases page](https://github.com/nerdcules/sankofa/releases/latest).

## Linux

| How | Do this | Where it goes |
|---|---|---|
| One line | `curl -fsSL https://raw.githubusercontent.com/nerdcules/sankofa/main/get.sh \| sh` | `~/.local/share/sankofa/<version>`, commands linked into `~/.local/bin` |
| Debian, Ubuntu, x64 | `sudo apt install ./sankofa_<version>_amd64.deb` then `sankofa setup` | `/usr/bin`, units in `/usr/lib/systemd/user` |
| Debian, Ubuntu, arm64 | `sudo apt install ./sankofa_<version>_arm64.deb` then `sankofa setup` | the same |
| Any Linux, x64 or arm64 | `tar -xzf sankofa-linux-x64.tar.gz -C ~/.local/share` then `~/.local/share/sankofa-<version>-linux-x64/install.sh` | keep that folder: the commands are links into it |
| Arch | `cd packaging/arch && makepkg -si` then `sankofa setup` | `/usr/bin` |
| From source | `./install.sh` in a checkout (needs the .NET 10 SDK, clang, jq) | links into the checkout |

It needs `jq`, `git`, `sqlite`, `inotify-tools` and `libnotify` (the `.deb`
and the Arch package depend on them; the tarball does not check). The window
also wants `fontconfig` and X11 or Wayland.

### What the install does

- Puts `sankofa`, `sankofa-watch`, `sankofad`, `sankofa-mcp`, `sankofa-hub`,
  `sankofa-tui` and `sankofa-gui` on your PATH, and a launcher entry for the
  window.
- Adds the Stop, SubagentStop, SessionEnd and edit-gate hooks to
  `~/.claude/settings.json` (a backup is kept beside it).
- Enables two user services: `sankofad` (the daemon) and `sankofa-watch`
  (desktop toasts for items that need you).
- Registers the MCP server with Claude Code and Codex when they are installed.

Then, in each repo that should keep a ledger:

```sh
sankofa init            # .sankofa/ in the repo; commit it or add it to .gitignore
sankofa watch add       # toast this repo's items on the desktop
sankofa gate install    # git pre-commit gate for this repo
```

Run the same install again to upgrade. `install.sh --uninstall` (or
`sankofa setup --uninstall`, or `apt remove sankofa`) removes the commands,
the hooks and the services, and leaves the ledgers alone.

### The hub box

On the always-on machine every device shares a ledger through, on a private
network (Tailscale, WireGuard):

```sh
SANKOFA_HUB_LISTEN=http://100.64.0.7:8787/ ./install.sh --hub    # or: sankofa setup --hub
sankofad hub token add laptop                                      # one token per device
```

Each device then runs `sankofa hub join <url> <token> <name>` and, in each
repo, `sankofa hub on`. The rest is in [GUIDE.md](GUIDE.md).

## Windows and macOS: the window

| System | File |
|---|---|
| Windows, x64 | `sankofa-gui-win-x64.zip` |
| Windows on Arm | `sankofa-gui-win-arm64.zip` |
| macOS, Apple silicon | `sankofa-gui-osx-arm64.zip` |
| macOS, Intel | `sankofa-gui-osx-x64.zip` |
| Linux, arm64 | `sankofa-gui-linux-arm64.zip` |

Unzip and run `sankofa-gui` with the folder or ledger files it should show:
`sankofa-gui ~/work/api ~/work/web`, or a hub replica. It reads and never
writes. The builds are not signed: macOS will say the app is from an
unidentified developer (right-click, Open, or
`xattr -d com.apple.quarantine sankofa-gui`), and Windows SmartScreen will
ask for "More info" and "Run anyway".

## What is where after a package install

```
/usr/bin/sankofa, sankofa-watch, sankofad, sankofa-mcp, sankofa-hub, sankofa-tui, sankofa-gui
/usr/lib/sankofa/              the window and its two libraries
/usr/lib/systemd/user/         sankofad.service, sankofa-watch.service, sankofa-hub.service
/usr/share/sankofa/            install.sh (what `sankofa setup` runs) and the hook scripts
/usr/share/applications/       the launcher entry
~/.claude/settings.json        the hooks, added by `sankofa setup`
~/.local/share/sankofa/        the daemon's database (XDG_DATA_HOME)
<repo>/.sankofa/               the ledger
```
