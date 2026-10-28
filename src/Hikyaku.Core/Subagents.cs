using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

public enum SubagentStatus
{
    Queued,
    Running,
    Done,
    Failed,
    Interrupted,
    Unwatched
}

public static class SubagentStatusExt
{
    public static bool IsLive(this SubagentStatus s) => s is SubagentStatus.Queued or SubagentStatus.Running;
}

/// <summary>Honesty: gateway "queued" ack is not proof the child read the steer.</summary>
public sealed record SubagentSteerOutcome(bool AcknowledgedQueued, bool ChildConfirmedRead, string HonestyLabel)
{
    public const string WireAccepted = "queued";

    public static SubagentSteerOutcome FromWireStatus(string? status)
    {
        var queued = string.Equals(status, WireAccepted, StringComparison.OrdinalIgnoreCase);
        return new SubagentSteerOutcome(
            AcknowledgedQueued: queued,
            ChildConfirmedRead: false,
            HonestyLabel: queued
                ? "Steer queued on the gateway — not proof the child agent has read it."
                : status is null or ""
                    ? "Steer response missing status."
                    : $"Steer status '{status}' — not a child-read confirmation.");
    }
}

public sealed record SubagentRow(
    string Key,
    string Goal,
    SubagentStatus Status,
    string? Model = null,
    double? DurationSeconds = null,
    IReadOnlyList<string>? Activity = null,
    string? Thinking = null,
    string? Summary = null,
    string? StoppableId = null,
    IReadOnlyList<SubagentRow>? Children = null)
{
    public IReadOnlyList<string> ActivityList => Activity ?? Array.Empty<string>();
    public string StatusLabel => Status.ToString();
    public string ElapsedLabel => DurationSeconds is { } d ? $"{d:0.#}s" : (Status.IsLive() ? "…" : "");
}

/// <summary>
/// Per-session/profile subagent progress — BookahHermes Subagents.kt + ChatSession stop/steer.
/// </summary>
public sealed class SubagentMonitor
{
    private const int MaxActivity = 24;
    private const int MaxFinished = 100;
    private readonly List<SubagentState> _items = new();
    private readonly object _lock = new();

    public SubagentMonitor(ProfileKey profile, string runtimeSessionId)
    {
        Profile = profile;
        RuntimeSessionId = runtimeSessionId;
    }

    public ProfileKey Profile { get; }
    public string RuntimeSessionId { get; }
    public string? LastError { get; private set; }

    public IReadOnlyList<SubagentRow> Rows
    {
        get { lock (_lock) return _items.Select(ToRow).ToList(); }
    }

    public bool Accepts(ProfileKey profile, string? runtimeSessionId) =>
        profile == Profile && string.Equals(runtimeSessionId, RuntimeSessionId, StringComparison.Ordinal);

    public void Clear()
    {
        lock (_lock) _items.Clear();
        LastError = null;
    }

