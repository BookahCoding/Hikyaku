using System.Text;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

/// <summary>One formula found in a reply: TeX source and whether it stands alone (display).</summary>
public readonly record struct MathFormula(string Latex, bool Display);

/// <summary>
/// Finds TeX math in Markdown and turns each formula into an image link the renderer can't mangle.
/// Ported from BookahHermes <c>MathMarkup.kt</c> (currency/code/streaming heuristics).
/// Display: <c>$$…$$</c> / <c>\[…\]</c>; inline: <c>\(…\)</c> / <c>$…$</c>.
/// Prices stay text; code spans/fences untouched; incomplete stream stays readable.
/// </summary>
public static partial class MathMarkup
{
    public const string Scheme = "herald-math:";
    public const int MaxLatexChars = 2048;
    public const int MaxBraceDepth = 64;

    /// <summary>Markdown with formulas as <c>![math](herald-math:…)</c> links; unchanged if no <c>$</c>/<c>\</c>.</summary>
    public static string Prepare(string markdown)
    {
        if (!markdown.Contains('$') && !markdown.Contains('\\')) return markdown;
        var chars = markdown.ToCharArray();
        var protectedMask = ProtectedMask(chars);
        var output = new StringBuilder(markdown.Length);
        var index = 0;
        while (index < chars.Length)
        {
            if (TryDisplayAt(chars, index, protectedMask, out var close, out var closeLen))
            {
                var latex = markdown[(index + 2)..close].Trim();
                if (latex.Length > 0)
                {
                    AppendDisplay(output, markdown, index, close + closeLen, latex);
                    index = close + closeLen;
                    continue;
                }
            }
            if (TryInlineAt(chars, index, protectedMask, out var openLen, out close, out closeLen))
            {
                output.Append(Link(markdown[(index + openLen)..close].Trim(), display: false));
                index = close + closeLen;
                continue;
            }
            output.Append(chars[index]);
            index++;
        }
        return output.ToString();
    }

