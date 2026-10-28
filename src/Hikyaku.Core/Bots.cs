using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

/// <summary>Bot Mode roster row — BookahHermes Bots.kt / profiles.list.</summary>
public sealed record Bot(
    string Name,
    bool IsDefault = false,
    string? DisplayName = null,
    string? Description = null,
    string? Model = null,
    string? Provider = null,
    BotSession? LastSession = null,
    BotSession? WorkerSession = null,
    BotSession? CanonicalSession = null,
    JsonObject? UiMeta = null,
    JsonObject? UiMetaRevisions = null,
    bool HasAvatar = false)
{
    public const string DefaultName = "default";

    public JsonObject MetaBlock =>
        UiMeta?["hermes-bots"] as JsonObject ?? new JsonObject();

    public BotMeta Meta => BotMeta.Of(UiMeta?["hermes-bots"]);

    public string Label =>
        Meta.Title
        ?? (string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim())
        ?? (Name.Equals(DefaultName, StringComparison.OrdinalIgnoreCase) ? "Shisha" : TitleCase(Name.Replace('-', ' ').Replace('_', ' ').Trim()));

    public string Handle => Name.Equals(DefaultName, StringComparison.OrdinalIgnoreCase) ? "hermes" : Name;

    public double? LastActivity()
    {
        double? Max(params double?[] xs)
        {
            double? m = null;
            foreach (var x in xs)
                if (x is { } v && (m is null || v > m)) m = v;
            return m;
        }
        return Max(CanonicalSession?.ActivityAt, LastSession?.ActivityAt, WorkerSession?.ActivityAt);
    }

    private static string TitleCase(string s) =>
        string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
}

/// <summary>Session summary on a roster row. OpenId is the live compression tip.</summary>
public sealed record BotSession(
    string? Id = null,
    string? ResolvedId = null,
    string? Title = null,
    string? Preview = null,
    double? StartedAt = null,
    double? LastActive = null,
    int? MessageCount = null)
{
    public string? OpenId =>
        !string.IsNullOrWhiteSpace(ResolvedId) ? ResolvedId :
        !string.IsNullOrWhiteSpace(Id) ? Id : null;

    public double? ActivityAt => LastActive ?? StartedAt;

    public bool IsChat(string? storedId) =>
        storedId is not null && (storedId == OpenId || storedId == Id);
}

public sealed record BotRoster(IReadOnlyList<Bot> Bots, bool TeammateProtocol);

public sealed record BotMeta(
    string? Title = null,
    string? Shape = null,
    string? Color = null,
    bool Custom = false,
    bool Hidden = false,
    string? ImageKind = null,
    bool Pinned = false,
    double? CreatedMs = null)
{
    public const string Key = "hermes-bots";

    public static BotMeta Of(JsonNode? element)
    {
        if (element is not JsonObject obj) return new BotMeta();
        string? Text(string k) => obj[k] is JsonValue v && v.TryGetValue<string>(out var s)
            ? (string.IsNullOrWhiteSpace(s) ? null : s.Trim()) : null;
        bool Flag(string k) => obj[k] is JsonValue v && v.TryGetValue(out bool b) && b;
        double? Created = null;
        if (obj["created"] is JsonValue cv && cv.TryGetValue(out double d)) Created = d;
        return new BotMeta(Text("title"), Text("shape"), Text("color"), Flag("custom"), Flag("hidden"),
            Text("imageKind"), Flag("pinned"), Created);
    }
}

public static class BotRosterParser
{
    public static BotRoster Parse(JsonNode? result)
    {
        if (result is not JsonObject obj) return new BotRoster(Array.Empty<Bot>(), false);
        var protocol = obj["bot_mode_protocol"] is JsonValue pv && pv.TryGetValue(out bool b) && b;
        var rows = obj["profiles"] as JsonArray ?? new JsonArray();
        var bots = new List<Bot>();
        foreach (var row in rows)
        {
            if (row is not JsonObject p) continue;
            try { bots.Add(MapBot(p)); }
            catch { /* skip malformed rows */ }
        }
        return new BotRoster(bots, protocol);
    }

    public static IReadOnlyList<Bot> ForRoster(IEnumerable<Bot> bots) =>
        bots.OrderByDescending(b => b.Meta.Pinned)
            .ThenByDescending(b => Math.Max(b.LastActivity() ?? 0, (b.Meta.CreatedMs ?? 0) / 1000.0))
            .ThenBy(b => b.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string? PreviewLine(Bot bot)
    {
        var text = bot.CanonicalSession?.Preview ?? bot.LastSession?.Preview;
        if (string.IsNullOrWhiteSpace(text)) return null;
        return Regex.Replace(
                Regex.Replace(
                    Regex.Replace(text, "```[\\s\\S]*?```", " "),
                    "`([^`\\n]*)`", "$1"),
                "\\s+", " ")
            .Trim();
    }

    private static Bot MapBot(JsonObject p)
    {
        static string? S(JsonObject o, string k) =>
            o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        static bool B(JsonObject o, string k) =>
            o[k] is JsonValue v && v.TryGetValue(out bool b) && b;
        var name = S(p, "name") ?? throw new JsonException("profile name required");
        return new Bot(
            Name: name,
            IsDefault: B(p, "is_default"),
            DisplayName: S(p, "display_name"),
            Description: S(p, "description"),
            Model: S(p, "model"),
            Provider: S(p, "provider"),
            LastSession: MapSession(p["last_session"] as JsonObject),
            WorkerSession: MapSession(p["worker_session"] as JsonObject),
            CanonicalSession: MapSession(p["canonical_session"] as JsonObject),
            UiMeta: p["ui_meta"] as JsonObject,
            UiMetaRevisions: p["ui_meta_revisions"] as JsonObject,
            HasAvatar: B(p, "has_avatar"));
    }

    private static BotSession? MapSession(JsonObject? o)
    {
        if (o is null) return null;
        static string? S(JsonObject obj, string k) =>
            obj[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        static double? D(JsonObject obj, string k) =>
            obj[k] is JsonValue v && v.TryGetValue(out double d) ? d : null;
        static int? I(JsonObject obj, string k) =>
            obj[k] is JsonValue v && v.TryGetValue(out int i) ? i : null;
        return new BotSession(
            Id: S(o, "id"),
            ResolvedId: S(o, "resolved_id"),
            Title: S(o, "title"),
            Preview: S(o, "preview"),
            StartedAt: D(o, "started_at"),
            LastActive: D(o, "last_active"),
            MessageCount: I(o, "message_count"));
    }
}

/// <summary>Minimal RPC surface used by bot/subagent helpers (JsonRpcClient implements).</summary>
public interface IJsonRpcPeer
{
    Task<JsonNode?> RequestAsync(string method, JsonObject? parameters = null,
        TimeSpan? timeout = null, CancellationToken ct = default);
}
