using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

/// <summary>
/// One thing the agent learned: a skill it wrote or a memory it keeps (BookahHermes <c>journey/Journey.kt</c> @ 5f00986b, the data behind
/// Desktop's star map). <see cref="Timestamp"/> is epoch seconds when the gateway knows it.
/// </summary>
public sealed record JourneyNode(
    string Id, string Label, string Kind, long? Timestamp, string? Category, int UseCount, string? State, bool Pinned, string? MemorySource)
{
    private static readonly Regex HtmlComment = new("<!--.*?(-->|$)", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Whitespace = new("\\s+", RegexOptions.Compiled);

    public bool IsMemory => Kind == "memory";
    public bool IsArchived => State == "archived";
    public bool IsUsed => !IsMemory && UseCount > 0;

    /// <summary>The label without the bookkeeping comments memory files carry (<c>&lt;!-- observed: … --&gt;</c>).</summary>
    public string Title
    {
        get
        {
            var t = Whitespace.Replace(HtmlComment.Replace(Label, " "), " ").Trim().TrimEnd(':').Trim();
            return t.Length > 0 ? t : Label;
        }
    }

    public string DetailLine => IsMemory
        ? string.Join(" · ", new[] { "Memory", LeafOf(MemorySource) }.Where(s => !string.IsNullOrWhiteSpace(s)))
        : string.Join(" · ", new[]
        {
            "Skill",
            Category is { Length: > 0 } c && c != "uncategorized" ? c : null,
            UseCount switch { 0 => null, 1 => "used once", var n => $"used {n} times" },
            IsArchived ? "archived" : null
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private static string? LeafOf(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Replace('\\', '/').Split('/')[^1];
}

public sealed record JourneyGraph(IReadOnlyList<JourneyNode> Nodes)
{
    public int Skills => Nodes.Count(n => !n.IsMemory);
    public int Memories => Nodes.Count(n => n.IsMemory);
}

/// <summary>A node's current text: the skill's SKILL.md or the memory chunk.</summary>
public sealed record JourneyNodeDetail(bool Ok, string? Kind, string? Label, string? Content);

public enum JourneyFilter { All, Used, Learned }

/// <summary>A month of the timeline.</summary>
public sealed record JourneyMonth(string Label, IReadOnlyList<JourneyNode> Nodes);

public static class JourneyTimeline
{
    /// <summary>
    /// Newest first, grouped by month (UTC), undated last. <b>Used</b> is a skill the agent has run; <b>Learned</b> is everything else
    /// (memories and skills not yet used): Desktop's labels are not defined in the docs, so this is Hikyaku's reading of them.
    /// </summary>
    public static IReadOnlyList<JourneyMonth> Group(IEnumerable<JourneyNode> nodes, JourneyFilter filter)
    {
        var shown = nodes.Where(n => filter switch { JourneyFilter.Used => n.IsUsed, JourneyFilter.Learned => !n.IsUsed, _ => true });
        return shown
            .OrderByDescending(n => n.Timestamp ?? long.MinValue)
            .ThenBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
            .GroupBy(n => MonthLabel(n.Timestamp))
            .Select(g => new JourneyMonth(g.Key, g.ToList()))   // a null timestamp sorts last, so "Undated" comes last
            .ToList();
    }

    /// <summary>"October 2026" for epoch seconds in UTC, or "Undated".</summary>
    public static string MonthLabel(long? epochSeconds)
    {
        if (epochSeconds is not { } s || s <= 0 || s > 253_402_300_799) return "Undated";
        return DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
    }
}

/// <summary>Read-only: what the profile's agent has learned (<c>GET api/learning/graph</c>, <c>GET api/learning/node</c>). Nothing here edits or deletes.</summary>
public sealed class JourneyApi(DashboardAuth auth)
{
    public const int MaxNodes = 2000;
    public const int MaxContentChars = 200_000;

    public async Task<JourneyGraph> GraphAsync(string profile, CancellationToken ct)
    {
        using var doc = await DashboardRest.SendAsync(auth, HttpMethod.Get, "api/learning/graph", profile, null, true, ct).ConfigureAwait(false);
        return ParseGraph(doc.RootElement);
    }

    public async Task<JourneyNodeDetail> NodeAsync(string profile, string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) return new JourneyNodeDetail(false, null, null, null);
        using var doc = await DashboardRest.SendAsync(auth, HttpMethod.Get,
            "api/learning/node?id=" + Uri.EscapeDataString(id), profile, null, false, ct).ConfigureAwait(false);
        var r = doc.RootElement;
        var content = DashboardRest.Str(r, "content");
        if (content is { } c && c.Length > MaxContentChars) content = c[..MaxContentChars] + "\n… (cut off)";
        return new JourneyNodeDetail(DashboardRest.Bool(r, "ok") ?? true, DashboardRest.Str(r, "kind"), DashboardRest.Str(r, "label"), content);
    }

    public static JourneyGraph ParseGraph(JsonElement root)
    {
        var nodes = new List<JourneyNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var arr = DashboardRest.ArrayOf(root, "nodes");
        if (arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var id = DashboardRest.Str(e, "id");
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
                var label = DashboardRest.Str(e, "label");
                nodes.Add(new JourneyNode(
                    id, string.IsNullOrWhiteSpace(label) ? id : label,
                    DashboardRest.Str(e, "kind") is { Length: > 0 } k ? k : "skill",
                    Epoch(e, "timestamp"), DashboardRest.Str(e, "category"),
                    Math.Max(DashboardRest.Int(e, "useCount"), DashboardRest.Int(e, "use_count")),
                    DashboardRest.Str(e, "state"), DashboardRest.Bool(e, "pinned") ?? false,
                    DashboardRest.Str(e, "memorySource") ?? DashboardRest.Str(e, "memory_source")));
                if (nodes.Count >= MaxNodes) break;
            }
        return new JourneyGraph(nodes);
    }

    /// <summary>Epoch seconds from a whole or fractional number or a numeric string; anything else is unknown.</summary>
    private static long? Epoch(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        double d;
        if (v.ValueKind == JsonValueKind.Number) d = v.GetDouble();
        else if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) d = p;
        else return null;
        return double.IsFinite(d) && d is > 0 and < 253_402_300_799 ? (long)d : null;
    }
}
