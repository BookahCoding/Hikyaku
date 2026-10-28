using System.Text.Json;

namespace Hikyaku.Core;

/// <summary>One result in the Inbox: a run of a scheduled job, which the gateway keeps as a stored chat.</summary>
public sealed record JobResult(CronJob Job, SessionSummary Run, bool Failed)
{
    public string JobKey => JobInbox.KeyOf(Job);
    public string Key => JobKey + "/" + Run.Id;
    public string Profile => Job.Profile ?? "";
    public string Title => Job.DisplayName;
    public long At => Run.StartedAt > 0 ? Run.StartedAt : Run.LastActive;
    public string Preview => (Run.Preview ?? Run.Title ?? "").Trim();

    /// <summary>
    /// A script-only job's run: an output document, not a chat (hermes-agent cron.py <c>_list_cron_output_runs</c>, ids
    /// <c>cron_output:…</c>). The sessions routes answer 404 for it, and Hermes Desktop does not offer it as a chat either
    /// (open-cron-run.ts <c>isCronRunRow</c>); the preview is all the gateway serves.
    /// </summary>
    public bool IsScriptOutput => Run.Source == "cron_output" || Run.Id.StartsWith("cron_output:", StringComparison.Ordinal);
}

/// <summary>A folder of the Inbox (BookahHermes InboxPage's Folders layout): failed runs, a group of jobs, or one job.</summary>
public sealed record InboxFolder(string Key, string Name, InboxFolderKind Kind, int Count, long NewestAt, string NewestPreview, string? JobKey);

public enum InboxFolderKind { Failed, Group, Job }

public static class InboxFolders
{
    public const string FailedKey = "failed";
    public static string GroupKey(string name) => "group:" + name;
    public static string JobKey(string jobKey) => "job:" + jobKey;

    /// <summary>Failed first (when any run failed), then groups and ungrouped jobs, the most recent first.</summary>
    public static IReadOnlyList<InboxFolder> Of(IReadOnlyList<JobResult> results, InboxGroups groups)
    {
        var folders = new List<InboxFolder>();
        var failed = results.Where(r => r.Failed).ToList();
        if (failed.Count > 0) folders.Add(Make(FailedKey, "Failed", InboxFolderKind.Failed, failed, null));
        var buckets = results.GroupBy(r => groups.GroupOf(r.JobKey) is { Length: > 0 } g ? GroupKey(g) : JobKey(r.JobKey));
        folders.AddRange(buckets.Select(b => b.Key.StartsWith("group:", StringComparison.Ordinal)
                ? Make(b.Key, b.Key["group:".Length..], InboxFolderKind.Group, b.ToList(), null)
                : Make(b.Key, b.First().Title, InboxFolderKind.Job, b.ToList(), b.First().JobKey))
            .OrderByDescending(f => f.NewestAt).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase));
        return folders;
    }

    /// <summary>The results inside one folder, newest first.</summary>
    public static IReadOnlyList<JobResult> In(IReadOnlyList<JobResult> results, string folderKey, InboxGroups groups) =>
        results.Where(r => folderKey == FailedKey ? r.Failed
                : folderKey.StartsWith("group:", StringComparison.Ordinal) ? groups.GroupOf(r.JobKey) == folderKey["group:".Length..]
                : folderKey == JobKey(r.JobKey) && !(groups.GroupOf(r.JobKey) is { Length: > 0 }))
            .OrderByDescending(r => r.At).ToList();

    private static InboxFolder Make(string key, string name, InboxFolderKind kind, List<JobResult> rs, string? jobKey)
    {
        var newest = rs.OrderByDescending(r => r.At).First();
        return new InboxFolder(key, name, kind, rs.Count, newest.At, newest.Preview, jobKey);
    }
}

public sealed record JobInboxLoad(IReadOnlyList<JobResult> Results, int HiddenJobs, IReadOnlyList<string> Problems);

/// <summary>
/// The Inbox, built on the dashboard's own scheduled-job routes (option B, the maintainer, 2026-10-27): every job of the profiles
/// ticked here, and the stored chats its runs produced. It needs no plugin and no device registration (the herald-push Inbox only
/// lets a device registered for its push notifications follow jobs, which a PC cannot be yet). A profile that is not ticked never
/// shows; a job the gateway does not attribute to a profile is left out of the all-profiles view; hidden jobs stay on the gateway
/// and only leave this PC's Inbox.
/// </summary>
public static class JobInbox
{
    public const int MaxJobs = 40;
    public const int RunsPerJob = 10;
    public const int MaxResults = 300;
    private const int Parallel = 4;

    public static string KeyOf(CronJob job) => (job.Profile ?? "") + "/" + job.Id;

