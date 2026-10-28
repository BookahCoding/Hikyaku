using System.Text;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

/// <summary>Why a reply cannot be read aloud (null when it can).</summary>
public enum SpeechTextProblem { None, Empty, TooLong }

public sealed record SpeechTextResult(string Text, SpeechTextProblem Problem)
{
    public bool Ok => Problem == SpeechTextProblem.None;
}

/// <summary>
/// Turns a markdown reply into text that is sensible to hear: code is replaced by a spoken note,
/// links by their text, and markup characters are dropped. Pure; nothing leaves the device here.
/// </summary>
public static class SpeechText
{
    /// <summary>Longest reply sent to the gateway's text-to-speech in one go.</summary>
    public const int MaxChars = 20_000;
    public const string CodeNote = "code omitted";

    private static readonly Regex Fence = new(@"^[ ]{0,3}(```|~~~)", RegexOptions.Compiled);
    private static readonly Regex Image = new(@"!\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex InlineCode = new(@"`+[^`]*`+", RegexOptions.Compiled);
    private static readonly Regex BareUrl = new(@"https?://\S+", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s+", RegexOptions.Compiled);
    private static readonly Regex Quote = new(@"^\s{0,3}>\s?", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^\s*(?:[-*+]|\d+[.)])\s+", RegexOptions.Compiled);
    private static readonly Regex Emphasis = new(@"(\*\*|__|\*|~~)", RegexOptions.Compiled);
    private static readonly Regex Rule = new(@"^\s*([-*_]\s*){3,}$", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \t]+", RegexOptions.Compiled);

    public static SpeechTextResult Prepare(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return new SpeechTextResult("", SpeechTextProblem.Empty);

        var sb = new StringBuilder();
        var inFence = false;
        var noteWritten = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (Fence.IsMatch(raw))
            {
                inFence = !inFence;
                if (inFence && !noteWritten) { sb.Append(CodeNote).Append(".\n"); noteWritten = true; }
                continue;
            }
            if (inFence) continue;
            noteWritten = false;
            if (Rule.IsMatch(raw)) continue;

            var line = raw;
            line = Heading.Replace(line, "");
            line = Quote.Replace(line, "");
            line = Bullet.Replace(line, "");
            line = Image.Replace(line, "$1");
            line = Link.Replace(line, "$1");
            line = InlineCode.Replace(line, CodeNote);
            line = BareUrl.Replace(line, "link");
            line = Emphasis.Replace(line, "");
            line = Spaces.Replace(line, " ").Trim();
            if (line.Length == 0) { if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n'); continue; }
            sb.Append(line).Append('\n');
        }

        var text = sb.ToString().Trim();
        if (text.Length == 0) return new SpeechTextResult("", SpeechTextProblem.Empty);
        if (text.Length > MaxChars) return new SpeechTextResult("", SpeechTextProblem.TooLong);
        return new SpeechTextResult(text, SpeechTextProblem.None);
    }
}
