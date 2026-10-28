namespace Hikyaku.Core;

/// <summary>
/// Literal, case-insensitive search over a chat's message texts (Find in chat). Ordinal comparison, so a match is
/// exactly what was typed: no regex, no accent folding. Bounded so a huge transcript cannot stall the UI.
/// </summary>
public static class TranscriptFinder
{
    public const int MaxMatches = 5000;

    /// <summary>Indexes (in transcript order) of the texts that contain the trimmed <paramref name="query"/>.</summary>
    public static IReadOnlyList<int> Find(IReadOnlyList<string> texts, string? query)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q)) return [];
        var hits = new List<int>();
        for (var i = 0; i < texts.Count && hits.Count < MaxMatches; i++)
            if (texts[i] is { } t && t.Contains(q, StringComparison.OrdinalIgnoreCase)) hits.Add(i);
        return hits;
    }

    public static int Next(int current, int count) => count <= 0 ? -1 : (current + 1) % count;

    public static int Previous(int current, int count) => count <= 0 ? -1 : (current - 1 + count) % count;
}
