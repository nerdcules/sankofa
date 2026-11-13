using Sankofa;
using Sankofa.Cli;

/// <summary>sankofa: the command. One verb per call; the shell CLI's usage, outputs and exit codes.</summary>
static class Program
{
    const string Usage = """
usage: sankofa <command> [args]

  init                 create .sankofa/ in the current directory
  setup                after a package install: wire the Claude Code hooks, the user
                       services and the MCP registrations (runs install.sh)
  note <text>          record what changed or was decided
  todo <text>          record an open item (prints its id)
  awaiting <text>      record an item that needs the human (prints its id)
  done <id> [text]     resolve a todo or awaiting item, or finish a lease
  handoff [text]       out of credits or time: push this branch and leave it for a teammate
                       (text defaults to the last agent reply; open items go with it; the
                       leases this session holds here are finished)
  pickup [branch|id]   take a handoff: fetch, switch to its branch, print the letter
                       (either one cut off halfway is finished by running it again)
  claim <glob> [ttl]   lease paths (ttl like 90, 30m, 2h; default 30m); exit 2 if held
  release <id>         give a lease up without finishing (its dirty mark stays)
  ask <path>           who holds this path: held / free, plus half-finished edits
  leases               live leases and ended ones still marked dirty
  leases settle        finish ended dirty leases whose paths git shows clean (sankofad
                       also does this every minute)
  events [--follow] [--since N] [--all]   raw events from sankofad, one JSON per line;
                       --follow alone streams new events, with --since it replays first
  daemon               is sankofad answering?
  open                 list unresolved items
  last [n]             show the newest n entries (default 5)
  log                  show the whole ledger
  hook stop            Stop hook: reads hook JSON on stdin
  hook subagent-stop   SubagentStop hook: records the report, finishes the subagent's leases
  hook session-end     SessionEnd hook: finishes every lease the session holds
  gate install [dir]   install the git pre-commit gate in a repo (uninstall, status)
  trees on             watch this repo and all its git worktrees for overlaps
  trees add <dir>...   also watch another checkout (a clone, or a worktree elsewhere)
  trees [list]         the watched trees, their branches and how many paths each changed
  trees scan           look for overlaps now (prints any recorded)
  trees rm <dir>|off   stop watching one tree, or all of them
  bridge on [interval] fetch the remotes every interval (default 5m); record a landed
                       event for each remote branch that moved and is not your own push
  bridge fetch         fetch now (prints any landed events)
  bridge [status]|off
  hub join <url> <token> [device]   connect this device to a hub (writes hub.conf)
  hub on [name]        share this repo's ledger through the hub (name: default from the
                       git remote, like github.com/you/repo; use the same name on every device)
  hub off              stop; the replica is kept as .sankofa/ledger.hub.jsonl
  hub [status]         online / offline, the hub name, writes queued while offline
  dash [repo...]       the dashboard in the terminal (sankofa-tui): what waits on you,
                       what is open, who holds what; menus, F1 for keys. Repos default to
                       this one, else the watched ones. dash summary [--json] prints it;
                       dash --gui opens it in a window (sankofa-gui: Windows, macOS, Linux)
  watch add [dir]      toast this repo's awaiting, overlap and landed events (sankofa-watch)
  watch list           show watched repos
  watch rm [dir]       stop watching a repo
  watch serve|once     the desktop notifier itself (what the sankofa-watch service runs)
  version

Ledger: <repo>/.sankofa/ledger.jsonl, append-only.
Config: <repo>/.sankofa/config, lines of key=value. Keys: require_note=0|1
Env:    SANKOFA_REQUIRE_NOTE overrides require_note; SANKOFA_AGENT names the writer;
        SANKOFA_SOCKET locates sankofad; SANKOFA_NO_DAEMON=1 skips it.

The Stop hook blocks (exit 2) only when all of these hold: require_note is on,
the ledger has open items, and this session changed something it has not yet
written up. "Changed" means an Edit/Write/MultiEdit/NotebookEdit call in the
session transcript, or a different git working tree, since the session's own
last ledger write. A reply that only answers a question is never blocked.
""";

    static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exit e)
        {
            if (e.Message.Length > 0) Console.Error.Write("sankofa: " + e.Message + "\n");
            return e.Code;
        }
    }

    static int Run(string[] args)
    {
        var cmd = args.Length > 0 ? args[0] : "";
        var rest = args.Length > 0 ? args[1..] : [];
        switch (cmd)
        {
            case "init": return Items.Init();
            case "note": return Items.Note(rest);
            case "todo": return Items.Todo(rest);
            case "awaiting": return Items.Awaiting(rest);
            case "done": return Items.Done(rest);
            case "open": return Items.Open();
            case "last": return Items.Last(rest);
            case "log": return Items.Log();
            case "version" or "-V" or "--version": Out.Line($"sankofa {Store.Version}"); return 0;
            case "" or "help" or "-h" or "--help": Console.Out.Write(Usage + "\n"); return 0;
            case "hook": return Hook.Run(rest);
            case "claim": return Leases.Claim(rest);
            case "release": return Leases.Release(rest);
            case "ask": return Leases.Ask(rest);
            case "leases": return Leases.List(rest);
            case "events": return Leases.Events(rest);
            case "daemon": return Leases.DaemonStatus();
            case "handoff": return Handoff.Run(rest);
            case "pickup": return Handoff.Pickup(rest);
            case "gate": return Admin.Gate(rest);
            case "trees": return Admin.Trees(rest);
            case "bridge": return Admin.Bridge(rest);
            case "hub": return Admin.Hub(rest);
            case "dash": return Admin.Dash(rest);
            case "setup": return Admin.Setup(rest);
            case "watch" when rest.Length > 0 && rest[0] is "serve" or "once": return Watch.Run(rest);
            case "watch": return Admin.WatchReg(rest);
        }
        throw new Exit($"unknown command '{cmd}' (try: sankofa help)");
    }

    /// <summary>A verb the port has not reached yet runs in the shell CLI kept beside this binary (bin/sankofa-sh).</summary>
    public static int Fallback(string cmd, string[] args)
    {
        var sh = Path.GetFullPath(Path.Combine(Env.SelfDir, "..", "sankofa-sh"));
        if (!File.Exists(sh)) throw new Exit($"'{cmd}' is not available in this build yet");
        return Env.RunInherit("sh", [sh, cmd, .. args]);
    }
}
