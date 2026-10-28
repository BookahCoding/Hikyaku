using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

public enum ArtifactKind { Image, File, Link }

/// <summary>
/// One thing a chat produced or pointed at: a picture, a file on the gateway, or a link. Derived from stored chats only
/// (nothing here asks the gateway for an "artifacts" list: none is documented).
/// </summary>
public sealed record ArtifactItem(
    ArtifactKind Kind, string Name, string Source, string SessionId, string? SessionTitle, ProfileKey Profile, bool Generated, int Order)
{
    /// <summary>The file lives on the gateway's disk, so it is fetched through the authenticated dashboard download.</summary>
    public bool OnGateway => Source.StartsWith('/') || Source.StartsWith("file://", StringComparison.OrdinalIgnoreCase) || Source.StartsWith('~');
    public string GatewayPath => Source.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? Source["file://".Length..] : Source;
    public bool IsInlineData => Source.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    public bool IsWeb => Source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || Source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    /// <summary>A path with no root (relative to a working directory this app does not know) cannot be fetched safely.</summary>
    public bool CanSave => OnGateway || IsInlineData;
}

/// <summary>A picture or file a reply points at: a Markdown image, a <c>MEDIA:&lt;path&gt;</c> delivery token, a <c>::preview</c> directive or a Markdown link.</summary>
public sealed record ReplyMedia(string Source, string Name, ArtifactKind Kind, bool Generated = false);

/// <summary>The picture an <c>image_generate</c> call made (BookahHermes <c>GeneratedImage.kt</c> @ 5f00986b).</summary>
public sealed record GeneratedImage(string Source, IReadOnlyList<string> Echoes)
{
    public const string ToolName = "image_generate";

