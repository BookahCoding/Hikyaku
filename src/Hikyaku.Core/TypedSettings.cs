using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

/// <summary>One setting of a profile: where it lives (a dotted path like <c>approvals.mode</c>), how the gateway's schema describes it, and its current value.</summary>
public sealed record SettingEntry(string Path, string Type, string Description, string Category, IReadOnlyList<string> Options, JsonNode? Value)
{
    /// <summary>Secrets (API keys, tokens, passwords) are neither shown nor edited here.</summary>
    public bool IsSecret => TypedSettings.IsSecretPath(Path);
    public bool IsEditable => !IsSecret && Type is "boolean" or "number" or "string" or "select";
    public string DisplayValue => IsSecret ? "(hidden)" : TypedSettings.Display(Value);
}

/// <summary>The result of checking a typed edit: the value to send, or what is wrong with it.</summary>
public sealed record SettingEdit(JsonNode? Value, string? Problem)
{
    public bool Ok => Problem is null;
}

/// <summary>
/// Pure rules for the schema-driven settings editor (Advanced only; BookahHermes <c>config/ConfigApi.kt</c> + <c>ConfigViewModel.kt</c> @ 5f00986b):
/// flatten the schema and the values, check an edit against the field's type, build the nested patch that holds only the changed key,
/// and flag settings whose change deserves an extra warning. Nothing here talks to the gateway.
/// </summary>
public static class TypedSettings
{
    public const int MaxTextChars = 10_000;
    private static readonly Regex SecretPath = new("token|secret|password|passwd|api[_-]?key|credential|private[_-]?key|bearer|auth[_-]?(header|key|token)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RiskyPath = new("approval|yolo|permission|sandbox|security|allow|terminal|trust|unsafe|dangerous", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsSecretPath(string path) => SecretPath.IsMatch(path);

    /// <summary>A warning to show with the confirmation for settings that govern what the agent may do without asking, else null.</summary>
    public static string? RiskWarning(string path) => RiskyPath.IsMatch(path)
        ? "This setting affects what the agent may do or reach, for everyone using this profile."
        : null;

    /// <summary>The schema's fields with their current values, grouped by category (alphabetical, "general" first), then by path.</summary>
    public static IReadOnlyList<SettingEntry> Flatten(ConfigSchemaInfo schema, JsonObject values)
    {
        var list = new List<SettingEntry>();
        foreach (var (path, f) in schema.Fields)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            list.Add(new SettingEntry(path, f.Type, f.Description, string.IsNullOrWhiteSpace(f.Category) ? "general" : f.Category, f.Options, IsSecretPath(path) ? null : ValueAt(values, path)));   // a secret's value is never held
        }
        return list
            .OrderBy(e => e.Category == "general" ? "" : e.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Path, StringComparer.Ordinal)
            .ToList();
    }

    public static JsonNode? ValueAt(JsonObject root, string dottedPath)
    {
        JsonNode? node = root;
        foreach (var part in dottedPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(part, out node)) return null;
        }
        return node;
    }

    public static string Display(JsonNode? v) => v switch
    {
        null => "",
        JsonValue jv when jv.TryGetValue<string>(out var s) => s,
        _ => v.ToJsonString()
    };

    /// <summary>Filters by a search text (path, description or category, case-insensitive; every word must match).</summary>
    public static IEnumerable<SettingEntry> Filter(IEnumerable<SettingEntry> all, string? query)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return all;
        return all.Where(e => words.All(w =>
            e.Path.Contains(w, StringComparison.OrdinalIgnoreCase) || e.Description.Contains(w, StringComparison.OrdinalIgnoreCase)
            || e.Category.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Checks <paramref name="text"/> against the entry's type. A select must be one of the gateway's own options; a number must be a number.</summary>
    public static SettingEdit ParseEdit(SettingEntry entry, string text)
    {
        if (entry.IsSecret) return new(null, "Secrets are not edited here.");
        text ??= "";
        switch (entry.Type)
        {
            case "boolean":
                return text.Trim().ToLowerInvariant() switch
                {
                    "true" => new(JsonValue.Create(true), null),
                    "false" => new(JsonValue.Create(false), null),
                    _ => new(null, "Choose on or off.")
                };
            case "number":
            {
                var t = text.Trim();
                if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return new(JsonValue.Create(l), null);
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)) return new(JsonValue.Create(d), null);
                return new(null, "Enter a number.");
            }
            case "select":
                if (!entry.Options.Contains(text, StringComparer.Ordinal)) return new(null, "Choose one of the listed options.");
                return new(JsonValue.Create(text), null);
            case "string":
                if (text.Length > MaxTextChars) return new(null, "That text is too long.");
                if (text.Any(c => c == '\0')) return new(null, "That text has a character that cannot be saved.");
                return new(JsonValue.Create(text), null);
            default:
                return new(null, "This kind of setting is edited in config.yaml.");
        }
    }

    /// <summary>True when sending <paramref name="edit"/> would change nothing.</summary>
    public static bool Unchanged(SettingEntry entry, JsonNode? edited)
    {
        if (entry.Value is null && edited is null) return true;
        if (entry.Value is null || edited is null) return false;
        if (entry.Value is JsonValue a && edited is JsonValue b)
        {
            if (a.TryGetValue<double>(out var x) && b.TryGetValue<double>(out var y)) return x == y;
            if (a.TryGetValue<bool>(out var p) && b.TryGetValue<bool>(out var q)) return p == q;
            if (a.TryGetValue<string>(out var s) && b.TryGetValue<string>(out var t)) return string.Equals(s, t, StringComparison.Ordinal);
        }
        return entry.Value.ToJsonString() == edited.ToJsonString();
    }

    /// <summary>The nested patch holding just this one key.</summary>
    public static JsonObject PatchFor(string path, JsonNode value) => ConfigPatch.Nest(path, value.DeepClone());
}
