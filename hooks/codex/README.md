# Codex adapter

Codex runs `PreToolUse` hooks before tool calls. File edits arrive as `apply_patch`, with the
patch text in `tool_input.command`; the adapter (`daemon/sankofad/Adapters.cs`) reads the
`*** Add File:`, `*** Update File:`, `*** Delete File:` and `*** Move to:` headers to find the
paths. Codex's `PreToolUse` payload does not say which subagent made the call, so the lease
holder is the Codex session. The git pre-commit gate (`sankofa gate install`) is the backstop
for anything a hook does not see.

Add to `~/.codex/config.toml`, then review and trust it with `/hooks` inside Codex:

```toml
[[hooks.PreToolUse]]
matcher = "^apply_patch$"

[[hooks.PreToolUse.hooks]]
type = "command"
command = "sankofad gate codex"
timeout = 5
statusMessage = "sankofa: checking leases"
```

The ledger tools reach Codex as an MCP server; `install.sh` adds this to
`~/.codex/config.toml`:

```toml
[mcp_servers.sankofa]
command = "sankofad"
args = ["mcp"]
```

The PreToolUse gate is not verified against a live Codex session yet: the payload shape comes from the Codex hooks
documentation, and the patch headers from Codex's patch format.
