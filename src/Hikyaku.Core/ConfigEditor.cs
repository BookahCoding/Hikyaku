using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

/// <summary>The profile's config.yaml as written, and where it lives on the gateway.</summary>
public sealed record RawConfig(string Yaml, string Path);

/// <summary>
/// Hermes's own settings (config.yaml) through the dashboard routes (BookahHermes <c>config/ConfigApi.kt</c> @ 5f00986b; <see cref="ConfigApi"/> already covers the approvals setting and the schema).
/// A save of values sends only what changed and the gateway deep-merges it onto the file; a raw save replaces the whole file (the gateway
/// refuses text that is not a YAML mapping). Advanced view only, and every write is confirmed by the caller first: a wrong setting can
/// change what the agent can reach or stop the gateway working for everyone on that profile.
/// </summary>
public sealed class GatewayConfigEditor(DashboardAuth auth)
{
    public const int MaxYamlChars = 1_000_000;

    public async Task<RawConfig> RawAsync(string profile, CancellationToken ct)
    {
        using var doc = await DashboardRest.SendAsync(auth, HttpMethod.Get, "api/config/raw", profile, null, false, ct).ConfigureAwait(false);
        var yaml = DashboardRest.Str(doc.RootElement, "yaml") ?? "";
        if (yaml.Length > MaxYamlChars) throw new RestException(RestFailure.Failed, "The configuration file is too large to edit here.");
        return new RawConfig(yaml, DashboardRest.Str(doc.RootElement, "path") ?? "");
    }

    /// <summary>The settings with Hermes's defaults filled in (read-only use).</summary>
    public async Task<JsonObject> ValuesAsync(string profile, CancellationToken ct)
    {
        using var doc = await DashboardRest.SendAsync(auth, HttpMethod.Get, "api/config", profile, null, false, ct).ConfigureAwait(false);
        return JsonNode.Parse(doc.RootElement.GetRawText()) as JsonObject ?? new JsonObject();
    }

    /// <summary>Merges <paramref name="patch"/> (a nested object holding only the changed keys) into the profile's config. Cannot delete a key.</summary>
    public async Task SavePatchAsync(string profile, JsonObject patch, CancellationToken ct)
    {
        if (patch.Count == 0) throw new RestException(RestFailure.Invalid, "There is nothing to save.");
        using var _ = await DashboardRest.SendAsync(auth, HttpMethod.Put, "api/config", profile,
            new JsonObject { ["config"] = patch.DeepClone() }, false, ct).ConfigureAwait(false);
    }

    /// <summary>Replaces the whole file with <paramref name="yaml"/>.</summary>
    public async Task SaveRawAsync(string profile, string yaml, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(yaml)) throw new RestException(RestFailure.Invalid, "The configuration cannot be empty.");
        if (yaml.Length > MaxYamlChars) throw new RestException(RestFailure.Invalid, "The configuration is too large.");
        using var _ = await DashboardRest.SendAsync(auth, HttpMethod.Put, "api/config/raw", profile,
            new JsonObject { ["yaml_text"] = yaml }, false, ct).ConfigureAwait(false);
    }
}

public enum McpTransport { Command, Url }

