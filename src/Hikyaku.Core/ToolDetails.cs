using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

/// <summary>
/// What a tool call was given and what it gave back, readable on a screen (BookahHermes <c>ToolDetails.kt</c> @ 5f00986b).
/// Pure; tolerant of anything the gateway stores.
/// </summary>
public static class ToolDetails
{
    /// <summary>Huge outputs would stall the list; the agent has the rest.</summary>
    public const int MaxChars = 8_000;

    private static readonly string[] MainText = ["output", "content", "result", "text", "stdout", "message", "error"];
    private static readonly Regex UntrustedFrame = new(
        "^<untrusted_tool_result source=\"[^\"]*\">\n[^\n]*\n\n([\\s\\S]*)\n</untrusted_tool_result>$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>The call's arguments: a lone text argument (a command, a query, a path) as itself, else indented JSON.</summary>
    public static string? Input(JsonElement? args)
    {
        if (args is not { } a) return null;
        switch (a.ValueKind)
        {
            case JsonValueKind.String:
                return FromJsonText(a.GetString(), e => Input(e));
            case JsonValueKind.Object:
                var props = a.EnumerateObject().ToList();
                if (props.Count == 0) return null;
                if (props.Count == 1 && props[0].Value.ValueKind == JsonValueKind.String)
                {
                    var only = props[0].Value.GetString();
                    return only is null ? null : Clip(only);
                }
                return Clip(JsonSerializer.Serialize(a, Pretty));
            default:
                return null;
        }
    }

    /// <summary>The result of a stored tool row (JSON text or plain text): the main text when there is one, else indented JSON.</summary>
    public static string? Output(string? resultText)
    {
        if (string.IsNullOrWhiteSpace(resultText)) return null;
        return FromJsonText(UnwrapUntrusted(resultText), OutputOf);
    }

    private static string? OutputOf(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Null) return null;
        if (result.ValueKind == JsonValueKind.String) return FromJsonText(UnwrapUntrusted(result.GetString() ?? ""), OutputOf);
        if (result.ValueKind != JsonValueKind.Object) return Clip(JsonSerializer.Serialize(result, Pretty));
        foreach (var key in MainText)
        {
            if (!result.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) continue;
            var text = v.GetString()?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            // Keep an error or exit code next to the output they explain.
            if (result.TryGetProperty("exit_code", out var code) && code.ValueKind == JsonValueKind.Number
                && code.TryGetInt32(out var n) && n != 0)
                text += "\n\nExit code " + n;
            return Clip(text);
        }
        return Clip(JsonSerializer.Serialize(result, Pretty));
    }

    /// <summary>A result that reports a failure: an error, <c>success: false</c> or a non-zero exit code.</summary>
    public static bool Failed(string? resultText)
    {
        if (string.IsNullOrWhiteSpace(resultText)) return false;
        var text = UnwrapUntrusted(resultText).Trim();
        if (text.Length == 0 || text[0] != '{') return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return false;
            if (o.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null
                && !(err.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(err.GetString())))
                return true;
            if (o.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False) return true;
            return o.TryGetProperty("exit_code", out var code) && code.ValueKind == JsonValueKind.Number
                && code.TryGetInt32(out var n) && n != 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The gateway fences what web, browser and MCP tools bring back with a note for the model; the result is what is inside.</summary>
    public static string UnwrapUntrusted(string text)
    {
        var m = UntrustedFrame.Match(text.Trim().Replace("\r\n", "\n"));
        return m.Success ? m.Groups[1].Value : text;
    }

    private static string? FromJsonText(string? text, Func<JsonElement, string?> format)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed[0] is '{' or '[')
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return format(doc.RootElement.Clone());
            }
            catch (JsonException)
            {
                // not JSON after all: show it as it is
            }
        }
        return Clip(trimmed);
    }

    private static string Clip(string text)
    {
        if (text.Length <= MaxChars) return text;
        var cut = text[..MaxChars];
        if (char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
        return cut.TrimEnd() + "\n… (" + (text.Length - cut.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) + " more characters)";
    }
}
