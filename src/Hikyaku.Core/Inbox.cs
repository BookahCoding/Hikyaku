using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Hikyaku.Core;

/// <summary>Follow mode for one cron job (BookahHermes Follow).</summary>
public enum FollowMode
{
    Off,
    Quiet,
    Notify
}

public static class FollowModeWire
{
    public static string ToWire(FollowMode m) => m switch
    {
        FollowMode.Quiet => "quiet",
        FollowMode.Notify => "notify",
        _ => "off"
    };

    public static FollowMode FromWire(string? wire) => wire switch
    {
        "quiet" => FollowMode.Quiet,
        "notify" => FollowMode.Notify,
        _ => FollowMode.Off
    };
}

/// <summary>One finished cron run in the Inbox (herald-push cron inbox/read).</summary>
public sealed record InboxRun(
    string Bot,
    string Job,
    string Run,
    string Label = "",
    string Status = "ok",
    long At = 0,
    string Text = "")
{
    public string Key => $"{Bot}/{Job}/{Run}";
    public bool Failed => Status == "failed";
    public string Title => string.IsNullOrWhiteSpace(Label) ? Job : Label.Trim();
}

/// <summary>What this device follows on the gateway.</summary>
public sealed record Following(
    IReadOnlyDictionary<string, FollowMode> Jobs,
    bool Failures = false,
    FollowMode Default = FollowMode.Off)
{
    public static Following Empty { get; } = new(new Dictionary<string, FollowMode>());

    public FollowMode Of(string jobId) => Jobs.TryGetValue(jobId, out var m) ? m : Default;

    public bool Covers(InboxRun run) => Of(run.Job) != FollowMode.Off || (Failures && run.Failed);

    public Following With(string jobId, FollowMode follow)
    {
        var next = new Dictionary<string, FollowMode>(Jobs, StringComparer.Ordinal);
        if (follow == Default) next.Remove(jobId);
        else next[jobId] = follow;
        return this with { Jobs = next };
    }

    public bool EchoedBy(Following saved) =>
        saved.Default == Default && saved.Failures == Failures &&
        Jobs.All(kv => saved.Of(kv.Key) == kv.Value);
}

public enum InboxConnectionState
{
    Disconnected,
    /// <summary>herald-push missing, too old, or device registration required — do not invent runs.</summary>
    SetupRequired,
    Ready,
    Error
}

public sealed class InboxPluginException(string message, string? needsVersion = null) : Exception(message)
{
    public string? NeedsVersion { get; } = needsVersion;
}

/// <summary>
/// Source-backed Inbox over <c>command.dispatch</c> name=<c>herald-push</c> args=<c>cron …</c>
/// (BookahHermes InboxApi / PushApi). Does not install or restart the plugin.
/// </summary>
public sealed class InboxApi(Func<JsonRpcClient> client, string deviceId, Func<string, bool>? profileAllowed = null)
{
    public const string PluginCommand = "herald-push";
    public const string MinPluginVersion = "0.4.0";
    public const string AllJobsPluginVersion = "0.4.1";
    public const int Page = 100;
    public const int NotACommand = 4018;

    private readonly Func<string, bool> _profileAllowed = profileAllowed ?? (_ => true);

    public async Task<Following> FollowingAsync(CancellationToken ct = default)
        => await FollowAsync("following", new JsonObject(), ct).ConfigureAwait(false);

    public async Task<Following> SetFollowingAsync(Following following, CancellationToken ct = default)
    {
        var jobsObj = new JsonObject();
        foreach (var kv in following.Jobs)
            jobsObj[kv.Key] = FollowModeWire.ToWire(kv.Value);
        var body = new JsonObject
        {
            ["jobs"] = jobsObj,
            ["failures"] = following.Failures
        };
        if (following.Default != FollowMode.Off)
            body["default"] = FollowModeWire.ToWire(following.Default);

        Following saved;
        try
        {
            saved = await FollowAsync("follow", body, ct).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.Message.StartsWith("mode must be notify or quiet", StringComparison.Ordinal))
        {
            throw new InboxPluginException("The gateway's herald-push plugin needs " + AllJobsPluginVersion + " for that.", AllJobsPluginVersion);
        }
        if (!following.EchoedBy(saved))
            throw new InboxPluginException("The gateway's herald-push plugin needs " + AllJobsPluginVersion + " for that.", AllJobsPluginVersion);
        return saved;
    }

