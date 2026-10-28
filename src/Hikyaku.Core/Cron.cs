using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

/// <summary>
/// A scheduled job as the dashboard's <c>GET /api/cron/jobs</c> returns it. Parsed tolerantly from the raw JSON (ids and
/// flags may arrive as numbers or strings, times as ISO text or epoch seconds, the schedule as an object or a plain string).
/// The wire contract is BookahHermes <c>CronApi.kt</c> @ 5f00986b; the behaviour (Cron page, All profiles, owner-profile
/// actions, <c>[bot:&lt;name&gt;]</c> routine names) follows Hermes Desktop.
/// </summary>
public sealed record CronJob(
    string Id,
    string Name,
    string Prompt,
    string ScheduleDisplay,
    string State,
    bool Enabled,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    string? LastStatus,
    string? LastError,
    string? LastDeliveryError,
    string? LastFireError,
    string? PausedReason,
    string? Profile,
    string? Model,
    string? Deliver,
    string EditableSchedule)
{
    private static readonly Regex RoutinePrefix = new(@"^\s*\[bot:([^\]]*)\]\s*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool Paused => State == "paused";

    /// <summary>A Bot Mode routine: an ordinary job named <c>[bot:&lt;name&gt;] &lt;routine&gt;</c>.</summary>
    public bool IsRoutine => RoutinePrefix.IsMatch(Name);

    public string? RoutineBot => RoutinePrefix.Match(Name) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The name without a routine's <c>[bot:…]</c> tag, else the start of the prompt, else the id.</summary>
    public string DisplayName
    {
        get
        {
            var n = RoutinePrefix.Replace(Name, "").Trim();
            if (n.Length > 0) return n;
            var first = Prompt.Split('\n', 2)[0].Trim();
            if (first.Length > 50) first = first[..50];
            return first.Length > 0 ? first : Id;
        }
    }

    /// <summary>The last run worked but its result never reached its target.</summary>
    public bool DeliveryFailed => !string.IsNullOrWhiteSpace(LastDeliveryError) || LastStatus == "delivery_failed";

    /// <summary>The scheduler could not hand the last fire to the runner.</summary>
    public bool MissedFire => !string.IsNullOrWhiteSpace(LastFireError);

    public bool LastRunFailed => !string.IsNullOrWhiteSpace(LastError) || LastStatus is "error" or "failed" or "blocked_config";

    public static CronJob? FromJson(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var id = Str(e, "id") ?? Str(e, "job_id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var enabled = Bool(e, "enabled") ?? true;
        var state = Str(e, "state");
        if (string.IsNullOrWhiteSpace(state)) state = enabled ? "scheduled" : "paused";
        var display = Str(e, "schedule_display") ?? "";
        return new CronJob(
            id!, Str(e, "name") ?? "", Str(e, "prompt") ?? "", display, state!, enabled,
            Time(e, "next_run_at"), Time(e, "last_run_at"),
            Str(e, "last_status"), Str(e, "last_error"), Str(e, "last_delivery_error"), FireError(e),
            Str(e, "paused_reason"), Str(e, "profile"), Str(e, "model"), Str(e, "deliver"),
            Editable(e, display));
    }

    private static string? FireError(JsonElement e)
    {
        if (!e.TryGetProperty("last_fire_error", out var v)) return null;
        switch (v.ValueKind)
        {
            case JsonValueKind.Object:
                // The dashboard stamps {at, detail}; an object with no detail is still a missed fire.
                return Str(v, "detail") ?? "The scheduler could not start the last run.";
            case JsonValueKind.String:
                var s = v.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            default: return null;
        }
    }

    /// <summary>The schedule as text the gateway parses back to the same schedule (one-shot jobs have none).</summary>
    private static string Editable(JsonElement e, string display)
    {
        if (e.TryGetProperty("schedule", out var sch) && sch.ValueKind == JsonValueKind.Object)
        {
            switch (Str(sch, "kind"))
            {
                case "cron": return Str(sch, "expr") ?? "";
                case "interval":
                    return sch.TryGetProperty("minutes", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetDouble(out var d) && d >= 1
                        ? "every " + ((long)d).ToString(CultureInfo.InvariantCulture) + "m" : "";
                case "once": return "";
            }
        }
        if (e.TryGetProperty("schedule", out var plain) && plain.ValueKind == JsonValueKind.String
            && plain.GetString() is { Length: > 0 } text && !text.StartsWith("once", StringComparison.OrdinalIgnoreCase))
            return text;
        return display.StartsWith("once", StringComparison.OrdinalIgnoreCase) || display == "?" ? "" : display;
    }

    internal static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
    }

    private static bool? Bool(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n != 0 : null,
            JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" => true,
                "false" or "0" or "no" => false,
                _ => null
            },
            _ => null
        };
    }

    private static DateTimeOffset? Time(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var secs) && secs > 0 && secs < 4e10)
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(secs * 1000));
        if (v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
            return t;
        return null;
    }
}

