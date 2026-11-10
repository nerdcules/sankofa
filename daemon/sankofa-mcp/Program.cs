using Sankofa;

// sankofa-mcp: the sankofa ledger and leases as MCP tools on stdio (also reached as `sankofad mcp`).
if (args.Length > 0 && args[0] is "version" or "--version") { Console.WriteLine($"sankofa-mcp {Store.Version}"); return 0; }
return Mcp.Serve();
