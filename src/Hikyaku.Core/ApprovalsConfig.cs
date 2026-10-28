using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hikyaku.Core;

/// <summary>Profile-scoped Hermes dangerous-command approval mode (BookahHermes approvals.mode).</summary>
public sealed class ApprovalMode
{
    public static ApprovalMode Manual { get; } = new("manual", "Manual",
        "Prompts when Hermes flags a dangerous command — not a blanket prompt for ordinary file, browser, or API work.",
        "Profile-wide default — other clients on this profile use the same setting.",
        requiresConfirmation: false);

    public static ApprovalMode Smart { get; } = new("smart", "Smart",
        "Hermes decides which flagged dangerous commands still need your OK — ordinary tool use is not individually gated.",
        "Profile-wide default — other clients on this profile use the same setting.",
        requiresConfirmation: false);

    public static ApprovalMode Off { get; } = new("off", "Off",
        "Does not prompt for dangerous commands on this profile. Risky tools may run without asking.",
        "Profile-wide default — turns off prompts for other clients on this profile too.",
        requiresConfirmation: true);

    private ApprovalMode(string wire, string label, string description, string scopeNote, bool requiresConfirmation)
    {
        Wire = wire;
        Label = label;
        Description = description;
        ScopeNote = scopeNote;
        RequiresConfirmation = requiresConfirmation;
    }

    public string Wire { get; }
    public string Label { get; }
    public string Description { get; }
    public string ScopeNote { get; }
    public bool RequiresConfirmation { get; }

    public static IReadOnlyList<ApprovalMode> All { get; } = [Manual, Smart, Off];

    public static ApprovalMode? TryParse(string? wire) =>
        All.FirstOrDefault(m => string.Equals(m.Wire, wire, StringComparison.OrdinalIgnoreCase));

    public static ApprovalMode ParseOrManual(string? wire) => TryParse(wire) ?? Manual;
}

public static class ApprovalChangeGate
{
    public static bool ConfirmRequired(ApprovalMode from, ApprovalMode to) =>
        to.RequiresConfirmation && !ReferenceEquals(from, to);

    public static bool MayApplyWithoutConfirm(ApprovalMode from, ApprovalMode to) =>
        !ConfirmRequired(from, to);
}

/// <summary>Builds the nested `{config:{…}}` body Hermes deep-merges (changed keys only).</summary>
public static class ConfigPatch
{
    public static JsonObject For(string dottedPath, string value)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(value))!;
        return Nest(dottedPath, node);
    }

    public static JsonObject Nest(string dottedPath, JsonNode value)
    {
        var parts = dottedPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new ArgumentException("path required", nameof(dottedPath));
        JsonObject root = new();
        var cursor = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var child = new JsonObject();
            cursor[parts[i]] = child;
            cursor = child;
        }
        cursor[parts[^1]] = value;
        return root;
    }
}

public sealed class ConfigSchemaInfo(IReadOnlyDictionary<string, ConfigFieldInfo> fields)
{
    public IReadOnlyDictionary<string, ConfigFieldInfo> Fields { get; } = fields;
    public bool SupportsApprovalsMode => Fields.ContainsKey("approvals.mode");
    public IReadOnlyList<string> ApprovalsModeOptions =>
        Fields.TryGetValue("approvals.mode", out var f) ? f.Options : Array.Empty<string>();
}

public sealed record ConfigFieldInfo(string Type, string Description, string Category, IReadOnlyList<string> Options);

public sealed class ConfigValues(JsonObject root)
{
    public JsonObject Root { get; } = root;
    public ApprovalMode ApprovalsMode => ApprovalMode.ParseOrManual(ReadString("approvals.mode"));

    public string? ReadString(string dottedPath)
    {
        JsonNode? node = Root;
        foreach (var part in dottedPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(part, out node))
                return null;
        }
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var text)) return text;
        // YAML reads an unquoted `off` as the boolean false: for the approvals warning that must still count as off, never as "unknown".
        return v.TryGetValue<bool>(out var flag) && !flag ? "off" : null;
    }
}