/// <summary>A place a job's replies can go (<c>GET /api/cron/delivery-targets</c>).</summary>
public sealed record DeliveryTarget(string Id, string Name, bool HomeTargetSet = true);

public enum CronFailure
{
    /// <summary>Session expired or not allowed.</summary>
    Auth,
    /// <summary>The gateway has no cron routes (404 on the collection).</summary>
    Unsupported,
    /// <summary>400/422 — the gateway's own reason (for example an unparseable schedule).</summary>
    Invalid,
    /// <summary>409 — for example "a run is already in progress".</summary>
    Conflict,
    /// <summary>404 on one job — it was removed elsewhere.</summary>
    NotFound,
    Failed
}

public sealed class CronException(CronFailure failure, string message) : Exception(message)
{
    public CronFailure Failure { get; } = failure;
}

/// <summary>
/// Dashboard cron REST (hermes_cli/web_routers/cron.py). Every job call carries the owning profile as a hint the gateway
/// checks, which keeps same-id jobs of different profiles apart. Nothing here retries; a failed call is reported and the
/// caller keeps its list unchanged.
/// </summary>
public sealed class CronApi(DashboardAuth auth)
{
    public const int MaxResponseBytes = 4 * 1024 * 1024;
    public const int MaxJobs = 500;

    /// <summary>Every job across profiles (Desktop's "All profiles"), or one profile's.</summary>
    public async Task<IReadOnlyList<CronJob>> JobsAsync(string? profile, bool allProfiles, CancellationToken ct)
    {
        var p = allProfiles ? "all" : profile;
        using var doc = await SendAsync(HttpMethod.Get, "api/cron/jobs", p, null, collection: true, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var array = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("jobs", out var j) ? j : default;
        if (array.ValueKind != JsonValueKind.Array) return [];
        var list = new List<CronJob>();
        foreach (var item in array.EnumerateArray())
        {
            if (CronJob.FromJson(item) is { } job) list.Add(job);
            if (list.Count >= MaxJobs) break;
        }
        return list;
    }

    public const int MaxRunsPerJob = 50;

    /// <summary>
    /// The stored chats a job's runs produced, newest first (<c>GET /api/cron/jobs/{id}/runs</c>, BookahHermes CronApi.runs @ 5f00986b):
    /// what the Inbox shows. The job's own profile goes with it, as for every job call.
    /// </summary>
    public async Task<IReadOnlyList<SessionSummary>> RunsAsync(CronJob job, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, MaxRunsPerJob);
        using var doc = await SendAsync(HttpMethod.Get, JobPath(job) + "/runs?limit=" + limit, job.Profile, null, collection: false, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var array = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("runs", out var r) ? r : default;
        return SessionsApi.ParseSummaries(array, limit);
    }

    public Task<CronJob> PauseAsync(CronJob job, CancellationToken ct) => ActionAsync(job, "pause", ct);
    public Task<CronJob> ResumeAsync(CronJob job, CancellationToken ct) => ActionAsync(job, "resume", ct);

    /// <summary>Runs the job now, outside its schedule. A 409 means a run is already in progress.</summary>
    public Task<CronJob> TriggerAsync(CronJob job, CancellationToken ct) => ActionAsync(job, "trigger", ct);

    public async Task<CronJob> CreateAsync(CronJobDraft draft, string? profile, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["name"] = draft.Name,
            ["prompt"] = draft.Prompt,
            ["schedule"] = draft.Schedule,
            ["deliver"] = string.IsNullOrWhiteSpace(draft.Deliver) ? "local" : draft.Deliver
        };
        using var doc = await SendAsync(HttpMethod.Post, "api/cron/jobs", profile, body, collection: true, ct).ConfigureAwait(false);
        return CronJob.FromJson(doc.RootElement) ?? throw new CronException(CronFailure.Failed, "The gateway did not return the new job.");
    }

