using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sankofa;

/// <summary>One ledger line. Field names and order match the 0.1 JSONL ledger.</summary>
public sealed class Entry
{
    [JsonPropertyName("ts")] public string Ts { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("session")] public string Session { get; set; } = "-";
    [JsonPropertyName("agent")] public string Agent { get; set; } = "main";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("ref")] public string? Ref { get; set; }
    [JsonPropertyName("wt")] public string? Wt { get; set; }
    [JsonPropertyName("glob")] public string? Glob { get; set; }
    [JsonPropertyName("expires")] public string? Expires { get; set; }
    /// <summary>Client-chosen id for one write, so a write whose answer was lost is never repeated.</summary>
    [JsonPropertyName("nonce")] public string? Nonce { get; set; }
    /// <summary>landed events: the remote-tracking branch (origin/x) and the commit it now points at.</summary>
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("sha")] public string? Sha { get; set; }
    /// <summary>0.7, through a hub: the device (from its token) and the client that wrote the event.</summary>
    [JsonPropertyName("device")] public string? Device { get; set; }
    [JsonPropertyName("client")] public string? Client { get; set; }
    /// <summary>Daemon-wide sequence number; only on the wire, never in the JSONL file.</summary>
    [JsonPropertyName("seq")] public long? Seq { get; set; }
    [JsonPropertyName("repo")] public string? Repo { get; set; }
}

public sealed class Lease
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("glob")] public string Glob { get; set; } = "";
    [JsonPropertyName("session")] public string Session { get; set; } = "";
    [JsonPropertyName("agent")] public string Agent { get; set; } = "";
    [JsonPropertyName("claimed")] public string Claimed { get; set; } = "";
    [JsonPropertyName("expires")] public string Expires { get; set; } = "";
    [JsonPropertyName("dirty")] public bool Dirty { get; set; }
    /// <summary>The device the holder is on (through a hub).</summary>
    [JsonPropertyName("device")] public string? Device { get; set; }
    /// <summary>held, released, done, expired</summary>
    [JsonPropertyName("state")] public string State { get; set; } = "";
}

public sealed class Request
{
    [JsonPropertyName("verb")] public string Verb { get; set; } = "";
    [JsonPropertyName("repo")] public string? Repo { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("session")] public string? Session { get; set; }
    [JsonPropertyName("agent")] public string? Agent { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("ref")] public string? Ref { get; set; }
    [JsonPropertyName("wt")] public string? Wt { get; set; }
    [JsonPropertyName("glob")] public string? Glob { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("ttl")] public long? Ttl { get; set; }
    [JsonPropertyName("n")] public int? N { get; set; }
    [JsonPropertyName("since")] public long? Since { get; set; }
    [JsonPropertyName("follow")] public bool Follow { get; set; }
    [JsonPropertyName("nonce")] public string? Nonce { get; set; }
    [JsonPropertyName("paths")] public List<string>? Paths { get; set; }
    [JsonPropertyName("inherit")] public bool Inherit { get; set; }
    [JsonPropertyName("idle")] public long? Idle { get; set; }
    /// <summary>bridge: seconds between fetches</summary>
    [JsonPropertyName("interval")] public long? Interval { get; set; }
    [JsonPropertyName("on")] public bool? On { get; set; }
    /// <summary>Who is asking: cli, gate:claude-code, mcp:&lt;name&gt; (recorded with the event on a hub).</summary>
    [JsonPropertyName("client")] public string? Client { get; set; }
    /// <summary>import: events written while the hub was unreachable, in order.</summary>
    [JsonPropertyName("entries")] public List<Entry>? Entries { get; set; }
    /// <summary>record: an event the daemon observed (overlap, landed, flag).</summary>
    [JsonPropertyName("entry")] public Entry? Entry { get; set; }
}

/// <summary>A worktree in a repo's overlap group.</summary>
public sealed class TreeInfo
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("branch")] public string Branch { get; set; } = "";
    /// <summary>"home", "worktree" (found by git worktree list) or "added" (registered by path)</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    /// <summary>paths this tree changes relative to where it left the home tree's history</summary>
    [JsonPropertyName("touched")] public int Touched { get; set; }
}

public sealed class BridgeInfo
{
    [JsonPropertyName("repo")] public string Repo { get; set; } = "";
    [JsonPropertyName("interval")] public long Interval { get; set; }
    [JsonPropertyName("last_fetch")] public string? LastFetch { get; set; }
    [JsonPropertyName("last_ok")] public string? LastOk { get; set; }
    [JsonPropertyName("last_error")] public string? LastError { get; set; }
    [JsonPropertyName("refs")] public int Refs { get; set; }
}

public sealed class Hit
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("lease")] public Lease Lease { get; set; } = new();
}

public sealed class Response
{
    [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("granted")] public bool? Granted { get; set; }
    [JsonPropertyName("renewed")] public bool? Renewed { get; set; }
    [JsonPropertyName("lease")] public Lease? Lease { get; set; }
    [JsonPropertyName("conflicts")] public List<Lease>? Conflicts { get; set; }
    [JsonPropertyName("warnings")] public List<Lease>? Warnings { get; set; }
    [JsonPropertyName("leases")] public List<Lease>? Leases { get; set; }
    [JsonPropertyName("entries")] public List<Entry>? Entries { get; set; }
    [JsonPropertyName("entry")] public Entry? Entry { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("hits")] public List<Hit>? Hits { get; set; }
    [JsonPropertyName("trees")] public List<TreeInfo>? Trees { get; set; }
    [JsonPropertyName("bridge")] public BridgeInfo? Bridge { get; set; }
    /// <summary>Hub answers to a request carrying <c>since</c>: every event of the repo after it, in order,
    /// so a device's replica stays a gapless copy. <c>more</c> when the tail was cut short.</summary>
    [JsonPropertyName("tail")] public List<Entry>? Tail { get; set; }
    [JsonPropertyName("more")] public bool? More { get; set; }
    /// <summary>import: the id each imported event got on the hub, by the id it had on the device.</summary>
    [JsonPropertyName("map")] public Dictionary<string, string>? Map { get; set; }
    [JsonPropertyName("device")] public string? Device { get; set; }
    /// <summary>hub-status: the hub's address, the replica's position, bytes queued while away.</summary>
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("cursor")] public long? Cursor { get; set; }
    [JsonPropertyName("queued")] public long? Queued { get; set; }

    public static Response Fail(string error) => new() { Ok = false, Error = error };
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Entry))]
[JsonSerializable(typeof(Lease))]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Response))]
[JsonSerializable(typeof(Hit))]
[JsonSerializable(typeof(TreeInfo))]
[JsonSerializable(typeof(BridgeInfo))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<Entry>))]
public partial class Json : JsonSerializerContext
{
    /// <summary>Like Default, but leaves quotes, apostrophes and non-ASCII text readable.</summary>
    public static Json Wire { get; } = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });

    /// <summary>The JSONL mirror format: no seq/repo, nulls omitted, compact.</summary>
    public static string Line(Entry e)
    {
        var copy = new Entry
        {
            Ts = e.Ts, Id = e.Id, Type = e.Type, Session = e.Session, Agent = e.Agent, Text = e.Text,
            Ref = e.Ref, Wt = e.Wt, Glob = e.Glob, Expires = e.Expires, Nonce = e.Nonce, Branch = e.Branch, Sha = e.Sha,
            Device = e.Device, Client = e.Client,
        };
        return JsonSerializer.Serialize(copy, Wire.Entry);
    }
}
