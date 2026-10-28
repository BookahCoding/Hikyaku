using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hikyaku.Core;

/// <summary>A skill the profile's agent can load (<c>GET /api/skills</c>, disabled ones included).</summary>
public sealed record SkillInfo(string Name, string Description, string? Category, bool Enabled, string Provenance, int Usage);

/// <summary>A toolset the agent can be given (<c>GET /api/tools/toolsets</c>). Not configured = needs a key or setup on the gateway.</summary>
public sealed record ToolsetInfo(string Name, string Label, string Description, bool Enabled, bool Configured, IReadOnlyList<string> Tools);

/// <summary>A configured MCP server, secrets left out (<c>GET /api/mcp/servers</c>). A plugin-provided one cannot be switched here.</summary>
public sealed record McpServerInfo(string Name, string Transport, string? Url, string? Command, IReadOnlyList<string> Args, bool Enabled, string? Plugin)
{
    public bool Locked => !string.IsNullOrWhiteSpace(Plugin);
    public string Target => Url ?? string.Join(" ", new[] { Command, string.Join(" ", Args) }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

public sealed record McpTestResult(bool Ok, string? Error, IReadOnlyList<(string Name, string Description)> Tools);

/// <summary>
/// The profile's skills, toolsets and MCP servers through the dashboard routes Hermes Desktop's settings use (wire contract:
/// BookahHermes <c>CapabilitiesApi.kt</c> @ 5f00986b). A switch is saved to the profile's config and applies from the next chat.
/// Every call names the profile explicitly; callers gate with the profile selection first.
/// </summary>
public sealed class CapabilitiesApi(DashboardAuth auth)
{
    public async Task<IReadOnlyList<SkillInfo>> SkillsAsync(string profile, CancellationToken ct)
    {
        using var doc = await DashboardRest.SendAsync(auth, HttpMethod.Get, "api/skills", profile, null, true, ct).ConfigureAwait(false);
        var list = new List<SkillInfo>();
        foreach (var e in Items(doc.RootElement, "skills"))
        {
            if (DashboardRest.Str(e, "name") is not { Length: > 0 } name) continue;
            list.Add(new SkillInfo(name, DashboardRest.Str(e, "description") ?? "", DashboardRest.Str(e, "category"),
                DashboardRest.Bool(e, "enabled") ?? true, DashboardRest.Str(e, "provenance") ?? "agent", DashboardRest.Int(e, "usage")));
        }
        return list;
    }

    public async Task SetSkillEnabledAsync(string profile, string name, bool enabled, CancellationToken ct)
    {
        using var _ = await DashboardRest.SendAsync(auth, HttpMethod.Put, "api/skills/toggle", profile,
            new JsonObject { ["name"] = name, ["enabled"] = enabled }, false, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ToolsetInfo>> ToolsetsAsync(string profile, CancellationToken ct)
    {
        using var doc = await DashboardRest.SendAsync(auth, HttpMethod.Get, "api/tools/toolsets", profile, null, true, ct).ConfigureAwait(false);
        var list = new List<ToolsetInfo>();
        foreach (var e in Items(doc.RootElement, "toolsets"))
        {
            if (DashboardRest.Str(e, "name") is not { Length: > 0 } name) continue;
            list.Add(new ToolsetInfo(name, DashboardRest.Str(e, "label") ?? name, DashboardRest.Str(e, "description") ?? "",
                DashboardRest.Bool(e, "enabled") ?? false, DashboardRest.Bool(e, "configured") ?? true, DashboardRest.Strings(e, "tools")));
        }
        return list;
    }

    public async Task SetToolsetEnabledAsync(string profile, string name, bool enabled, CancellationToken ct)
    {
        using var _ = await DashboardRest.SendAsync(auth, HttpMethod.Put, "api/tools/toolsets/" + Uri.EscapeDataString(name), profile,
            new JsonObject { ["enabled"] = enabled }, false, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpServerInfo>> McpServersAsync(string profile, CancellationToken ct)
    {
        using var doc = await DashboardRest.SendAsync(auth, HttpMethod.Get, "api/mcp/servers", profile, null, true, ct).ConfigureAwait(false);
        var list = new List<McpServerInfo>();
        foreach (var e in Items(doc.RootElement, "servers"))
        {
            if (DashboardRest.Str(e, "name") is not { Length: > 0 } name) continue;
            list.Add(new McpServerInfo(name, DashboardRest.Str(e, "transport") ?? "unknown", DashboardRest.Str(e, "url"),
                DashboardRest.Str(e, "command"), DashboardRest.Strings(e, "args"), DashboardRest.Bool(e, "enabled") ?? true,
                DashboardRest.Str(e, "plugin")));
        }
        return list;
    }

    public async Task SetMcpServerEnabledAsync(string profile, string name, bool enabled, CancellationToken ct)
    {
        using var _ = await DashboardRest.SendAsync(auth, HttpMethod.Put, "api/mcp/servers/" + Uri.EscapeDataString(name) + "/enabled", profile,
            new JsonObject { ["enabled"] = enabled }, false, ct).ConfigureAwait(false);
    }

    /// <summary>Connects to the server, lists its tools, disconnects. A failed connection is still a reply, with Ok = false.</summary>
    public async Task<McpTestResult> TestMcpServerAsync(string profile, string name, CancellationToken ct)
    {
        using var doc = await DashboardRest.SendAsync(auth, HttpMethod.Post, "api/mcp/servers/" + Uri.EscapeDataString(name) + "/test", profile,
            null, false, ct).ConfigureAwait(false);
        var r = doc.RootElement;
        var tools = new List<(string, string)>();
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("tools", out var t) && t.ValueKind == JsonValueKind.Array)
            foreach (var e in t.EnumerateArray())
                if (DashboardRest.Str(e, "name") is { Length: > 0 } n) tools.Add((n, DashboardRest.Str(e, "description") ?? ""));
        return new McpTestResult(DashboardRest.Bool(r, "ok") ?? false, DashboardRest.Str(r, "error"), tools);
    }

    private static IEnumerable<JsonElement> Items(JsonElement root, string key)
    {
        var arr = DashboardRest.ArrayOf(root, key);
        return arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray() : [];
    }
}