    /// <summary>Changes only the supplied fields (<c>name</c>, <c>prompt</c>, <c>schedule</c>, <c>deliver</c>).</summary>
    public async Task<CronJob> UpdateAsync(CronJob job, IReadOnlyDictionary<string, string> changes, CancellationToken ct)
    {
        if (changes.Count == 0) throw new ArgumentException("nothing to change", nameof(changes));
        var updates = new JsonObject();
        foreach (var (k, v) in changes) updates[k] = v;
        using var doc = await SendAsync(HttpMethod.Put, JobPath(job), job.Profile, new JsonObject { ["updates"] = updates },
            collection: false, ct).ConfigureAwait(false);
        return CronJob.FromJson(doc.RootElement) ?? job;
    }

    public async Task DeleteAsync(CronJob job, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Delete, JobPath(job), job.Profile, null, collection: false, ct).ConfigureAwait(false);
    }

    /// <summary><c>local</c> first, then each messaging platform the profile is set up for.</summary>
    public async Task<IReadOnlyList<DeliveryTarget>> DeliveryTargetsAsync(string? profile, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "api/cron/delivery-targets", profile, null, collection: true, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var arr = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("targets", out var t) ? t
            : root.ValueKind == JsonValueKind.Array ? root : default;
        var list = new List<DeliveryTarget>();
        if (arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
            {
                var id = CronJob.Str(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var home = !(item.TryGetProperty("home_target_set", out var h) && h.ValueKind == JsonValueKind.False);
                list.Add(new DeliveryTarget(id!, CronJob.Str(item, "name") ?? id!, home));
            }
        if (list.All(d => d.Id != "local")) list.Insert(0, new DeliveryTarget("local", "Local (kept on the gateway)"));
        return list;
    }

    private async Task<CronJob> ActionAsync(CronJob job, string verb, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Post, JobPath(job) + "/" + verb, job.Profile, null, collection: false, ct).ConfigureAwait(false);
        return CronJob.FromJson(doc.RootElement) ?? job;
    }

    private static string JobPath(CronJob job) => "api/cron/jobs/" + Uri.EscapeDataString(job.Id);

    private async Task<JsonDocument> SendAsync(HttpMethod method, string relative, string? profile, JsonObject? body, bool collection, CancellationToken ct)
    {
        var path = relative + (string.IsNullOrWhiteSpace(profile) ? "" : (relative.Contains('?') ? "&" : "?") + "profile=" + Uri.EscapeDataString(profile));
        using var req = new HttpRequestMessage(method, new Uri(auth.BaseUri, path));
        if (body is not null)
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await auth.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new CronException(Classify(resp.StatusCode, collection), await ReasonAsync(resp, ct).ConfigureAwait(false));
        if (resp.Content.Headers.ContentLength is { } len && len > MaxResponseBytes)
            throw new CronException(CronFailure.Failed, "The gateway's cron response was too large.");
        var bytes = await ReadBoundedAsync(resp, ct).ConfigureAwait(false);
        if (bytes.Length == 0) return JsonDocument.Parse("{}");
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new CronException(CronFailure.Failed, "The gateway returned an unreadable cron response."); }
    }

    private static CronFailure Classify(HttpStatusCode code, bool collection) => code switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => CronFailure.Auth,
        HttpStatusCode.NotFound => collection ? CronFailure.Unsupported : CronFailure.NotFound,
        HttpStatusCode.Conflict => CronFailure.Conflict,
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => CronFailure.Invalid,
        _ => CronFailure.Failed
    };

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxResponseBytes) throw new CronException(CronFailure.Failed, "The gateway's cron response was too large.");
        }
        return ms.ToArray();
    }

    /// <summary>The gateway's own reason (<c>detail</c> / <c>error</c> / <c>message</c>), trimmed; never a stack or header.</summary>
    private static async Task<string> ReasonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        string fallback = $"The gateway answered {(int)resp.StatusCode}.";
        try
        {
            var bytes = await ReadBoundedAsync(resp, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(bytes);
            var r = doc.RootElement;
            string? reason = null;
            if (r.ValueKind == JsonValueKind.Object)
            {
                reason = CronJob.Str(r, "detail") ?? CronJob.Str(r, "error") ?? CronJob.Str(r, "message");
                // FastAPI validation errors: detail is a list of {msg}.
                if (reason is null && r.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.Array
                    && d.GetArrayLength() > 0) reason = CronJob.Str(d[0], "msg");
            }
            if (string.IsNullOrWhiteSpace(reason)) return fallback;
            reason = new string(reason.Where(c => !char.IsControl(c) || c == ' ').ToArray()).Trim();
            return reason.Length > 300 ? reason[..300] : reason;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { return fallback; }
    }
}

