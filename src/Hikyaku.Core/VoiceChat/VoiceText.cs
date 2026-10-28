using System.Text;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

/// <summary>
/// A whole utterance that only says "stop" (or "never mind", "goodbye", …), optionally addressed to the agent, ends a voice
/// chat instead of being sent; "stop the container" is a real request (BookahHermes voice/Speech.kt @ 5f00986b). The
/// profile's own <c>voice.stop_phrases</c> replace the defaults when set; an empty list switches stop phrases off.
/// </summary>
public static partial class VoiceStopPhrases
{
    private static readonly string[] Defaults =
    [
        "stop", "stop listening", "stop it", "stop please", "please stop", "stop stop", "that is all", "that's all",
        "never mind", "nevermind", "end conversation", "end the conversation", "goodbye", "good bye", "bye", "cancel",
    ];

    [GeneratedRegex(@"[\p{P}]+")]
    private static partial Regex Punctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static bool IsStop(string transcript, IReadOnlyList<string>? profilePhrases, string? agentName)
    {
        var text = Normalize(transcript);
        if (text.Length == 0) return false;
        var agent = string.IsNullOrWhiteSpace(agentName) ? null : Normalize(agentName);
        var prefixes = new List<string>();
        if (agent is { Length: > 0 }) { prefixes.Add("hey " + agent); prefixes.Add(agent); }
        prefixes.AddRange(["hey hermes", "hermes", "okay", "ok", "hey"]);
        var prefix = prefixes.FirstOrDefault(p => text.StartsWith(p + " ", StringComparison.Ordinal));
        if (prefix is not null) text = text[prefix.Length..].Trim();
        var phrases = profilePhrases ?? Defaults;
        return phrases.Any(p => Normalize(p) == text);
    }

    /// <summary>Lower case, punctuation as spaces, spaces collapsed; the transcript and every phrase alike ("that's all").</summary>
    private static string Normalize(string s) => Spaces().Replace(Punctuation().Replace(s.ToLowerInvariant(), " "), " ").Trim();
}

/// <summary>
/// What of a reply can be spoken, and what is new since the last part was sent to the voice (BookahHermes
/// voice/SpeechStream.kt <c>speakableSoFar</c> @ 5f00986b). Code blocks are left out, and an open one holds back everything
/// after its start until it closes, so code is never read out; a file the agent attaches (<c>MEDIA:</c> on its own line or
/// among the words) is left out too. What could still become either waits for the next words, so the result only grows.
/// </summary>
public static partial class SpeakableText
{
    private const string Media = "MEDIA:";
    private const string Space = @"\s\p{Z}\x1C-\x1F";

    [GeneratedRegex(@"(?m)^([ \t]*)MEDIA:[^" + Space + @"]+[ \t]*\r?\n")]
    private static partial Regex MediaLine();

    [GeneratedRegex(@"(?<![^" + Space + @"])MEDIA:[^" + Space + @"]+?(?=[.,;:!?)\]]*(?:[" + Space + @"]|$))")]
    private static partial Regex MediaInline();

    public static string SoFar(string reply, bool complete)
    {
        // Fences are cut three backticks at a time from the left, so the one or two after the last whole fence could still become one.
        var trailing = 0;
        for (var i = reply.Length - 1; i >= 0 && reply[i] == '`'; i--) trailing++;
        var text = complete ? reply : reply[..^(trailing % 3)];
        var output = new StringBuilder();
        var start = 0;
        var inCode = false;
        for (var at = text.IndexOf("```", StringComparison.Ordinal); at >= 0; at = text.IndexOf("```", start, StringComparison.Ordinal))
        {
            if (!inCode) output.Append(text, start, at - start);
            inCode = !inCode;
            start = at + 3;
        }
        if (!inCode) output.Append(text, start, text.Length - start);
        if (!complete)
        {
            var s = output.ToString();
            var lastLine = s[(s.LastIndexOf('\n') + 1)..].TrimStart();
            if (lastLine.Length > 0 && (Media.StartsWith(lastLine, StringComparison.Ordinal) || lastLine.StartsWith(Media, StringComparison.Ordinal)))
                output.Length -= lastLine.Length;
            s = output.ToString();
            var wordStart = s.Length;
            while (wordStart > 0 && !char.IsWhiteSpace(s[wordStart - 1])) wordStart--;
            var lastWord = s[wordStart..];
            if (lastWord.Length > 0 && (Media.StartsWith(lastWord, StringComparison.Ordinal) || lastWord.StartsWith(Media, StringComparison.Ordinal)))
                output.Length -= lastWord.Length;
        }
        return MediaInline().Replace(MediaLine().Replace(output.ToString(), "$1"), "");
    }

    /// <summary>
    /// What of <paramref name="now"/> has not been sent, after <paramref name="sent"/>: what was added. Where the reply was
    /// rewritten under what was sent, the words from where the two part (what was already said cannot be taken back).
    /// </summary>
    public static string NewPart(string sent, string now)
    {
        if (now.StartsWith(sent, StringComparison.Ordinal)) return now[sent.Length..];
        var common = 0;
        while (common < sent.Length && common < now.Length && sent[common] == now[common]) common++;
        var wordStart = common;
        while (wordStart > 0 && !char.IsWhiteSpace(now[wordStart - 1])) wordStart--;
        return now[wordStart..];
    }
}
