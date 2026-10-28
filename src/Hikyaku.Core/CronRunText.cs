using System.Text.RegularExpressions;

namespace Hikyaku.Core;

public enum RunStatus { Ok, Failed, Silent }

/// <summary>What a scheduled run found: its status, the result text, and for a failed run the reason Hermes gave.</summary>
public sealed record RunText(RunStatus Status, string Text, string? Reason);

/// <summary>
/// Reads a run document as hermes-agent writes it (cron/scheduler.py and cron/scheduler_prompt.py): a header
/// (<c># Cron Job: …</c>, <c>**Job ID:**</c>, <c>**Run Time:**</c>, <c>**Mode:**</c> or <c>**Schedule:**</c>), then either
/// <c>---</c> and a script's output, a <c>**Status:**</c> line (script failed, silent, BLOCKED) or the framed
/// <c>## Prompt</c> / <c>## Response</c> sections of an agent run. Also reads the gateway's 180-character preview of one, which
/// folds all whitespace. Whatever else comes is shown as it is.
/// </summary>
public static partial class CronRunText
{
    private const string Title = "# Cron Job:";
    private static readonly string[] HeaderKeys = ["**Job ID:**", "**Run Time:**", "**Mode:**", "**Schedule:**", "**Prompt Characters:**"];

    [GeneratedRegex(@"(?m)^\*\*Response Characters:\*\* (\d+)\n## Response\n\n")]
    private static partial Regex ResponseFrame();

    public static RunText Parse(string? document)
    {
        var doc = (document ?? "").Replace("\r\n", "\n");
        if (doc.Trim().Length == 0) return new RunText(RunStatus.Silent, "", null);

        // An agent run: the response is framed by its length, so a heading inside it cannot cut it short.
        if (ResponseFrame().Match(doc) is { Success: true } frame)
        {
            var start = frame.Index + frame.Length;
            var text = int.TryParse(frame.Groups[1].Value, out var n) && n >= 0 && start + n <= doc.Length
                ? doc.Substring(start, n) : doc[start..];
            return Ok(text);
        }
        var heading = doc.IndexOf("## Response\n\n", StringComparison.Ordinal);
        if (heading >= 0) return Ok(doc[(heading + "## Response\n\n".Length)..]);

        if (!doc.TrimStart().StartsWith(Title, StringComparison.Ordinal)) return Ok(doc);
        return doc.Contains('\n') ? FromLines(doc) : FromFolded(doc);
    }

    /// <summary>
    /// True when an agent's final answer is a cron silence marker (<c>[SILENT]</c>, <c>NO_REPLY</c>, and the same
    /// forms Hermes treats as silence), so the Inbox says "Nothing new" instead of showing the marker.
    /// </summary>
    public static bool IsCronSilence(string? text)
    {
        var stripped = (text ?? "").Replace("\r\n", "\n").Trim();
        if (stripped.Length == 0) return false;
        if (stripped.StartsWith("[SILENT]", StringComparison.OrdinalIgnoreCase)
            || stripped.StartsWith("[\u9759\u9ED8]", StringComparison.Ordinal)
            || stripped.StartsWith("[\u6C89\u9ED8]", StringComparison.Ordinal))
            return true;
        var lines = stripped.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        return IsSilenceMarker(stripped) || IsSilenceMarker(lines[0]) || IsSilenceMarker(lines[^1]);
    }

    private static bool IsSilenceMarker(string value)
    {
        if (value.Length is 0 or > 64) return false;
        var canon = StripEdgePunctuation(string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        return canon.ToUpperInvariant() is "[SILENT]" or "SILENT" or "NO_REPLY" or "NO REPLY"
            or "[\u9759\u9ED8]" or "\u9759\u9ED8" or "[\u6C89\u9ED8]" or "\u6C89\u9ED8";
    }

    private static string StripEdgePunctuation(string value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsEdgePunctuation(value[start])) start++;
        while (end > start && IsEdgePunctuation(value[end - 1])) end--;
        return value[start..end].Trim();
    }

    private static bool IsEdgePunctuation(char ch) => ch is not '[' and not ']' && char.IsPunctuation(ch);

    /// <summary>The first few non-empty lines, cut with "…" when there is more.</summary>
    public static string Excerpt(string text, int maxLines = 3, int maxChars = 280)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        var excerpt = string.Join("\n", lines.Take(maxLines));
        var more = lines.Count > maxLines;
        if (excerpt.Length > maxChars - 1) return excerpt[..(maxChars - 1)] + "…";
        return more ? excerpt + "…" : excerpt;
    }

    private static RunText FromLines(string doc)
    {
        string? status = null;
        var lines = doc.Split('\n');
        var i = 0;
        for (; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith(Title, StringComparison.Ordinal) || HeaderKeys.Any(k => line.StartsWith(k, StringComparison.Ordinal)))
                continue;
            if (line.StartsWith("**Status:**", StringComparison.Ordinal)) { status = line["**Status:**".Length..].Trim(); continue; }
            break;
        }
        if (i < lines.Length && lines[i].Trim() == "---") i++;
        return WithStatus(status, string.Join("\n", lines.Skip(i)));
    }

    private static RunText FromFolded(string doc)
    {
        var separator = doc.IndexOf(" --- ", StringComparison.Ordinal);
        if (separator >= 0) return Ok(doc[(separator + 5)..]);
        var at = doc.IndexOf("**Status:** ", StringComparison.Ordinal);
        if (at < 0)
        {
            var errorAt = doc.IndexOf("Error:", StringComparison.Ordinal);
            if (errorAt >= 0)
            {
                var error = doc[errorAt..].Trim();
                return new RunText(RunStatus.Failed, error, error);          // a config doc folded into the preview
            }
            return new RunText(RunStatus.Silent, "", null);                  // only a header, cut off by the preview's length
        }
        var rest = doc[(at + "**Status:** ".Length)..];
        foreach (var known in new[]
        {
            "monitor source failed", "no_change (agent run suppressed)", "no_change",
            "script failed", "BLOCKED (configuration)", "BLOCKED", "silent"
        })
            if (rest.StartsWith(known, StringComparison.OrdinalIgnoreCase))
                return WithStatus(known, rest[known.Length..]);
        return WithStatus(null, rest);
    }

    private static RunText WithStatus(string? status, string body)
    {
        var text = body.Trim();
        if (status is { } s)
        {
            // hermes-agent writes "script failed", "monitor source failed", and any later "… failed".
            if (s.Contains("failed", StringComparison.OrdinalIgnoreCase)) return new RunText(RunStatus.Failed, text, s);
            if (s.StartsWith("silent", StringComparison.OrdinalIgnoreCase) || s.StartsWith("no_change", StringComparison.OrdinalIgnoreCase))
                return new RunText(RunStatus.Silent, "", null);
            if (s.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase))
                return new RunText(RunStatus.Failed, text, s);
        }
        if (text.StartsWith("Script gate returned", StringComparison.Ordinal) && text.Contains("wakeAgent=false", StringComparison.Ordinal))
            return new RunText(RunStatus.Silent, "", null);
        if (text.StartsWith("Error:", StringComparison.Ordinal)) return new RunText(RunStatus.Failed, text, text);
        return text.Length == 0 ? new RunText(RunStatus.Silent, "", null) : new RunText(RunStatus.Ok, text, null);
    }

    private static RunText Ok(string text)
    {
        var t = text.Trim();
        return t.Length == 0 ? new RunText(RunStatus.Silent, "", null) : new RunText(RunStatus.Ok, t, null);
    }
}
