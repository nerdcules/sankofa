# Get sankofa

Pick your system. Every package holds the same programs built from the same
code: the command, the daemon, the gate, the MCP server, the hub, the notifier
and the dashboards. A Linux desktop, a Mac laptop and a Windows machine share
one ledger through a hub, and hand branches to each other through a git
remote, without knowing what the others run. Every file below is on the
[releases page](https://github.com/nerdcules/sankofa/releases/latest).

## Linux

| How | Do this | Where it goes |
|---|---|---|
| One line | `curl -fsSL https://raw.githubusercontent.com/nerdcules/sankofa/main/get.sh \| sh` | `~/.local/share/sankofa/<version>`, commands linked into `~/.local/bin` |
| Debian, Ubuntu, x64 | `sudo apt install ./sankofa_<version>_amd64.deb` then `sankofa setup` | `/usr/bin`, units in `/usr/lib/systemd/user` |
| Debian, Ubuntu, arm64 | `sudo apt install ./sankofa_<version>_arm64.deb` then `sankofa setup` | the same |
| Any Linux, x64 or arm64 | `tar -xzf sankofa-linux-x64.tar.gz -C ~/.local/share` then `~/.local/share/sankofa-<version>-linux-x64/install.sh` | keep that folder: the commands are links into it |
| Arch | `cd packaging/arch && makepkg -si` then `sankofa setup` | `/usr/bin` |
| From source | `./install.sh` in a checkout (needs the .NET 10 SDK and clang) | links into the checkout |

It needs `git` and `sqlite`; the `.deb` and the Arch package depend on them,
the tarball does not check. The window also wants `fontconfig` and X11 or
Wayland. Desktop toasts use `notify-send` (libnotify) when it is there.

## macOS

| How | Do this | Where it goes |
|---|---|---|
| One line | the same `curl -fsSL https://raw.githubusercontent.com/nerdcules/sankofa/main/get.sh \| sh` | `~/.local/share/sankofa/<version>`, commands linked into `~/.local/bin` |
| Apple silicon | `tar -xzf sankofa-macos-arm64.tar.gz -C ~/.local/share` then `~/.local/share/sankofa-<version>-macos-arm64/install.sh` | keep that folder: the commands are links into it |
| Intel | `sankofa-macos-x64.tar.gz`, the same way | the same |
| From source | `./install.sh` in a checkout (needs the .NET 10 SDK and the Xcode command line tools) | links into the checkout |

It needs `git` (the command line tools bring it) and the system's SQLite,
which is already there. The services are launchd agents in
`~/Library/LaunchAgents`, started at login. Toasts are macOS notifications.

The programs are not signed or notarised. The first time, macOS says they
are from an unidentified developer: allow them in System Settings, Privacy &
Security, or clear the quarantine mark on the unpacked folder:

```sh
xattr -dr com.apple.quarantine ~/.local/share/sankofa/current
```

## Windows

| How | Do this | Where it goes |
|---|---|---|
| x64 | unzip `sankofa-windows-x64.zip` to a folder that can stay (for example `C:\Users\you\sankofa`), add that folder to your PATH, open a new terminal, run `sankofa setup` | the folder you chose |
| Arm | `sankofa-windows-arm64.zip`, the same way | the same |

The zip holds `sankofa.exe`, `sankofad.exe`, `sankofa-mcp.exe`,
`sankofa-hub.exe`, `sankofa-tui.exe` and `sankofa-gui.exe` with their
libraries (SQLite included), the hook scripts, and a `SETUP.txt` saying the
same as this page. It needs `git` on PATH ([Git for Windows](https://git-scm.com/download/win)).
`sankofa setup` adds the hooks to `%USERPROFILE%\.claude\settings.json`,
registers the MCP server with Claude Code and Codex when they are installed,
and creates two scheduled tasks that start at logon: `sankofad serve` and
`sankofa watch serve`. Toasts are Windows notifications.

Windows SmartScreen may say the programs are unrecognised the first time:
"More info", then "Run anyway". They are not signed.

## What the install does, on every system

- Puts `sankofa`, `sankofa-watch`, `sankofad`, `sankofa-mcp`, `sankofa-hub`,
  `sankofa-tui` and `sankofa-gui` on your PATH, and on Linux a launcher entry
  for the window.
- Adds the Stop, SubagentStop, SessionEnd and edit-gate hooks to Claude Code's
  `settings.json` (a backup is kept beside it).
- Starts two services at login: `sankofad` (the daemon) and the notifier
  (desktop toasts for items that need you): systemd user units on Linux,
  launchd agents on macOS, scheduled tasks on Windows.
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

## The hub box

On the always-on machine every device shares a ledger through, on a private
network (Tailscale, WireGuard). Any of the three systems can be the hub:

```sh
SANKOFA_HUB_LISTEN=http://100.64.0.7:8787/ ./install.sh --hub    # or: sankofa setup --hub
sankofad hub token add laptop                                      # one token per device
```

Each device then runs `sankofa hub join <url> <token> <name>` and, in each
repo, `sankofa hub on`. The rest is in [GUIDE.md](GUIDE.md).

## What is where after a package install

```
/usr/bin/sankofa, sankofa-watch, sankofad, sankofa-mcp, sankofa-hub, sankofa-tui, sankofa-gui   (Linux packages)
/usr/lib/sankofa/              the window and its two libraries
/usr/lib/systemd/user/         sankofad.service, sankofa-watch.service, sankofa-hub.service
/usr/share/sankofa/            install.sh (what `sankofa setup` runs) and the hook scripts
/usr/share/applications/       the launcher entry
~/.local/share/sankofa/<v>/    the unpacked tarball (Linux and macOS, get.sh); "current" points at it
~/Library/LaunchAgents/        the launchd agents (macOS)
~/.claude/settings.json        the hooks, added by `sankofa setup`
~/.local/share/sankofa/        the daemon's database (XDG_DATA_HOME; %LOCALAPPDATA%\sankofa on Windows)
<repo>/.sankofa/               the ledger
```