    public async Task<IReadOnlyList<InboxRun>> RunsAsync(int limit = Page, long before = 0, CancellationToken ct = default)
    {
        // Every herald-push cron request names this device (BookahHermes InboxApi.request); without it the plugin says "bad device_id".
        var body = new JsonObject { ["device_id"] = deviceId, ["limit"] = limit };
        if (before > 0) body["before"] = before;
        var output = await CronAsync("inbox " + body.ToJsonString(HermesJson.Options), ct).ConfigureAwait(false);
        var reply = Decode(output);
        if (reply["ok"]?.GetValue<bool>() != true)
        {
            var err = reply["error"]?.GetValue<string>();
            if (IsUsage(err)) throw new InboxPluginException(TooOldMessage, MinPluginVersion);
            throw new RpcException(0, err ?? "The gateway couldn't list the runs.");
        }
        var runs = new List<InboxRun>();
        if (reply["runs"] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node is not JsonObject o) continue;
                var run = ParseRun(o);
                if (_profileAllowed(run.Bot)) runs.Add(run);
            }
        }
        return runs;
    }

    public async Task<InboxRun?> ReadAsync(InboxRun run, CancellationToken ct = default)
    {
        if (!_profileAllowed(run.Bot)) return null;
        var body = new JsonObject { ["device_id"] = deviceId, ["bot"] = run.Bot, ["job"] = run.Job, ["run"] = run.Run };
        var output = await CronAsync("read " + body.ToJsonString(HermesJson.Options), ct).ConfigureAwait(false);
        var reply = Decode(output);
        if (reply["ok"]?.GetValue<bool>() == true && reply["run"] is JsonObject o)
            return ParseRun(o);
        if (string.Equals(reply["error"]?.GetValue<string>(), "no such run", StringComparison.Ordinal))
            return null;
        var err = reply["error"]?.GetValue<string>();
        if (IsUsage(err)) throw new InboxPluginException(TooOldMessage, MinPluginVersion);
        throw new RpcException(0, err ?? "The gateway couldn't read the run.");
    }

    private async Task<Following> FollowAsync(string op, JsonObject more, CancellationToken ct)
    {
        var body = new JsonObject { ["device_id"] = deviceId };
        foreach (var kv in more) body[kv.Key] = kv.Value?.DeepClone();
        var output = await CronAsync(op + " " + body.ToJsonString(HermesJson.Options), ct).ConfigureAwait(false);
        var reply = Decode(output);
        if (reply["ok"]?.GetValue<bool>() != true)
        {
            var err = reply["error"]?.GetValue<string>();
            if (IsUsage(err)) throw new InboxPluginException(TooOldMessage, MinPluginVersion);
            // herald-push lets only a device registered for its push notifications follow jobs (cron.py Follows.set).
            if (err == "not registered")
                throw new RpcException(0, "Following jobs needs this PC registered with the gateway's herald-push notifications, which Hikyaku can't do yet.");
            throw new RpcException(0, err ?? "The gateway refused.");
        }
        var def = FollowModeWire.FromWire(reply["default"]?.GetValue<string>() ?? "off");
        var jobs = new Dictionary<string, FollowMode>(StringComparer.Ordinal);
        if (reply["jobs"] is JsonObject jobsObj)
        {
            foreach (var kv in jobsObj)
            {
                var mode = FollowModeWire.FromWire(kv.Value?.GetValue<string>());
                if (mode != def) jobs[kv.Key] = mode;
            }
        }
        return new Following(jobs, reply["failures"]?.GetValue<bool>() == true, def);
    }

    private async Task<string> CronAsync(string arg, CancellationToken ct)
    {
        JsonNode? reply;
        try
        {
            reply = await client().RequestAsync("command.dispatch", new JsonObject
            {
                ["name"] = PluginCommand,
                ["arg"] = "cron " + arg
            }, ct: ct).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.Code == NotACommand)
        {
            throw new InboxPluginException(
                "The gateway's herald-push plugin isn't installed or enabled. Inbox requires herald-push ≥ "
                + MinPluginVersion + " (no server install performed by this client).",
                MinPluginVersion);
        }

        if (reply is not JsonObject obj || obj["type"]?.GetValue<string>() != "plugin")
            throw new InboxPluginException(
                "The gateway's herald-push plugin isn't there.", MinPluginVersion);
        return obj["output"]?.GetValue<string>()
            ?? throw new InboxPluginException("Empty herald-push cron output.", MinPluginVersion);
    }

    private static JsonObject Decode(string output)
    {
        try
        {
            return JsonNode.Parse(output) as JsonObject
                ?? throw new RpcException(0, "The gateway's herald-push plugin gave an answer the Inbox can't read.");
        }
        catch (JsonException)
        {
            throw new RpcException(0, "The gateway's herald-push plugin gave an answer the Inbox can't read.");
        }
    }

    private static InboxRun ParseRun(JsonObject o) => new(
        o["bot"]?.GetValue<string>() ?? "default",
        o["job"]?.GetValue<string>() ?? "",
        o["run"]?.GetValue<string>() ?? "",
        o["label"]?.GetValue<string>() ?? "",
        o["status"]?.GetValue<string>() ?? "ok",
        o["at"]?.GetValue<long>() ?? 0,
        o["text"]?.GetValue<string>() ?? "");

    private static bool IsUsage(string? error) =>
        error?.TrimStart().StartsWith("usage:", StringComparison.OrdinalIgnoreCase) == true;

    private const string TooOldMessage = "The gateway's herald-push plugin is too old for the Inbox.";
}