    public static async Task<JobInboxLoad> LoadAsync(
        Func<CancellationToken, Task<IReadOnlyList<CronJob>>> listJobs,
        Func<CronJob, CancellationToken, Task<IReadOnlyList<SessionSummary>>> listRuns,
        string? requestedProfile, bool allProfiles, Func<string, bool> profileAllowed, IReadOnlySet<string> hiddenJobs,
        CancellationToken ct)
    {
        var jobs = new List<CronJob>();
        var hidden = 0;
        foreach (var raw in await listJobs(ct).ConfigureAwait(false))
        {
            // One profile's list may leave the profile out; the all-profiles list must name it (never guess an owner).
            var job = string.IsNullOrWhiteSpace(raw.Profile) && !allProfiles ? raw with { Profile = requestedProfile } : raw;
            if (string.IsNullOrWhiteSpace(job.Profile) || !profileAllowed(job.Profile!)) continue;
            if (hiddenJobs.Contains(KeyOf(job))) { hidden++; continue; }
            jobs.Add(job);
            if (jobs.Count >= MaxJobs) break;
        }

        var results = new List<JobResult>();
        var problems = new List<string>();
        using var gate = new SemaphoreSlim(Parallel);
        var tasks = jobs.Select(async job =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { return (job, runs: await listRuns(job, ct).ConfigureAwait(false), error: (string?)null); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return (job, runs: (IReadOnlyList<SessionSummary>)[], error: ex.Message); }   // one job never stops the rest
            finally { gate.Release(); }
        }).ToList();
        foreach (var (job, runs, error) in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            if (error is not null) { problems.Add($"{job.DisplayName}: {error}"); continue; }
            var newest = runs.Where(r => r.Id.Length > 0).OrderByDescending(r => r.StartedAt > 0 ? r.StartedAt : r.LastActive).ToList();
            for (var i = 0; i < newest.Count; i++)
                results.Add(new JobResult(job, newest[i], Failed: i == 0 && job.LastRunFailed));   // only the latest run's outcome is known
        }
        var ordered = results.GroupBy(r => r.Key).Select(g => g.First())
            .OrderByDescending(r => r.At).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).Take(MaxResults).ToList();
        return new JobInboxLoad(ordered, hidden, problems);
    }
}

/// <summary>
/// Which results are new since the Inbox was last looked at, for Windows notifications. The first load only learns what is
/// there (nothing old is announced when the app starts); after that a result is new once.
/// </summary>
public sealed class JobInboxNews
{
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private bool _primed;

    public IReadOnlyList<JobResult> Next(IReadOnlyList<JobResult> results)
    {
        var fresh = results.Where(r => !_known.Contains(r.Key)).ToList();
        foreach (var r in results) _known.Add(r.Key);
        if (!_primed) { _primed = true; return []; }
        return fresh;
    }

    /// <summary>The notification for new results: the job and profile for one, a count for several (never the result text).</summary>
    public static (string Title, string Body) Describe(IReadOnlyList<JobResult> fresh, Func<string, string> profileLabel) =>
        fresh.Count == 1
            ? (fresh[0].Failed ? "A scheduled job failed" : "New scheduled job result", fresh[0].Title + " · " + profileLabel(fresh[0].Profile))
            : ($"{fresh.Count} new scheduled job results", string.Join(", ", fresh.Select(r => r.Title).Distinct().Take(3)) + (fresh.Select(r => r.Title).Distinct().Count() > 3 ? "…" : ""));
}

/// <summary>The jobs hidden from this PC's Inbox (kept on this PC only; the jobs themselves are untouched).</summary>
public sealed class JobInboxPrefs(IPreferenceStore store)
{
    private HashSet<string> _hidden = new(StringComparer.Ordinal);

    public IReadOnlySet<string> Hidden => _hidden;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var text = await store.LoadAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) return;
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.Array)
                _hidden = new HashSet<string>(h.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!), StringComparer.Ordinal);
        }
        catch (JsonException) { /* an unreadable file hides nothing */ }
    }

    public Task HideAsync(string jobKey, CancellationToken ct = default) { _hidden.Add(jobKey); return SaveAsync(ct); }

    public Task ShowAllAsync(CancellationToken ct = default) { _hidden.Clear(); return SaveAsync(ct); }

    private Task SaveAsync(CancellationToken ct) =>
        store.SaveAsync(JsonSerializer.Serialize(new { hidden = _hidden.OrderBy(k => k, StringComparer.Ordinal) }), ct);
}

/// <summary>
/// Which Inbox results this PC has read (0.7.0): result keys by gateway, so the same job ids on another gateway are not
/// marked. Only ids are kept, never a result's text; the newest <see cref="Max"/> per gateway.
/// </summary>
public sealed class InboxReadStore(IPreferenceStore store)
{
    public const int Max = 5000;
    private readonly Dictionary<string, List<string>> _read = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _lookup = new(StringComparer.Ordinal);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var text = await store.LoadAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) return;
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var gateway in doc.RootElement.EnumerateObject())
            {
                if (gateway.Value.ValueKind != JsonValueKind.Array) continue;
                var keys = gateway.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
                _read[gateway.Name] = keys;
                _lookup[gateway.Name] = new HashSet<string>(keys, StringComparer.Ordinal);
            }
        }
        catch (JsonException) { /* an unreadable file marks nothing read */ }
    }

    public bool IsRead(string gatewayId, string runKey) => _lookup.TryGetValue(gatewayId, out var keys) && keys.Contains(runKey);

    public Task MarkReadAsync(string gatewayId, IEnumerable<string> runKeys, CancellationToken ct = default)
    {
        if (!_read.TryGetValue(gatewayId, out var list)) { _read[gatewayId] = list = new List<string>(); _lookup[gatewayId] = new HashSet<string>(StringComparer.Ordinal); }
        var lookup = _lookup[gatewayId];
        var added = false;
        foreach (var key in runKeys)
            if (lookup.Add(key)) { list.Add(key); added = true; }
        if (!added) return Task.CompletedTask;
        if (list.Count > Max)
        {
            foreach (var old in list.Take(list.Count - Max)) lookup.Remove(old);
            list.RemoveRange(0, list.Count - Max);                // the oldest marks go first
        }
        return store.SaveAsync(JsonSerializer.Serialize(_read), ct);
    }
}