/// <summary>What a new job needs; the gateway parses <see cref="Schedule"/> and stays the authority on whether it is valid.</summary>
public sealed record CronJobDraft(string Name, string Prompt, string Schedule, string Deliver = "local")
{
    public const int MaxNameChars = 200;
    public const int MaxPromptChars = 20_000;
    public const int MaxScheduleChars = 200;

    /// <summary>A reason the draft cannot be sent, or null. This only stops obviously empty or oversized input.</summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Prompt)) return "Write what the job should do (the prompt).";
        if (Prompt.Length > MaxPromptChars) return $"The prompt is longer than {MaxPromptChars:N0} characters.";
        if (Name.Length > MaxNameChars) return $"The name is longer than {MaxNameChars} characters.";
        if (string.IsNullOrWhiteSpace(Schedule)) return "Choose when the job runs.";
        if (Schedule.Length > MaxScheduleChars) return "The schedule is too long.";
        if (Schedule.Any(c => char.IsControl(c))) return "The schedule contains control characters.";
        return null;
    }
}

public enum ScheduleKind
{
    EveryMinutes,
    EveryHours,
    Daily,
    Weekdays,
    Weekly,
    InMinutes,
    InHours,
    /// <summary>The raw Hermes schedule string (cron expression, natural phrase, ISO time).</summary>
    Advanced
}