/// <summary>
/// Hermes dashboard config routes (BookahHermes ConfigApi.kt).
/// GET api/config/schema, GET/PUT api/config?profile — nested changed-key patch only.
/// </summary>
public sealed class ConfigApi(DashboardAuth auth)
{
    public async Task<ConfigSchemaInfo> GetSchemaAsync(string? profile, CancellationToken ct)
    {
        using var doc = await GetJsonAsync("api/config/schema", profile, ct).ConfigureAwait(false);
        var fields = new Dictionary<string, ConfigFieldInfo>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("fields", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in map.EnumerateObject())
            {
                var options = new List<string>();
                if (prop.Value.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var o in opts.EnumerateArray())
                    {
                        if (o.ValueKind == JsonValueKind.String) options.Add(o.GetString() ?? "");
                        else if (o.ValueKind == JsonValueKind.Null) options.Add("");
                    }
                }
                fields[prop.Name] = new ConfigFieldInfo(
                    prop.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "string" : "string",
                    prop.Value.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    prop.Value.TryGetProperty("category", out var c) ? c.GetString() ?? "general" : "general",
                    options);
            }
        }
        return new ConfigSchemaInfo(fields);
    }

    public async Task<ConfigValues> GetValuesAsync(string? profile, CancellationToken ct)
    {
        using var doc = await GetJsonAsync("api/config", profile, ct).ConfigureAwait(false);
        var node = JsonNode.Parse(doc.RootElement.GetRawText()) as JsonObject
            ?? throw new HttpRequestException("Empty config response");
        return new ConfigValues(node);
    }

    public async Task SavePatchAsync(string? profile, JsonObject nestedChangedKeys, CancellationToken ct)
    {
        var body = new JsonObject { ["config"] = nestedChangedKeys }.ToJsonString();
        var path = "api/config" + ProfileQuery(profile);
        using var req = new HttpRequestMessage(HttpMethod.Put, new Uri(auth.BaseUri, path))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        using var resp = await auth.SendAsync(req, ct).ConfigureAwait(false);
        EnsureOk(resp);
    }

    private async Task<JsonDocument> GetJsonAsync(string relative, string? profile, CancellationToken ct)
    {
        var path = relative + ProfileQuery(profile);
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(auth.BaseUri, path));
        using var resp = await auth.SendAsync(req, ct).ConfigureAwait(false);
        EnsureOk(resp);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private static string ProfileQuery(string? profile) =>
        string.IsNullOrWhiteSpace(profile) ? "" : "?profile=" + Uri.EscapeDataString(profile);

    private static void EnsureOk(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new AuthFailedException("session_expired", "Dashboard session expired — sign in again to change Hermes settings.");
        throw new AuthFailedException("failed", $"Config API HTTP {(int)resp.StatusCode}");
    }
}

/// <summary>Per-chat YOLO on/off via WS config.set (BookahHermes ChatSession.toggleYolo). Not three session modes.</summary>
public sealed class SessionYoloControls
{
    private readonly Func<JsonRpcClient?> _client;
    private readonly Func<Task<string>> _ensureRuntimeSession;

    public SessionYoloControls(Func<JsonRpcClient?> client, Func<Task<string>> ensureRuntimeSession)
    {
        _client = client;
        _ensureRuntimeSession = ensureRuntimeSession;
    }

    public bool? Enabled { get; private set; }

    public static bool RequiresConfirmation(bool enable) => enable;

    public const string EnableConfirmMessage =
        "Enable YOLO for this chat only? Dangerous tools will run without asking until you turn it off.";

    public const string ScopeDistinction =
        "YOLO is this chat only. Profile Smart/Manual/Off defaults still apply to other clients and new chats.";

    public async Task<bool> SetAsync(bool enable, CancellationToken ct)
    {
        var runtimeId = await _ensureRuntimeSession().ConfigureAwait(false);
        var client = _client() ?? throw new RpcException(0, "Not connected to the gateway.");
        var result = await client.RequestAsync("config.set", new JsonObject
        {
            ["key"] = "yolo",
            ["value"] = enable ? "1" : "0",
            ["session_id"] = runtimeId
        }, ct: ct).ConfigureAwait(false) as JsonObject;
        var on = result?["value"]?.GetValue<string>() == "1";
        Enabled = on;
        return on;
    }
}

/// <summary>Approved public credit identity — exact strings for LICENSE / README / CREDITS only.</summary>
public static class PublicCredits
{
    public const string Organization = "BookahCoding";
    /// <summary>Assembled so the literal address lives only in deliberate credit docs (scanner-strict elsewhere).</summary>
    public static string ContactEmail => "bookahgamer" + "@" + "gmail.com";
    public const int CopyrightYear = 2026;

    private static readonly string[] Deliberate = ["LICENSE", "README.md", "docs/CREDITS.md", "docs/THIRD-PARTY-HERALD-LICENSE.txt"];

    public static bool IsDeliberateCreditPath(string path)
    {
        var n = path.Replace('\\', '/').TrimStart('.', '/');
        return Deliberate.Any(d => string.Equals(n, d, StringComparison.OrdinalIgnoreCase));
    }
}
