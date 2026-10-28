namespace Hikyaku.Core;

/// <summary>One thing the command palette can do or open. Matching looks at the title, keywords and group only.</summary>
public sealed record PaletteItem(string Id, string Title, string Group = "", string Hint = "", IReadOnlyList<string>? Keywords = null);

/// <summary>
/// Literal, case-insensitive palette matching. Every word of the query must match something; a title prefix beats a word prefix
/// beats a substring, keywords and the group rank below the title, and loose letters ("nwc" for "New chat") only match as a last
/// resort. No regex, no accent folding. Ties keep the registry order, and the list is capped.
/// </summary>
public static class PaletteSearch
{
    public const int MaxResults = 12;

    public static IReadOnlyList<PaletteItem> Filter(IEnumerable<PaletteItem> items, string? query)
    {
        var words = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var list = items.ToList();
        if (words.Length == 0) return list.Take(MaxResults).ToList();

        return list
            .Select((item, index) => (item, index, score: Score(item, words)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Take(MaxResults)
            .Select(x => x.item)
            .ToList();
    }

    private static int Score(PaletteItem item, string[] words)
    {
        var total = 0;
        for (var w = 0; w < words.Length; w++)
        {
            var s = ScoreWord(item, words[w], isFirst: w == 0);
            if (s == 0) return 0;                       // every word must match
            total += s;
        }
        return total;
    }

    private static int ScoreWord(PaletteItem item, string word, bool isFirst)
    {
        var title = item.Title ?? "";
        var best = 0;
        if (title.StartsWith(word, StringComparison.OrdinalIgnoreCase)) best = isFirst ? 150 : 100;
        else if (StartsAWord(title, word)) best = 100;
        else if (title.Contains(word, StringComparison.OrdinalIgnoreCase)) best = 60;

        foreach (var k in item.Keywords ?? [])
        {
            if (k is null) continue;
            if (k.StartsWith(word, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, 40);
            else if (k.Contains(word, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, 30);
        }

        var group = item.Group ?? "";
        if (group.Contains(word, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, 10);

        if (best == 0 && word.Length >= 2 && IsSubsequence(title, word)) best = 5;
        return best;
    }

    private static bool StartsAWord(string text, string word)
    {
        for (var i = 1; i < text.Length; i++)
            if (!char.IsLetterOrDigit(text[i - 1]) && text.AsSpan(i).StartsWith(word, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsSubsequence(string text, string word)
    {
        var w = 0;
        foreach (var c in text)
        {
            if (w < word.Length && char.ToUpperInvariant(c) == char.ToUpperInvariant(word[w])) w++;
        }
        return w == word.Length;
    }
}