/// <summary>
/// Offline retained Inbox runs keyed by gateway URL (BookahHermes InboxStore).
/// Identity-gated: unknown/deselected profiles are dropped.
/// </summary>
public sealed class InboxStore(IPreferenceStore store, Func<string, string, bool> allowsProfile)
{
    public const int MaxKept = 100;
    public const int MaxKeptText = 2000;
    public const int MaxRead = 1000;

    private string? _gateway;
    private InboxSnapshot _state = InboxSnapshot.Empty;
    private readonly object _gate = new();

    public InboxSnapshot Snapshot { get { lock (_gate) return _state; } }
    public string? BoundGateway { get { lock (_gate) return _gateway; } }

    public static string KeyFor(string gatewayUrl) => "inbox.v1." + gatewayUrl;

    public async Task BindAsync(string gatewayUrl, long nowUnixSeconds, CancellationToken ct = default)
    {
        Saved saved;
        lock (_gate)
        {
            if (_gateway == gatewayUrl) return;
        }
        saved = await LoadOrStartAsync(gatewayUrl, nowUnixSeconds, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _gateway = gatewayUrl;
            _state = new InboxSnapshot(saved.Runs, saved.Read, saved.Since);
        }
    }

    public async Task ReplaceAsync(string gatewayUrl, IReadOnlyList<InboxRun> runs, bool complete, CancellationToken ct = default)
    {
        await EditAsync(gatewayUrl, s => s with
        {
            Runs = MergeListed(s.Runs, runs, complete).Take(MaxKept).ToList()
        }, ct).ConfigureAwait(false);
    }

    public async Task AddAsync(string gatewayUrl, InboxRun run, CancellationToken ct = default)
    {
        await EditAsync(gatewayUrl, s =>
        {
            var rest = s.Runs.Where(r => r.Key != run.Key);
            return s with { Runs = Merge(new[] { run }.Concat(rest).ToList()) };
        }, ct).ConfigureAwait(false);
    }

    public async Task MarkReadAsync(string gatewayUrl, InboxRun run, CancellationToken ct = default)
    {
        await EditAsync(gatewayUrl, s => s with { Read = TrimRead(s.Read.Append(run.Key).ToHashSet(StringComparer.Ordinal)) }, ct)
            .ConfigureAwait(false);
    }

    public async Task EnforceAsync(string gatewayUrl, CancellationToken ct = default)
        => await EditAsync(gatewayUrl, s => s, ct).ConfigureAwait(false);

    public async Task PruneAsync(string gatewayUrl, Func<InboxRun, bool> keep, CancellationToken ct = default)
        => await EditAsync(gatewayUrl, s => s with { Runs = s.Runs.Where(keep).ToList() }, ct).ConfigureAwait(false);

    private async Task EditAsync(string url, Func<InboxSnapshot, InboxSnapshot> change, CancellationToken ct)
    {
        InboxSnapshot next;
        lock (_gate)
        {
            if (_gateway != url) return;
            var changed = change(_state);
            next = changed with
            {
                Runs = changed.Runs.Where(r => allowsProfile(url, r.Bot)).ToList()
            };
            _state = next;
        }
        await SaveAsync(url, new Saved(next.Runs, next.Read.ToHashSet(StringComparer.Ordinal), next.Since), ct).ConfigureAwait(false);
    }

    private async Task<Saved> LoadOrStartAsync(string gatewayUrl, long now, CancellationToken ct)
    {
        var saved = await LoadAsync(gatewayUrl, ct).ConfigureAwait(false);
        if (saved is null)
        {
            saved = new Saved(Array.Empty<InboxRun>(), new HashSet<string>(), now);
            await SaveAsync(gatewayUrl, saved, ct).ConfigureAwait(false);
            return saved;
        }
        var allowed = saved.Runs.Where(r => allowsProfile(gatewayUrl, r.Bot)).ToList();
        if (allowed.Count == saved.Runs.Count) return saved;
        saved = saved with { Runs = allowed };
        await SaveAsync(gatewayUrl, saved, ct).ConfigureAwait(false);
        return saved;
    }

