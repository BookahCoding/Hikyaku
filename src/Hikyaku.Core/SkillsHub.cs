using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

/// <summary>A skill the hub offers (featured, searched or from the official catalog). BookahHermes <c>SkillsHub.kt</c> @ 5f00986b.</summary>
public sealed record HubSkill(
    string Name, string Description, string Source, string Identifier, string TrustLevel,
    string? Repo, IReadOnlyList<string> Tags, string? Category, bool Installed)
{
    public bool IsOfficial => Source == "official" || Identifier.StartsWith("official/", StringComparison.Ordinal);

    /// <summary>What the hub's calls take: the identifier, or the name when the hub gave none.</summary>
    public string Key => string.IsNullOrWhiteSpace(Identifier) ? Name : Identifier;

    public string TrustLabel => string.IsNullOrWhiteSpace(TrustLevel) ? "" : TrustLevel switch
    {
        "builtin" => "built in",
        "trusted" => "trusted source",
        "community" => "community",
        var other => other
    };
}

/// <summary>What the profile has installed from the hub, keyed by identifier.</summary>
public sealed record HubInstalled(string? Name, string? TrustLevel, string? ScanVerdict);

public sealed record HubSource(string Id, string Label, bool RateLimited, bool Available);

public sealed record HubSources(
    IReadOnlyList<HubSource> Sources, IReadOnlyList<HubSkill> Featured, IReadOnlyDictionary<string, HubInstalled> Installed);

public sealed record HubSearch(IReadOnlyList<HubSkill> Results, IReadOnlyList<string> TimedOut, IReadOnlyDictionary<string, HubInstalled> Installed);

/// <summary>A skill's SKILL.md and file list, shown before anything is installed.</summary>
public sealed record HubPreview(
    string Name, string Description, string Source, string Identifier, string TrustLevel,
    string? Repo, IReadOnlyList<string> Tags, string SkillMd, IReadOnlyList<string> Files)
{
    /// <summary>The SKILL.md without its YAML front matter (name, description and tags are shown separately).</summary>
    public string Body
    {
        get
        {
            var lines = SkillMd.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != "---") return SkillMd;
            var end = -1;
            for (var i = 1; i < lines.Length; i++)
                if (lines[i].Trim() is "---" or "...") { end = i; break; }
            return end < 0 ? SkillMd : string.Join("\n", lines.Skip(end + 1)).TrimStart('\n');
        }
    }
}

/// <summary>How a background hub action (install, uninstall, update) ended; <see cref="Lines"/> is what to show for it.</summary>
public abstract record HubActionResult(IReadOnlyList<string> Lines)
{
    public sealed record Done(IReadOnlyList<string> Lines) : HubActionResult(Lines);

    /// <summary>The security scan refused the install; <see cref="Findings"/> is how many it counted, when the log says.</summary>
    public sealed record Blocked(int? Findings, IReadOnlyList<string> Lines) : HubActionResult(Lines);

    public sealed record Failed(IReadOnlyList<string> Lines) : HubActionResult(Lines);

    /// <summary>Hikyaku stopped waiting; the gateway may still finish it.</summary>
    public sealed record StillRunning(IReadOnlyList<string> Lines) : HubActionResult(Lines);

    private static readonly Regex BlockedNow = new(@"Not installed:\s+the security scan found\s+(?:(\d+)\s+)?high-risk\s+pattern", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BlockedLegacy = new(@"Installation blocked:.*?\(([a-z_-]+) source \+ ([a-z_]+) verdict, (\d+) findings?\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Blocked when the action's log says the scan refused it, else null.</summary>
    internal static Blocked? ScanBlock(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (BlockedNow.Match(line) is { Success: true } a) return new Blocked(int.TryParse(a.Groups[1].Value, out var n) ? n : null, lines);
            if (BlockedLegacy.Match(line) is { Success: true } b) return new Blocked(int.TryParse(b.Groups[3].Value, out var m) ? m : null, lines);
        }
        return null;
    }
}

public sealed record HubFinding(string Severity, string Category, string File, int Line, string Description);

/// <summary>The security scan of a skill; <see cref="Policy"/> says whether the gateway would install it, ask, or refuse.</summary>
public sealed record HubScan(
    string Name, string Identifier, string Source, string TrustLevel, string Verdict, string Summary,
    string Policy, string PolicyReason, IReadOnlyList<HubFinding> Findings);

/// <summary>
/// The skills hub through the dashboard routes: the reads (sources + featured, official catalog, search, preview, safety scan) and, since
/// the maintainer approved them (2026-10-27), the writes (install, uninstall, update all), which put third-party code on the gateway. This class
/// only makes the calls: <b>the caller must have the person's explicit yes first</b> (the view model asks, in words, after reading the
/// safety check, and never overrides a "block" policy). The gateway writes null for what it does not have; readers treat it as a default.
/// </summary>
public sealed class SkillsHubApi(DashboardAuth auth, TimeSpan? pollInterval = null, TimeSpan? actionTimeout = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>How long an action is polled before Hikyaku stops waiting; the gateway keeps going.</summary>
    public static readonly TimeSpan DefaultActionTimeout = TimeSpan.FromMinutes(10);
    private const int StatusRetries = 2;
    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromSeconds(1);
    private readonly TimeSpan _timeout = actionTimeout ?? DefaultActionTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>Search waits up to 30 s on slow sources, longer than a plain read.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);
    public const int MaxResults = 100;
    public const int MaxSkillMdChars = 200_000;
    public const int MaxQueryChars = 200;