/// <summary>
/// The structured schedule picker (Hermes Desktop's picker asks for the frequency first, then only the detail that matters,
/// with an Advanced field for the raw string). Produces the text the gateway parses: <c>every 30m</c>, <c>every 2h</c>,
/// <c>daily at 09:00</c>, <c>weekdays at 09:00</c>, <c>every monday 09:00</c>, <c>in 30m</c>, <c>in 2h</c>.
/// </summary>
public sealed record ScheduleSpec(ScheduleKind Kind, int Amount = 1, int Hour = 9, int Minute = 0, DayOfWeek Day = DayOfWeek.Monday, string Raw = "")
{
    public string? Problem()
    {
        switch (Kind)
        {
            case ScheduleKind.EveryMinutes or ScheduleKind.InMinutes:
                if (Amount is < 1 or > 1440) return "Choose between 1 and 1440 minutes.";
                break;
            case ScheduleKind.EveryHours or ScheduleKind.InHours:
                if (Amount is < 1 or > 720) return "Choose between 1 and 720 hours.";
                break;
            case ScheduleKind.Daily or ScheduleKind.Weekdays or ScheduleKind.Weekly:
                if (Hour is < 0 or > 23 || Minute is < 0 or > 59) return "Choose a valid time of day.";
                break;
            case ScheduleKind.Advanced:
                if (string.IsNullOrWhiteSpace(Raw)) return "Enter a schedule such as \"every 2h\", \"weekdays at 9am\" or \"0 9 * * *\".";
                if (Raw.Length > CronJobDraft.MaxScheduleChars) return "The schedule is too long.";
                if (Raw.Any(char.IsControl)) return "The schedule contains control characters.";
                break;
        }
        return null;
    }

    public string ToText()
    {
        var time = $"{Hour:00}:{Minute:00}";
        return Kind switch
        {
            ScheduleKind.EveryMinutes => $"every {Amount}m",
            ScheduleKind.EveryHours => $"every {Amount}h",
            ScheduleKind.InMinutes => $"in {Amount}m",
            ScheduleKind.InHours => $"in {Amount}h",
            ScheduleKind.Daily => $"daily at {time}",
            ScheduleKind.Weekdays => $"weekdays at {time}",
            ScheduleKind.Weekly => $"every {Day.ToString().ToLowerInvariant()} {time}",
            _ => Raw.Trim()
        };
    }

    private static readonly Regex EveryN = new(@"^every\s+(\d{1,4})\s*([mh])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex InN = new(@"^in\s+(\d{1,4})\s*([mh])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex At = new(@"^(daily|weekdays)\s+at\s+(\d{1,2}):(\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex WeeklyAt = new(@"^every\s+(monday|tuesday|wednesday|thursday|friday|saturday|sunday)\s+(\d{1,2}):(\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Opens an existing schedule in the picker when it is one the picker can write; everything else stays Advanced.</summary>
    public static ScheduleSpec FromText(string? text)
    {
        var t = (text ?? "").Trim();
        if (EveryN.Match(t) is { Success: true } e && int.Parse(e.Groups[1].Value, CultureInfo.InvariantCulture) is var n)
            return new(e.Groups[2].Value.Equals("m", StringComparison.OrdinalIgnoreCase) ? ScheduleKind.EveryMinutes : ScheduleKind.EveryHours, n);
        if (InN.Match(t) is { Success: true } i && int.Parse(i.Groups[1].Value, CultureInfo.InvariantCulture) is var k)
            return new(i.Groups[2].Value.Equals("m", StringComparison.OrdinalIgnoreCase) ? ScheduleKind.InMinutes : ScheduleKind.InHours, k);
        if (At.Match(t) is { Success: true } a)
        {
            var h = int.Parse(a.Groups[2].Value, CultureInfo.InvariantCulture);
            var m = int.Parse(a.Groups[3].Value, CultureInfo.InvariantCulture);
            if (h <= 23 && m <= 59)
                return new(a.Groups[1].Value.Equals("daily", StringComparison.OrdinalIgnoreCase) ? ScheduleKind.Daily : ScheduleKind.Weekdays, Hour: h, Minute: m);
        }
        if (WeeklyAt.Match(t) is { Success: true } w)
        {
            var h = int.Parse(w.Groups[2].Value, CultureInfo.InvariantCulture);
            var m = int.Parse(w.Groups[3].Value, CultureInfo.InvariantCulture);
            if (h <= 23 && m <= 59 && Enum.TryParse<DayOfWeek>(w.Groups[1].Value, true, out var d))
                return new(ScheduleKind.Weekly, Hour: h, Minute: m, Day: d);
        }
        return new(ScheduleKind.Advanced, Raw: t);
    }
}