    public void ApplyEvent(string type, JsonObject? payload)
    {
        if (payload is null) return;
        if (!type.StartsWith("subagent.", StringComparison.Ordinal)) return;
        lock (_lock)
        {
            var id = Str(payload, "subagent_id");
            if (string.IsNullOrWhiteSpace(id))
            {
                var parent = Str(payload, "parent_id") ?? "root";
                var idx = Int(payload, "task_index") ?? 0;
                var goal = Str(payload, "goal") ?? "";
                id = $"{parent}:{idx}:{goal}";
            }
            var prev = _items.FirstOrNull(x => x.Id == id);
            var terminal = type == "subagent.complete";
            var parentId = Str(payload, "parent_id") ?? prev?.ParentId;
            var goalText = Str(payload, "goal")?.Trim();
            if (string.IsNullOrEmpty(goalText))
                goalText = prev?.Goal
                    ?? (type is "subagent.spawn_requested" or "subagent.start"
                        ? (Str(payload, "text")?.Trim() ?? "")
                        : "");

            var status = type == "subagent.spawn_requested"
                ? (prev is null || prev.Status == SubagentStatus.Queued ? SubagentStatus.Queued : prev.Status)
                : EventStatus(Str(payload, "status"), terminal);

            var lines = new List<string>();
            if (type == "subagent.tool")
            {
                var tool = Str(payload, "tool_name");
                if (!string.IsNullOrEmpty(tool))
                    lines.Add(ToolLine(tool!, Str(payload, "tool_preview") ?? Str(payload, "text") ?? ""));
            }
            if (prev is null || prev.Activity.Count == 0)
            {
                if (payload["output_tail"] is JsonArray tail)
                {
                    foreach (var t in tail)
                        if (t is JsonObject to && Str(to, "tool") is { Length: > 0 } tn)
                            lines.Add(ToolLine(tn, ""));
                }
            }

            string? thinking = type switch
            {
                "subagent.thinking" => Compact(Str(payload, "text") ?? "") is { Length: > 0 } c ? c : prev?.Thinking,
                "subagent.tool" or "subagent.complete" => null,
                _ => prev?.Thinking
            };

            var activity = prev?.Activity.ToList() ?? new List<string>();
            foreach (var line in lines)
            {
                if (activity.Count > 0 && activity[^1] == line) continue;
                activity.Add(line);
                if (activity.Count > MaxActivity) activity.RemoveAt(0);
            }

            var updated = new SubagentState(
                Id: id!,
                ParentId: string.IsNullOrWhiteSpace(parentId) ? null : parentId,
                Goal: goalText ?? "",
                Status: status,
                TaskIndex: Int(payload, "task_index") ?? prev?.TaskIndex ?? 0,
                Model: Str(payload, "model") ?? prev?.Model,
                Activity: activity,
                Thinking: thinking,
                Summary: terminal ? (Str(payload, "summary")?.Trim() ?? prev?.Summary) : prev?.Summary,
                DurationSeconds: Dbl(payload, "duration_seconds") ?? prev?.DurationSeconds);

            if (prev is null) _items.Add(updated);
            else
            {
                var i = _items.FindIndex(x => x.Id == id);
                if (i >= 0) _items[i] = updated;
            }
            TrimFinished();
        }
    }