/// <summary>
/// A server to add to, or change in, <c>mcp_servers</c> (Hermes MCP guide: a name, then <c>command</c> / <c>args</c> / <c>env</c> or <c>url</c> /
/// <c>headers</c>, plus <c>enabled</c>, <c>timeout</c>, <c>connect_timeout</c>). There is no REST route for this; it is written as a patch to
/// config.yaml. Validation is strict because the values go into a file that decides what runs on the gateway.
/// </summary>
public sealed record McpServerSpec(
    string Name, McpTransport Transport, string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env,
    string Url, IReadOnlyDictionary<string, string> Headers, bool Enabled, int? TimeoutSeconds, int? ConnectTimeoutSeconds)
{
    private static readonly Regex NamePattern = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex KeyPattern = new("^[A-Za-z_][A-Za-z0-9_.-]{0,127}$", RegexOptions.Compiled);
    private static readonly Regex SecretishKey = new("token|key|secret|password|passwd|auth|bearer|credential", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VarReference = new(@"^\$\{[A-Za-z_][A-Za-z0-9_]*\}$", RegexOptions.Compiled);
    public const int MaxArgs = 64;
    public const int MaxPairs = 64;
    public const int MaxValueChars = 4_000;

    /// <summary>Everything wrong with this spec, in words (empty when it can be saved).</summary>
    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (!NamePattern.IsMatch(Name ?? "")) p.Add("The name must be 1 to 64 letters, digits, dashes or underscores, starting with a letter or digit.");
        if (Transport == McpTransport.Command)
        {
            if (string.IsNullOrWhiteSpace(Command)) p.Add("A local server needs a command.");
            else if (Command.Any(char.IsControl)) p.Add("The command cannot contain line breaks or control characters.");
            else if (Command.Length > MaxValueChars) p.Add("The command is too long.");
            if (Args.Count > MaxArgs) p.Add($"At most {MaxArgs} arguments.");
            if (Args.Any(a => a.Any(c => c is '\n' or '\r' or '\0') || a.Length > MaxValueChars)) p.Add("An argument has a line break or is too long.");
            CheckPairs(Env, "environment variable", p);
        }
        else
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) p.Add("A remote server needs an http(s) address.");
            else if (u.Scheme == "http" && !u.IsLoopback) p.Add("A remote server must use https (plain http is only accepted for this computer).");
            else if (!string.IsNullOrEmpty(u.UserInfo)) p.Add("Do not put a user name or password in the address; use a header with a ${VARIABLE}.");
            CheckPairs(Headers, "header", p);
        }
        if (TimeoutSeconds is < 1 or > 3600) p.Add("The timeout must be between 1 and 3600 seconds.");
        if (ConnectTimeoutSeconds is < 1 or > 3600) p.Add("The connection timeout must be between 1 and 3600 seconds.");
        return p;
    }

    private static void CheckPairs(IReadOnlyDictionary<string, string> pairs, string what, List<string> problems)
    {
        if (pairs.Count > MaxPairs) problems.Add($"At most {MaxPairs} {what}s.");
        foreach (var (k, v) in pairs)
        {
            if (!KeyPattern.IsMatch(k)) problems.Add($"“{k}” is not a valid {what} name.");
            if (v.Any(c => c is '\n' or '\r' or '\0') || v.Length > MaxValueChars) problems.Add($"The value of {what} “{k}” has a line break or is too long.");
        }
    }

    /// <summary>Env / header names that look like secrets but hold a literal value instead of a <c>${VARIABLE}</c> reference: they would sit in config.yaml in plain text.</summary>
    public IReadOnlyList<string> PlaintextSecretKeys()
    {
        var pairs = Transport == McpTransport.Command ? Env : Headers;
        return pairs.Where(kv => SecretishKey.IsMatch(kv.Key) && kv.Value.Length > 0 && !VarReference.IsMatch(kv.Value.Trim()))
            .Select(kv => kv.Key).ToList();
    }

    /// <summary>The patch to merge: only this server's keys, only the fields of its transport.</summary>
    public JsonObject ToPatch()
    {
        var problems = Problems();
        if (problems.Count > 0) throw new InvalidOperationException(problems[0]);
        var server = new JsonObject();
        if (Transport == McpTransport.Command)
        {
            server["command"] = Command.Trim();
            server["args"] = new JsonArray(Args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
            if (Env.Count > 0) server["env"] = ToObject(Env);
        }
        else
        {
            server["url"] = Url.Trim();
            if (Headers.Count > 0) server["headers"] = ToObject(Headers);
        }
        server["enabled"] = Enabled;
        if (TimeoutSeconds is { } t) server["timeout"] = t;
        if (ConnectTimeoutSeconds is { } c) server["connect_timeout"] = c;
        return new JsonObject { ["mcp_servers"] = new JsonObject { [Name] = server } };
    }

    private static JsonObject ToObject(IReadOnlyDictionary<string, string> pairs)
    {
        var o = new JsonObject();
        foreach (var (k, v) in pairs) o[k] = v;
        return o;
    }

    /// <summary>"KEY=value" or "Name: value" lines → pairs; a blank line is skipped, a line with no separator is an error.</summary>
    public static (IReadOnlyDictionary<string, string> Pairs, IReadOnlyList<string> Problems) ParsePairs(string? text, char separator)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var i = line.IndexOf(separator);
            if (i <= 0) { problems.Add($"“{(line.Length > 40 ? line[..40] + "…" : line)}” needs the form NAME{separator}value."); continue; }
            map[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return (map, problems);
    }

    public static IReadOnlyList<string> ParseArgs(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
}