    private async Task<Saved?> LoadAsync(string url, CancellationToken ct)
    {
        var raw = await store.LoadAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            var root = JsonNode.Parse(raw) as JsonObject;
            var node = (root?["gateways"] as JsonObject)?[url];
            if (node is null) return null;
            var dto = JsonSerializer.Deserialize<SavedDto>(node.ToJsonString(), InboxJson.Options);
            if (dto is null) return null;
            return new Saved(dto.Runs,
                new HashSet<string>(dto.Read, StringComparer.Ordinal), dto.Since);
        }
        catch (JsonException) { return null; }
    }

    private async Task SaveAsync(string url, Saved saved, CancellationToken ct)
    {
        var trimmed = saved with
        {
            Runs = saved.Runs.Select(r => r with { Text = r.Text.Length <= MaxKeptText ? r.Text : r.Text[..MaxKeptText] }).ToList()
        };
        JsonObject root;
        var existing = await store.LoadAsync(ct).ConfigureAwait(false);
        try { root = string.IsNullOrEmpty(existing) ? new JsonObject() : JsonNode.Parse(existing) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { root = new JsonObject(); }
        if (root["gateways"] is not JsonObject gws)
        {
            gws = new JsonObject();
            root["gateways"] = gws;
        }
        gws[url] = JsonSerializer.SerializeToNode(new SavedDto(trimmed.Runs.ToList(), trimmed.Read.ToList(), trimmed.Since), InboxJson.Options);
        await store.SaveAsync(root.ToJsonString(), ct).ConfigureAwait(false);
    }

    public static List<InboxRun> Merge(IReadOnlyList<InboxRun> runs) =>
        runs.DistinctBy(r => r.Key).OrderByDescending(r => r.At).Take(MaxKept).ToList();

    public static List<InboxRun> MergeListed(IReadOnlyList<InboxRun> kept, IReadOnlyList<InboxRun> listed, bool complete)
    {
        var keptByKey = kept.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var fresh = listed.DistinctBy(r => r.Key).Select(run =>
        {
            if (keptByKey.TryGetValue(run.Key, out var old) && old.Text.Length > run.Text.Length)
                return run with { Text = old.Text };
            return run;
        }).ToList();
        var listedKeys = fresh.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var newest = fresh.Count == 0 ? (long?)null : fresh.Max(r => r.At);
        var oldest = fresh.Count == 0 ? (long?)null : fresh.Min(r => r.At);
        var unlisted = kept.Where(run =>
        {
            if (listedKeys.Contains(run.Key)) return false;
            if (newest is null) return false;
            if (run.At > newest) return true;
            if (!complete && oldest is not null && run.At < oldest) return true;
            return false;
        });
        return fresh.Concat(unlisted).OrderByDescending(r => r.At).ToList();
    }

    public static HashSet<string> TrimRead(HashSet<string> read) =>
        read.Count <= MaxRead
            ? read
            : read.OrderByDescending(k => k.Contains('/') ? k[(k.LastIndexOf('/') + 1)..] : k).Take(MaxRead)
                .ToHashSet(StringComparer.Ordinal);

    private sealed record Saved(IReadOnlyList<InboxRun> Runs, HashSet<string> Read, long Since);
    private sealed class SavedDto
    {
        public List<InboxRun> Runs { get; set; } = new();
        public List<string> Read { get; set; } = new();
        public long Since { get; set; }
        public SavedDto() { }
        public SavedDto(List<InboxRun> runs, List<string> read, long since)
        {
            Runs = runs; Read = read; Since = since;
        }
    }
}

public sealed record InboxSnapshot(IReadOnlyList<InboxRun> Runs, IReadOnlySet<string> Read, long Since)
{
    public static InboxSnapshot Empty { get; } = new(Array.Empty<InboxRun>(), new HashSet<string>(), 0);

    public bool IsUnread(InboxRun run) => !Read.Contains(run.Key) && run.At > Since;
    public int Unread => Runs.Count(IsUnread);
}

/// <summary>Profile-gated Inbox façade with loading / setup-required / reconnect states.</summary>
public sealed class InboxService
{
    private readonly InboxStore _store;
    private readonly NotificationGate _gate;
    private readonly string _gatewayUrl;
    private readonly string _gatewayId;
    private readonly Func<JsonRpcClient?> _client;
    private readonly string _deviceId;
    private InboxConnectionState _state = InboxConnectionState.Disconnected;
    private string? _statusDetail;
    private Following _following = Following.Empty;