    public async Task<HubSources> SourcesAsync(string profile, CancellationToken ct)
    {
        using var doc = await GetAsync("api/skills/hub/sources", profile, ct).ConfigureAwait(false);
        var r = doc.RootElement;
        return new HubSources(Items(r, "sources", ParseSource), Items(r, "featured", ParseSkill), ParseInstalled(r));
    }

    public async Task<IReadOnlyList<HubSkill>> OfficialAsync(string profile, CancellationToken ct)
    {
        using var doc = await GetAsync("api/skills/hub/official", profile, ct).ConfigureAwait(false);
        return Items(doc.RootElement, "skills", ParseSkill);
    }

    public async Task<HubSearch> SearchAsync(string profile, string query, int limit, CancellationToken ct)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return new HubSearch([], [], new Dictionary<string, HubInstalled>());
        if (q.Length > MaxQueryChars) q = q[..MaxQueryChars];
        using var doc = await GetAsync("api/skills/hub/search?q=" + Uri.EscapeDataString(q) + "&source=all&limit=" + Math.Clamp(limit, 1, MaxResults),
            profile, ct).ConfigureAwait(false);
        var r = doc.RootElement;
        return new HubSearch(Items(r, "results", ParseSkill), DashboardRest.Strings(r, "timed_out"), ParseInstalled(r));
    }

    public async Task<HubPreview> PreviewAsync(string profile, string identifier, CancellationToken ct)
    {
        using var doc = await GetAsync("api/skills/hub/preview?identifier=" + Uri.EscapeDataString(RequireIdentifier(identifier)), profile, ct).ConfigureAwait(false);
        var r = doc.RootElement;
        var md = DashboardRest.Str(r, "skill_md") ?? "";
        if (md.Length > MaxSkillMdChars) md = md[..MaxSkillMdChars] + "\n… (cut off)";
        return new HubPreview(S(r, "name"), S(r, "description"), S(r, "source"), S(r, "identifier"), S(r, "trust_level"),
            DashboardRest.Str(r, "repo"), DashboardRest.Strings(r, "tags"), md, DashboardRest.Strings(r, "files"));
    }

    public async Task<HubScan> ScanAsync(string profile, string identifier, CancellationToken ct)
    {
        using var doc = await GetAsync("api/skills/hub/scan?identifier=" + Uri.EscapeDataString(RequireIdentifier(identifier)), profile, ct).ConfigureAwait(false);
        var r = doc.RootElement;
        var findings = new List<HubFinding>();
        var found = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("findings", out var fa) ? fa : default;
        foreach (var f in found.EnumerateArrayOrEmpty())
        {
            if (f.ValueKind != JsonValueKind.Object) continue;
            findings.Add(new HubFinding(S(f, "severity"), S(f, "category"), S(f, "file"), DashboardRest.Int(f, "line"), S(f, "description")));
            if (findings.Count >= 200) break;
        }
        return new HubScan(S(r, "name"), S(r, "identifier"), S(r, "source"), S(r, "trust_level"), S(r, "verdict"), S(r, "summary"),
            S(r, "policy"), S(r, "policy_reason"), findings);
    }

    // ---- writes (confirmed by the caller) ------------------------------------------------------------

    public Task<HubActionResult> InstallAsync(string profile, string identifier, CancellationToken ct) =>
        RunActionAsync("api/skills/hub/install", "install", new JsonObject { ["identifier"] = RequireIdentifier(identifier) }, profile, ct);

    public Task<HubActionResult> UninstallAsync(string profile, string name, CancellationToken ct) =>
        RunActionAsync("api/skills/hub/uninstall", "removal", new JsonObject { ["name"] = RequireIdentifier(name) }, profile, ct);

    public Task<HubActionResult> UpdateAllAsync(string profile, CancellationToken ct) =>
        RunActionAsync("api/skills/hub/update", "update", new JsonObject(), profile, ct);

    /// <summary>Starts a background action on the gateway, then polls its status until the process ends; <paramref name="noun"/> names it in a lost-track message.</summary>
    private async Task<HubActionResult> RunActionAsync(string route, string noun, JsonObject fields, string profile, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(profile)) fields["profile"] = profile;       // the body names the profile, as the gateway's contract has it
        string name;
        using (var started = await DashboardRest.SendAsync(auth, HttpMethod.Post, route, null, fields, false, ct).ConfigureAwait(false))
        {
            var r = started.RootElement;
            if (DashboardRest.Bool(r, "ok") == false)
                return new HubActionResult.Failed(DashboardRest.Str(r, "error") is { Length: > 0 } e ? [e] : []);
            name = S(r, "name");
            if (name.Length == 0) return new HubActionResult.Failed(["The gateway did not name the action."]);
        }

        var outages = 0;
        var waited = TimeSpan.Zero;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_poll > TimeSpan.Zero && waited >= _timeout)
                return new HubActionResult.StillRunning([$"The {noun} is still running on the gateway. Check back later."]);
            JsonDocument doc;
            try
            {
                doc = await DashboardRest.SendAsync(auth, HttpMethod.Get,
                    "api/actions/" + Uri.EscapeDataString(name) + "/status?lines=200", profile, null, false, ct).ConfigureAwait(false);
                outages = 0;
            }
            catch (HttpRequestException) when (++outages <= StatusRetries)
            {
                await _delay(_poll, ct).ConfigureAwait(false);
                waited += _poll;
                continue;
            }
            catch (RestException ex) when (ex.Failure == RestFailure.NotFound)
            {
                return new HubActionResult.Failed([$"Lost track of the {noun} on the gateway."]);
            }
            using (doc)
            {
                var r = doc.RootElement;
                if (DashboardRest.Bool(r, "running") != true)
                {
                    var lines = DashboardRest.Strings(r, "lines");
                    if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("exit_code", out var ec)
                        && ec.ValueKind == JsonValueKind.Number && ec.TryGetInt32(out var code) && code == 0)
                        return new HubActionResult.Done(lines);
                    return (HubActionResult?)HubActionResult.ScanBlock(lines) ?? new HubActionResult.Failed(lines.TakeLast(3).ToList());
                }
            }
            await _delay(_poll, ct).ConfigureAwait(false);
            waited += _poll;
        }
    }

    private static string RequireIdentifier(string id) =>
        string.IsNullOrWhiteSpace(id) ? throw new RestException(RestFailure.Invalid, "That skill has no identifier.") : id.Trim();

    private async Task<JsonDocument> GetAsync(string relative, string profile, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        return await DashboardRest.SendAsync(auth, HttpMethod.Get, relative, profile, null, true, cts.Token).ConfigureAwait(false);
    }

    // ---- tolerant parsing ---------------------------------------------------------------------------

    private static string S(JsonElement e, string name) => DashboardRest.Str(e, name) ?? "";

    internal static HubSkill? ParseSkill(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var name = S(e, "name");
        var id = S(e, "identifier");
        if (name.Length == 0 && id.Length == 0) return null;
        return new HubSkill(name.Length > 0 ? name : id, S(e, "description"), S(e, "source"), id, S(e, "trust_level"),
            DashboardRest.Str(e, "repo"), DashboardRest.Strings(e, "tags"), DashboardRest.Str(e, "category"), DashboardRest.Bool(e, "installed") ?? false);
    }

    private static HubSource? ParseSource(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var id = S(e, "id");
        if (id.Length == 0) return null;
        return new HubSource(id, DashboardRest.Str(e, "label") is { Length: > 0 } l ? l : id,
            DashboardRest.Bool(e, "rate_limited") ?? false, DashboardRest.Bool(e, "available") ?? true);
    }

    private static IReadOnlyDictionary<string, HubInstalled> ParseInstalled(JsonElement r)
    {
        var map = new Dictionary<string, HubInstalled>(StringComparer.Ordinal);
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("installed", out var inst) && inst.ValueKind == JsonValueKind.Object)
            foreach (var p in inst.EnumerateObject())
                map[p.Name] = new HubInstalled(DashboardRest.Str(p.Value, "name"), DashboardRest.Str(p.Value, "trust_level"), DashboardRest.Str(p.Value, "scan_verdict"));
        return map;
    }

    private static IReadOnlyList<T> Items<T>(JsonElement root, string key, Func<JsonElement, T?> parse) where T : class
    {
        var list = new List<T>();
        var arr = DashboardRest.ArrayOf(root, key);
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray())
            if (parse(e) is { } item) { list.Add(item); if (list.Count >= MaxResults) break; }
        return list;
    }
}

internal static class JsonElementExtensions
{
    public static IEnumerable<JsonElement> EnumerateArrayOrEmpty(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];
}