    public static MathFormula? TryParseFormulaLink(string link)
    {
        if (!link.StartsWith(Scheme, StringComparison.Ordinal)) return null;
        var rest = link[Scheme.Length..];
        bool display;
        if (rest.StartsWith("d:", StringComparison.Ordinal)) display = true;
        else if (rest.StartsWith("i:", StringComparison.Ordinal)) display = false;
        else return null;
        try
        {
            var latex = Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(rest[2..])));
            return new MathFormula(latex, display);
        }
        catch
        {
            return null;
        }
    }

    public static IEnumerable<MathFormula> ExtractFormulas(string prepared)
    {
        var i = 0;
        while (i < prepared.Length)
        {
            var start = prepared.IndexOf("![math](", i, StringComparison.Ordinal);
            if (start < 0) yield break;
            var linkStart = start + "![math](".Length;
            var end = prepared.IndexOf(')', linkStart);
            if (end < 0) yield break;
            var link = prepared[linkStart..end];
            if (TryParseFormulaLink(link) is { } f)
                yield return f;
            i = end + 1;
        }
    }

    public static bool IsOversize(string latex) => latex.Length > MaxLatexChars;

    public static bool ExceedsNesting(string latex)
    {
        var depth = 0;
        var max = 0;
        foreach (var c in latex)
        {
            if (c is '{' or '[' or '(')
            {
                depth++;
                if (depth > max) max = depth;
            }
            else if (c is '}' or ']' or ')')
                depth = Math.Max(0, depth - 1);
        }
        return max > MaxBraceDepth;
    }

    public static bool LooksLikeMath(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return false;
        if ("/=,".Contains(trimmed[^1])) return false;
        if (SingleSymbolRegex().IsMatch(trimmed)) return true;
        if (LooksLikeAssignment(trimmed)) return true;
        if (NumericTupleRegex().IsMatch(trimmed)) return true;
        if (trimmed.Contains('\\') || trimmed.Contains('^') || trimmed.Contains('_')) return true;
        return OperatorBetweenRegex().IsMatch(trimmed);
    }

    private static string Link(string latex, bool display)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(latex))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "![math](" + Scheme + (display ? "d:" : "i:") + b64 + ")";
    }

    private static string PadBase64(string urlSafe)
    {
        var s = urlSafe.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return s;
    }

    private static void AppendDisplay(StringBuilder output, string source, int start, int end, string latex)
    {
        var lineStart = start == 0 ? 0 : source.LastIndexOf('\n', start - 1) + 1;
        var indent = source[lineStart..start];
        var lineEnd = source.IndexOf('\n', end);
        if (lineEnd < 0) lineEnd = source.Length;
        var alone = string.IsNullOrWhiteSpace(indent) && string.IsNullOrWhiteSpace(source[end..lineEnd]);
        if (alone)
        {
            if (lineStart > 0) output.Append('\n').Append(indent);
            output.Append(Link(latex, display: true));
            output.Append('\n');
        }
        else
            output.Append(Link(latex, display: true));
    }

    private static bool TryDisplayAt(char[] chars, int index, bool[] protectedMask, out int close, out int closeLen)
    {
        close = 0;
        closeLen = 0;
        string closer;
        if (Matches(chars, index, "$$", protectedMask)) closer = "$$";
        else if (Matches(chars, index, "\\[", protectedMask)) closer = "\\]";
        else return false;
        if (IsEscaped(chars, index)) return false;
        var i = index + 2;
        while (i < chars.Length)
        {
            if (Matches(chars, i, closer, protectedMask) && !IsEscaped(chars, i))
            {
                var inner = new string(chars, index + 2, i - (index + 2));
                if (closer == "$$" || LooksLikeMath(inner))
                {
                    close = i;
                    closeLen = 2;
                    return true;
                }
                return false;
            }
            i++;
        }
        return false;
    }

    private static bool TryInlineAt(char[] chars, int index, bool[] protectedMask,
        out int openLen, out int close, out int closeLen)
    {
        openLen = 0;
        close = 0;
        closeLen = 0;
        if (IsEscaped(chars, index)) return false;
        if (Matches(chars, index, "\\(", protectedMask))
        {
            var i = index + 2;
            while (i < chars.Length && chars[i] != '\n')
            {
                if (Matches(chars, i, "\\)", protectedMask) && !IsEscaped(chars, i))
                {
                    var inner = new string(chars, index + 2, i - (index + 2));
                    if (!LooksLikeMath(inner)) return false;
                    openLen = 2;
                    close = i;
                    closeLen = 2;
                    return true;
                }
                i++;
            }
            return false;
        }
        if (!IsSingleDollar(chars, index, protectedMask)) return false;
        if (index + 1 >= chars.Length || char.IsWhiteSpace(chars[index + 1])) return false;
        var j = index + 1;
        while (j < chars.Length && chars[j] != '\n')
        {
            if (IsSingleDollar(chars, j, protectedMask) && !IsEscaped(chars, j))
            {
                var inner = new string(chars, index + 1, j - (index + 1));
                var closes = !char.IsWhiteSpace(chars[j - 1])
                             && (j + 1 >= chars.Length || !char.IsDigit(chars[j + 1]));
                if (closes && LooksLikeMath(inner))
                {
                    openLen = 1;
                    close = j;
                    closeLen = 1;
                    return true;
                }
                return false;
            }
            j++;
        }
        return false;
    }

    private static bool IsSingleDollar(char[] chars, int index, bool[] protectedMask) =>
        chars[index] == '$' && !protectedMask[index]
        && (index == 0 || chars[index - 1] != '$')
        && (index + 1 >= chars.Length || chars[index + 1] != '$');

    private static bool Matches(char[] chars, int index, string token, bool[] protectedMask)
    {
        if (index < 0 || index + token.Length > chars.Length) return false;
        for (var k = 0; k < token.Length; k++)
        {
            if (protectedMask[index + k] || chars[index + k] != token[k]) return false;
        }
        return true;
    }

    private static bool IsEscaped(char[] chars, int index)
    {
        var count = 0;
        var i = index - 1;
        while (i >= 0 && chars[i] == '\\')
        {
            count++;
            i--;
        }
        return count % 2 == 1;
    }

    private static bool LooksLikeAssignment(string value)
    {
        var eq = value.IndexOf('=');
        if (eq < 0) return false;
        var lhs = value[..eq].Trim();
        var rhs = value[(eq + 1)..].Trim();
        if (!SingleSymbolRegex().IsMatch(lhs) || rhs.Length == 0) return false;
        foreach (var c in rhs)
        {
            if (!(char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || AssignmentSymbols.Contains(c)))
                return false;
        }
        var stack = new Stack<char>();
        foreach (var c in rhs)
        {
            switch (c)
            {
                case '(':
                case '[':
                case '{':
                    stack.Push(c);
                    break;
                case ')':
                    if (stack.Count == 0 || stack.Pop() != '(') return false;
                    break;
                case ']':
                    if (stack.Count == 0 || stack.Pop() != '[') return false;
                    break;
                case '}':
                    if (stack.Count == 0 || stack.Pop() != '{') return false;
                    break;
            }
        }
        return stack.Count == 0 && rhs.Any(c => char.IsDigit(c) || "\\_^,+-*/<>|".Contains(c));
    }

    private const string AssignmentSymbols = "\\_^,+-*/.=<>|[](){}";

    internal static bool[] ProtectedMask(char[] chars)
    {
        var mask = new bool[chars.Length];
        var lineStart = 0;
        var fenceStart = -1;
        var fenceChar = ' ';
        var fenceRun = 0;
        var previousBlank = true;
        var flatContext = true;
        var inIndentedCode = false;
        while (lineStart < chars.Length)
        {
            var lineEnd = lineStart;
            while (lineEnd < chars.Length && chars[lineEnd] != '\n') lineEnd++;
            var next = lineEnd < chars.Length ? lineEnd + 1 : lineEnd;
            var c = lineStart;
            var indent = 0;
            while (c < lineEnd && char.IsWhiteSpace(chars[c]))
            {
                indent += chars[c] == '\t' ? 4 - indent % 4 : 1;
                c++;
            }
            var blank = c == lineEnd;
            if (fenceStart < 0 && !blank && indent >= 4 && (inIndentedCode || (previousBlank && flatContext)))
            {
                inIndentedCode = true;
                for (var k = lineStart; k < next; k++) mask[k] = true;
                lineStart = next;
                previousBlank = false;
                continue;
            }
            if (!blank) inIndentedCode = false;
            if (c < lineEnd && (chars[c] == '`' || chars[c] == '~'))
            {
                var candidate = chars[c];
                var run = 0;
                while (c + run < lineEnd && chars[c + run] == candidate) run++;
                if (run >= 3)
                {
                    var bare = true;
                    for (var t = c + run; t < lineEnd; t++)
                    {
                        if (!char.IsWhiteSpace(chars[t])) { bare = false; break; }
                    }
                    if (fenceStart >= 0 && candidate == fenceChar && run >= fenceRun && bare)
                    {
                        for (var k = fenceStart; k < next; k++) mask[k] = true;
                        fenceStart = -1;
                    }
                    else if (fenceStart < 0)
                    {
                        fenceStart = lineStart;
                        fenceChar = candidate;
                        fenceRun = run;
                    }
                }
            }
            if (!blank && fenceStart < 0)
            {
                var lineContent = new string(chars, c, lineEnd - c);
                flatContext = indent == 0 && !ListMarkerRegex().IsMatch(lineContent);
            }
            previousBlank = blank;
            lineStart = next;
        }
        if (fenceStart >= 0)
            for (var k = fenceStart; k < chars.Length; k++) mask[k] = true;

        var i = 0;
        while (i < chars.Length)
        {
            if (chars[i] != '`' || mask[i])
            {
                i++;
                continue;
            }
            var run = RunLength(chars, i);
            var j = i + run;
            var close = -1;
            while (j < chars.Length)
            {
                if (chars[j] == '\n' && EndsParagraph(chars, j + 1)) break;
                if (chars[j] == '`' && !mask[j])
                {
                    var other = RunLength(chars, j);
                    if (other == run)
                    {
                        close = j;
                        break;
                    }
                    j += other;
                }
                else j++;
            }
            if (close >= 0)
            {
                for (var k = i; k < close + run; k++) mask[k] = true;
                i = close + run;
            }
            else i += run;
        }
        return mask;
    }

    private static bool EndsParagraph(char[] chars, int index)
    {
        var i = index;
        while (i < chars.Length && chars[i] != '\n')
        {
            if (!char.IsWhiteSpace(chars[i])) return false;
            i++;
        }
        return true;
    }

    private static int RunLength(char[] chars, int index)
    {
        var n = 0;
        while (index + n < chars.Length && chars[index + n] == '`') n++;
        return n;
    }

    [GeneratedRegex(@"^[A-Za-z](?:_[A-Za-z0-9]+|\^[A-Za-z0-9]+)?$")]
    private static partial Regex SingleSymbolRegex();

    [GeneratedRegex(@"^\(\s*[+-]?\d+(?:\.\d+)?(?:\s*,\s*[+-]?\d+(?:\.\d+)?)+\s*\)$")]
    private static partial Regex NumericTupleRegex();

    [GeneratedRegex(@"[A-Za-z0-9]\s*[=<>+\-*/|]\s*[A-Za-z0-9]")]
    private static partial Regex OperatorBetweenRegex();

    [GeneratedRegex(@"^(?:[-*+]|\d{1,9}[.)])(?:\s|$)")]
    private static partial Regex ListMarkerRegex();
}