    public InboxService(InboxStore store, NotificationGate gate, string gatewayUrl, string gatewayId,
        Func<JsonRpcClient?> client, string deviceId)
    {
        _store = store;
        _gate = gate;
        _gatewayUrl = gatewayUrl;
        _gatewayId = gatewayId;
        _client = client;
        _deviceId = deviceId;
    }

    public InboxConnectionState State => _state;
    public string? StatusDetail => _statusDetail;
    public Following Following => _following;
    public InboxSnapshot Snapshot => _store.Snapshot;
    public string GatewayUrl => _gatewayUrl;
    public string GatewayId => _gatewayId;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _store.BindAsync(_gatewayUrl, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ct).ConfigureAwait(false);
        var rpc = _client();
        if (rpc is null)
        {
            _state = InboxConnectionState.Disconnected;
            _statusDetail = "Not connected to the gateway WebSocket.";
            return;
        }

        var api = new InboxApi(() => rpc, _deviceId, bot =>
            _gate.Allow(Surface.Inbox, new ProfileKey(_gatewayId, bot)));

        try
        {
            _following = await api.FollowingAsync(ct).ConfigureAwait(false);
            var runs = await api.RunsAsync(ct: ct).ConfigureAwait(false);
            // Drop runs for deselected / unknown profiles again at the store boundary.
            var allowed = runs.Where(r => _gate.Allow(Surface.Inbox, new ProfileKey(_gatewayId, r.Bot))).ToList();
            await _store.ReplaceAsync(_gatewayUrl, allowed, complete: allowed.Count < InboxApi.Page, ct)
                .ConfigureAwait(false);
            _state = InboxConnectionState.Ready;
            _statusDetail = null;
        }
        catch (InboxPluginException ex)
        {
            _state = InboxConnectionState.SetupRequired;
            _statusDetail = ex.Message;
        }
        catch (Exception ex)
        {
            _state = InboxConnectionState.Error;
            _statusDetail = ex.Message;
        }
    }

    public IReadOnlyList<InboxRun> Filter(bool failuresOnly, string? jobId = null, string? groupName = null,
        InboxGroups? groups = null) =>
        Snapshot.Runs.Where(r =>
        {
            if (!_gate.Allow(Surface.Inbox, new ProfileKey(_gatewayId, r.Bot))) return false;
            if (failuresOnly && !r.Failed) return false;
            if (jobId is not null && !string.Equals(r.Job, jobId, StringComparison.Ordinal)) return false;
            if (groupName is not null)
            {
                var g = groups?.GroupOf(r.Job);
                if (!string.Equals(g, groupName, StringComparison.Ordinal)) return false;
            }
            return true;
        }).ToList();

    public IReadOnlyList<string> JobIds() =>
        Snapshot.Runs.Select(r => r.Job).Where(j => j.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(j => j).ToList();

    /// <summary>Mark read locally and, when connected, call herald-push cron read for full text.</summary>
    public async Task<InboxRun?> OpenRunAsync(InboxRun run, CancellationToken ct = default)
    {
        if (!_gate.Allow(Surface.Inbox, new ProfileKey(_gatewayId, run.Bot)))
            return null;
        await _store.MarkReadAsync(_gatewayUrl, run, ct).ConfigureAwait(false);
        var rpc = _client();
        if (rpc is null) return run;
        var api = new InboxApi(() => rpc, _deviceId, bot =>
            _gate.Allow(Surface.Inbox, new ProfileKey(_gatewayId, bot)));
        try
        {
            var full = await api.ReadAsync(run, ct).ConfigureAwait(false);
            if (full is not null)
            {
                await _store.AddAsync(_gatewayUrl, full, ct).ConfigureAwait(false);
                return full;
            }
        }
        catch (InboxPluginException) { /* keep local text */ }
        catch (RpcException) { /* keep local text */ }
        return run;
    }

    public async Task<Following> SetFollowingAsync(Following following, CancellationToken ct = default)
    {
        var rpc = _client() ?? throw new InvalidOperationException("Not connected.");
        var api = new InboxApi(() => rpc, _deviceId, bot =>
            _gate.Allow(Surface.Inbox, new ProfileKey(_gatewayId, bot)));
        _following = await api.SetFollowingAsync(following, ct).ConfigureAwait(false);
        _state = InboxConnectionState.Ready;
        _statusDetail = null;
        return _following;
    }
}

internal static class InboxJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