    public void ApplySnapshots(IEnumerable<JsonObject> snapshots)
    {
        lock (_lock)
        {
            foreach (var snapshot in snapshots)
            {
                var id = Str(snapshot, "subagent_id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var prev = _items.FirstOrNull(x => x.Id == id);
                var parentId = Str(snapshot, "parent_id") ?? prev?.ParentId;
                var goal = Str(snapshot, "goal")?.Trim() ?? prev?.Goal ?? "";
                var statusWire = Str(snapshot, "status");
                var status = statusWire is not null
                    ? EventStatus(statusWire, terminal: false)
                    : prev?.Status ?? SubagentStatus.Running;
                var activity = prev?.Activity is { Count: > 0 } a
                    ? a
                    : (Str(snapshot, "last_tool") is { Length: > 0 } lt
                        ? new List<string> { lt }
                        : new List<string>());
                var updated = new SubagentState(
                    Id: id!,
                    ParentId: string.IsNullOrWhiteSpace(parentId) ? null : parentId,
                    Goal: goal,
                    Status: status,
                    TaskIndex: prev?.TaskIndex ?? 0,
                    Model: Str(snapshot, "model") ?? prev?.Model,
                    Activity: activity,
                    Thinking: prev?.Thinking,
                    Summary: prev?.Summary,
                    DurationSeconds: prev?.DurationSeconds);
                if (prev is null) _items.Add(updated);
                else
                {
                    var i = _items.FindIndex(x => x.Id == id);
                    if (i >= 0) _items[i] = updated;
                }
            }
            TrimFinished();
        }
    }

    public async Task<SubagentSteerOutcome> SteerAsync(IJsonRpcPeer client, string text, CancellationToken ct = default)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return new SubagentSteerOutcome(false, false, "Empty steer.");
        var result = await client.RequestAsync("session.steer",
            new JsonObject { ["session_id"] = RuntimeSessionId, ["text"] = trimmed }, ct: ct).ConfigureAwait(false) as JsonObject;
        var status = result?["status"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        return SubagentSteerOutcome.FromWireStatus(status);
    }

    public async Task StopAsync(IJsonRpcPeer client, string subagentId, CancellationToken ct = default)
    {
        try
        {
            var result = await client.RequestAsync("subagent.interrupt",
                new JsonObject
                {
                    ["session_id"] = RuntimeSessionId,
                    ["subagent_id"] = subagentId
                }, ct: ct).ConfigureAwait(false) as JsonObject;
            var found = result?["found"] is JsonValue fv && fv.TryGetValue(out bool b) && b;
            if (result is not null && result.ContainsKey("found") && !found)
            {
                lock (_lock)
                {
                    for (var i = 0; i < _items.Count; i++)
                        if (_items[i].Id == subagentId && _items[i].Status.IsLive())
                            _items[i] = _items[i] with { Status = SubagentStatus.Interrupted };
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
        }
    }

    private void TrimFinished()
    {
        var finished = _items.Count(x => !x.Status.IsLive());
        if (finished <= MaxFinished) return;
        var drop = finished - MaxFinished;
        _items.RemoveAll(x =>
        {
            if (drop > 0 && !x.Status.IsLive()) { drop--; return true; }
            return false;
        });
    }

    private SubagentRow ToRow(SubagentState s) =>
        new(
            Key: s.Id,
            Goal: string.IsNullOrEmpty(s.Goal) ? "Subagent" : s.Goal,
            Status: s.Status,
            Model: s.Model,
            DurationSeconds: s.DurationSeconds,
            Activity: s.Activity,
            Thinking: s.Status.IsLive() ? s.Thinking : null,
            Summary: s.Summary,
            StoppableId: s.Status.IsLive() ? s.Id : null,
            Children: _items.Where(c => c.ParentId == s.Id).Select(ToRow).ToList());

    private static SubagentStatus EventStatus(string? status, bool terminal) => status switch
    {
        "completed" or "ok" => SubagentStatus.Done,
        "failed" or "error" or "timeout" => SubagentStatus.Failed,
        "interrupted" or "cancelled" or "canceled" => SubagentStatus.Interrupted,
        "queued" => terminal ? SubagentStatus.Failed : SubagentStatus.Queued,
        _ => terminal ? SubagentStatus.Failed : SubagentStatus.Running
    };

    private static string ToolLine(string name, string preview)
    {
        var p = Compact(preview);
        return p.Length == 0 ? name : $"{name}: {p}";
    }

    private static string Compact(string text)
    {
        var t = Regex.Replace(text, @"\s+", " ").Trim();
        return t.Length <= 200 ? t : t[..199].TrimEnd() + "…";
    }

    private static string? Str(JsonObject o, string k) =>
        o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static int? Int(JsonObject o, string k) =>
        o[k] is JsonValue v && v.TryGetValue(out int i) ? i : null;
    private static double? Dbl(JsonObject o, string k) =>
        o[k] is JsonValue v && v.TryGetValue(out double d) ? d : null;

    private sealed record SubagentState(
        string Id,
        string? ParentId,
        string Goal,
        SubagentStatus Status,
        int TaskIndex,
        string? Model,
        List<string> Activity,
        string? Thinking,
        string? Summary,
        double? DurationSeconds);
}

file static class SubagentLinq
{
    public static T? FirstOrNull<T>(this IEnumerable<T> src, Func<T, bool> pred) where T : class
    {
        foreach (var x in src) if (pred(x)) return x;
        return null;
    }
}