    /// <summary>From a stored tool row's JSON text; null for a failure or when no path is given.</summary>
    public static GeneratedImage? From(string? resultText)
    {
        if (string.IsNullOrWhiteSpace(resultText)) return null;
        var text = ToolDetails.UnwrapUntrusted(resultText).Trim();
        if (text.Length == 0 || text[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return null;
            if (o.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False) return null;
            string? Field(string key) => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()?.Trim() is { Length: > 0 } s ? s : null;
            var source = Field("host_image") ?? Field("image");
            if (source is null) return null;
            var echoes = new[] { "host_image", "image", "agent_visible_image" }.Select(Field).OfType<string>().Distinct().ToList();
            return new GeneratedImage(source, echoes);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Finds what a reply's text points at, leaving code alone (a path in a code sample is a sample).</summary>
public static class ReplyMediaExtractor
{
    private static readonly Regex MarkdownImage = new(
        "!\\[([^\\]\\n]*)\\]\\(\\s*<?([^\\s()<>]+(?:\\([^\\s()]*\\)[^\\s()<>]*)*)>?(?:\\s+\"[^\"]*\")?\\s*\\)", RegexOptions.Compiled);
    private static readonly Regex MediaToken = new(
        "(?<![A-Za-z0-9_])MEDIA:([^\\s]+?)(?=[.,;:!?)\\]]*(?:\\s|$))", RegexOptions.Compiled);
    private static readonly Regex PreviewDirective = new(
        "(?m)^[ \\t]*::preview\\{[^}]*?file=\"([^\"]+)\"[^}]*\\}[ \\t]*$", RegexOptions.Compiled);
    private static readonly Regex WebLink = new(
        "(?<!!)\\[([^\\]\\n]*)\\]\\((https?://[^\\s)]+)\\)", RegexOptions.Compiled);
    private static readonly Regex Fence = new("^[ ]{0,3}(`{3,}|~{3,})", RegexOptions.Compiled);
    private static readonly Regex Backticks = new("`+", RegexOptions.Compiled);
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "gif", "webp", "bmp", "svg" };

    public const int MaxTextChars = 200_000;

    public static IReadOnlyList<ReplyMedia> Extract(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        if (text.Length > MaxTextChars) text = text[..MaxTextChars];
        if (!text.Contains("![") && !text.Contains("MEDIA:") && !text.Contains("::preview") && !text.Contains("](http")) return [];
        text = text.Replace("\r\n", "\n");
        var masked = MaskCode(text);
        var found = new List<(int At, ReplyMedia Media)>();
        foreach (Match m in MarkdownImage.Matches(masked))
        {
            var source = text.Substring(m.Groups[2].Index, m.Groups[2].Length);
            var alt = text.Substring(m.Groups[1].Index, m.Groups[1].Length);
            found.Add((m.Index, new ReplyMedia(source, NameOf(source, alt), ImageOrFile(source, assumeImage: true))));
        }
        foreach (Match m in PreviewDirective.Matches(masked))
        {
            var source = text.Substring(m.Groups[1].Index, m.Groups[1].Length);
            found.Add((m.Index, new ReplyMedia(source, NameOf(source, ""), ImageOrFile(source, assumeImage: false))));
        }
        foreach (Match m in MediaToken.Matches(masked))
        {
            var source = text.Substring(m.Groups[1].Index, m.Groups[1].Length);
            found.Add((m.Index, new ReplyMedia(source, NameOf(source, ""), ImageOrFile(source, assumeImage: false))));
        }
        foreach (Match m in WebLink.Matches(masked))
        {
            var source = text.Substring(m.Groups[2].Index, m.Groups[2].Length);
            var label = text.Substring(m.Groups[1].Index, m.Groups[1].Length).Trim();
            found.Add((m.Index, new ReplyMedia(source, label.Length > 0 ? label : source, ArtifactKind.Link)));
        }
        return found.OrderBy(f => f.At).Select(f => f.Media).ToList();
    }

    private static ArtifactKind ImageOrFile(string source, bool assumeImage)
    {
        if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return ArtifactKind.Image;
        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return assumeImage ? ArtifactKind.Image : (IsImagePath(source) ? ArtifactKind.Image : ArtifactKind.Link);
        return IsImagePath(source) || assumeImage && !HasExtension(source) ? ArtifactKind.Image : ArtifactKind.File;
    }

    private static bool HasExtension(string source) => Path.GetExtension(source.Split('?')[0]).Length > 0;
    private static bool IsImagePath(string source) => ImageExtensions.Contains(Path.GetExtension(source.Split('?')[0]).TrimStart('.'));

    public static string NameOf(string source, string alt)
    {
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return string.IsNullOrWhiteSpace(alt) ? "image" : alt.Trim();
        var leaf = source.Split('?')[0].Replace('\\', '/');
        var name = leaf[(leaf.LastIndexOf('/') + 1)..];
        return name.Length > 0 ? name : (string.IsNullOrWhiteSpace(alt) ? "file" : alt.Trim());
    }

    /// <summary>The text with every character of its code (fenced blocks, one still streaming to the end, and spans) replaced by NUL.</summary>
    private static string MaskCode(string text)
    {
        var chars = text.ToCharArray();
        string? fence = null;
        var lineStart = 0;
        while (lineStart <= text.Length)
        {
            var lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = text.Length;
            var line = text[lineStart..lineEnd];
            var f = Fence.Match(line);
            if (fence is null)
            {
                if (f.Success && !(f.Groups[1].Value[0] == '`' && line[(f.Index + f.Length)..].Contains('`')))
                {
                    fence = f.Groups[1].Value;
                    Blank(chars, lineStart, lineEnd);
                }
                else MaskSpans(text, chars, lineStart, lineEnd);
            }
            else
            {
                Blank(chars, lineStart, lineEnd);
                if (f.Success && f.Groups[1].Value[0] == fence[0] && f.Groups[1].Value.Length >= fence.Length && line.Trim().Length == f.Groups[1].Value.Length)
                    fence = null;
            }
            lineStart = lineEnd + 1;
        }
        return new string(chars);
    }

    private static void Blank(char[] chars, int from, int to)
    {
        for (var i = from; i < to && i < chars.Length; i++) chars[i] = '\0';
    }

    /// <summary>A run of backticks to the next run as long, on one line.</summary>
    private static void MaskSpans(string text, char[] chars, int from, int to)
    {
        var runs = Backticks.Matches(text[from..to]).ToList();
        for (var i = 0; i < runs.Count; i++)
        {
            var close = -1;
            for (var j = i + 1; j < runs.Count; j++)
                if (runs[j].Length == runs[i].Length) { close = j; break; }
            if (close < 0) continue;
            Blank(chars, from + runs[i].Index, from + runs[close].Index + runs[close].Length);
            i = close;
        }
    }
}

/// <summary>Stored rows of one chat → its artifacts, newest first, each source once.</summary>
public static class ArtifactIndex
{
    public const int MaxPerChat = 200;

    public static IReadOnlyList<ArtifactItem> FromRows(IReadOnlyList<SessionMessage> rows, string sessionId, string? title, ProfileKey profile)
    {
        var results = new Dictionary<string, SessionMessage>(StringComparer.Ordinal);
        foreach (var r in rows)
            if (r.Role.Equals("tool", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(r.ToolCallId)) results[r.ToolCallId!] = r;

        // Pictures the tool made show on their own; the sandbox paths the model repeats in its text cannot be served.
        var echoes = new HashSet<string>(StringComparer.Ordinal);
        var generatedByRow = new Dictionary<int, List<GeneratedImage>>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (!r.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var call in r.ToolCalls ?? [])
            {
                if (call.Name != GeneratedImage.ToolName || call.Id is null || !results.TryGetValue(call.Id, out var res)) continue;
                if (GeneratedImage.From(res.Content) is not { } g) continue;
                if (!generatedByRow.TryGetValue(i, out var list)) generatedByRow[i] = list = [];
                list.Add(g);
                foreach (var e in g.Echoes) if (e != g.Source) echoes.Add(e);
            }
        }

        var generatedSources = generatedByRow.Values.SelectMany(g => g).Select(g => g.Source).ToHashSet(StringComparer.Ordinal);
        var items = new List<ArtifactItem>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (!r.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(r.DisplayKind, "hidden", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var g in generatedByRow.GetValueOrDefault(i) ?? [])
                items.Add(new ArtifactItem(ArtifactKind.Image, ReplyMediaExtractor.NameOf(g.Source, ""), g.Source, sessionId, title, profile, true, i));
            var body = string.IsNullOrEmpty(r.DisplayContent) ? r.Content : r.DisplayContent!;
            foreach (var m in ReplyMediaExtractor.Extract(body))
            {
                if (echoes.Contains(m.Source)) continue;
                items.Add(new ArtifactItem(m.Kind, m.Name, m.Source, sessionId, title, profile, false, i));
            }
        }
        // Newest first; the same source shown once (its newest mention).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ArtifactItem>();
        foreach (var item in items.OrderByDescending(x => x.Order))
        {
            if (!seen.Add(item.Source)) continue;
            // The picture the tool made stays "generated" even when a later reply names the same path again.
            result.Add(item.Generated || !generatedSources.Contains(item.Source) ? item : item with { Generated = true });
            if (result.Count >= MaxPerChat) break;
        }
        return result;
    }
}

public sealed record ArtifactScanResult(IReadOnlyList<ArtifactItem> Items, int ChatsRead, int ChatsFailed, bool Truncated);

/// <summary>Reads a bounded number of recent chats on demand (an explicit gesture) and indexes their artifacts.</summary>
public static class ArtifactScanner
{
    public const int MaxChats = 15;
    public const int RowsPerChat = 100;
    public const int MaxItems = 500;

    public static async Task<ArtifactScanResult> ScanAsync(
        IReadOnlyList<SessionSummary> chats, ProfileKey profile,
        Func<string, CancellationToken, Task<SessionMessagesPage>> load, CancellationToken ct = default)
    {
        var items = new List<ArtifactItem>();
        int read = 0, failed = 0;
        foreach (var chat in chats.Take(MaxChats))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var page = await load(chat.Id, ct).ConfigureAwait(false);
                read++;
                items.AddRange(ArtifactIndex.FromRows(page.Messages, chat.Id, chat.Title, profile));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or AuthFailedException or InvalidOperationException or JsonException)
            {
                failed++;      // one unreadable chat does not stop the rest
            }
        }
        var truncated = chats.Count > MaxChats || items.Count > MaxItems;
        return new ArtifactScanResult(items.Take(MaxItems).ToList(), read, failed, truncated);
    }
}

/// <summary>Where saved artifact bytes may come from: inline pictures are decoded here, bounded.</summary>
public static class ArtifactData
{
    public const int MaxInlineBytes = 15 * 1024 * 1024;

    public static byte[]? DecodeDataUrl(string source)
    {
        if (!source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        var comma = source.IndexOf(',');
        if (comma < 0 || !source[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)) return null;
        var b64 = source[(comma + 1)..];
        if (b64.Length > MaxInlineBytes * 4L / 3 + 8) return null;
        try
        {
            var bytes = Convert.FromBase64String(b64);
            return bytes.Length <= MaxInlineBytes ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A file name that is safe to offer in a save dialog.</summary>
    public static string SafeName(ArtifactItem item)
    {
        var name = ReplyMediaExtractor.NameOf(item.Source, item.Name);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().Trim('.');
        if (name.Length == 0) name = "artifact";
        if (item.IsInlineData && !Path.HasExtension(name)) name += ".png";
        return name.Length > 120 ? name[..120] : name;
    }
}
