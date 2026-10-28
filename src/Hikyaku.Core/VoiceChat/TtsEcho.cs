using System.Text.RegularExpressions;

namespace Hikyaku.Core;

/// <summary>
/// Whether a transcript is the speaker hearing its own voice rather than someone cutting in: close enough to the reply that
/// was playing, or to a stretch of it its own length (Desktop's isTtsEcho, after tools/voice_mode_transcript.is_tts_echo;
/// BookahHermes voice/BargeIn.kt @ 5f00986b).
/// </summary>
public static partial class TtsEcho
{
    private const double EchoSimilarity = 0.6;
    /// <summary>Shorter than this, a real one-word cut-in ("yes") would match some window of the reply.</summary>
    private const int MinEchoFragment = 10;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static bool IsEcho(string transcript, string spoken)
    {
        var a = EchoForm(transcript);
        var b = EchoForm(spoken);
        if (a.Count == 0 || b.Count == 0) return false;
        if (SequenceRatio(a, b) >= EchoSimilarity) return true;
        if (a.Count < MinEchoFragment || a.Count >= b.Count) return false;
        // A stretch cannot match better than the letters it shares with the transcript (difflib's quick_ratio), counted as it
        // slides along the reply; only a stretch that clears that is compared in full.
        var need = new Dictionary<int, int>();
        foreach (var x in a) need[x] = need.GetValueOrDefault(x) + 1;
        var have = new Dictionary<int, int>();
        var shared = 0;
        void Add(int x) { var n = have.GetValueOrDefault(x); have[x] = n + 1; if (n < need.GetValueOrDefault(x)) shared++; }
        void Drop(int x) { var n = have[x]; have[x] = n - 1; if (n <= need.GetValueOrDefault(x)) shared--; }
        for (var j = 0; j < a.Count; j++) Add(b[j]);
        for (var start = 0; start <= b.Count - a.Count; start++)
        {
            if (start > 0)
            {
                Drop(b[start - 1]);
                Add(b[start + a.Count - 1]);
            }
            if (shared >= a.Count * EchoSimilarity && SequenceRatio(a, b.GetRange(start, a.Count)) >= EchoSimilarity) return true;
        }
        return false;
    }

    /// <summary>Code points, like Python's str, lowercased with the spaces collapsed.</summary>
    private static List<int> EchoForm(string text)
    {
        var s = Spaces().Replace(text, " ").Trim().ToLowerInvariant();
        var points = new List<int>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                points.Add(char.ConvertToUtf32(s[i], s[i + 1]));
                i++;
            }
            else points.Add(s[i]);
        }
        return points;
    }

    /// <summary>Python's <c>difflib.SequenceMatcher(None, a, b).ratio()</c>, autojunk and all.</summary>
    public static double SequenceRatio<T>(IReadOnlyList<T> a, IReadOnlyList<T> b) where T : notnull
    {
        var total = a.Count + b.Count;
        if (total == 0) return 1.0;
        var b2j = new Dictionary<T, List<int>>();
        for (var j = 0; j < b.Count; j++)
        {
            if (!b2j.TryGetValue(b[j], out var list)) b2j[b[j]] = list = new List<int>();
            list.Add(j);
        }
        // Elements in more than 1% of a long b never start a match, though they can extend one.
        if (b.Count >= 200)
        {
            var ntest = b.Count / 100 + 1;
            foreach (var key in b2j.Where(kv => kv.Value.Count > ntest).Select(kv => kv.Key).ToList()) b2j.Remove(key);
        }

        (int I, int J, int K) Longest(int alo, int ahi, int blo, int bhi)
        {
            int besti = alo, bestj = blo, best = 0;
            var j2len = new Dictionary<int, int>();
            for (var i = alo; i < ahi; i++)
            {
                var next = new Dictionary<int, int>();
                if (b2j.TryGetValue(a[i], out var js))
                {
                    foreach (var j in js)
                    {
                        if (j < blo) continue;
                        if (j >= bhi) break;
                        var k = j2len.GetValueOrDefault(j - 1) + 1;
                        next[j] = k;
                        if (k > best) { besti = i - k + 1; bestj = j - k + 1; best = k; }
                    }
                }
                j2len = next;
            }
            var eq = EqualityComparer<T>.Default;
            while (besti > alo && bestj > blo && eq.Equals(a[besti - 1], b[bestj - 1])) { besti--; bestj--; best++; }
            while (besti + best < ahi && bestj + best < bhi && eq.Equals(a[besti + best], b[bestj + best])) best++;
            return (besti, bestj, best);
        }

        var matches = 0;
        var queue = new Stack<(int, int, int, int)>();
        queue.Push((0, a.Count, 0, b.Count));
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue.Pop();
            var (i, j, k) = Longest(alo, ahi, blo, bhi);
            if (k <= 0) continue;
            matches += k;
            if (alo < i && blo < j) queue.Push((alo, i, blo, j));
            if (i + k < ahi && j + k < bhi) queue.Push((i + k, ahi, j + k, bhi));
        }
        return 2.0 * matches / total;
    }
}
