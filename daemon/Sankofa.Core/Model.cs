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

    public static Response Fail(string error) => new() { Ok = false, Error = error };
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Entry))]
[JsonSerializable(typeof(Lease))]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Response))]
[JsonSerializable(typeof(Hit))]
[JsonSerializable(typeof(string))]
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
            Ref = e.Ref, Wt = e.Wt, Glob = e.Glob, Expires = e.Expires, Nonce = e.Nonce,
        };
        return JsonSerializer.Serialize(copy, Wire.Entry);
    }
}
