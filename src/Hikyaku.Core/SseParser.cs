using System.Runtime.CompilerServices;
using System.Text;

namespace Hikyaku.Core;

public static class SseParser
{
    /// <summary>Yields the joined <c>data:</c> payload of each Server-Sent Event.</summary>
    public static async IAsyncEnumerable<string> ReadDataAsync(TextReader reader,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0) { yield return data.ToString(); data.Clear(); }
                continue;
            }
            if (line[0] == ':') continue; // comment / keep-alive
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var v = line.AsSpan(5);
                if (v.Length > 0 && v[0] == ' ') v = v[1..];
                if (data.Length > 0) data.Append('\n');
                data.Append(v);
            }
        }
        if (data.Length > 0) yield return data.ToString();
    }
}
