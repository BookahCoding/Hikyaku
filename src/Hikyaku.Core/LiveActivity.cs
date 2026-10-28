using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

// Stage 44 — what the agent is doing while it answers (BookahHermes ChatReducer.kt @ 5f00986b): reasoning text, the live label,
// the status line, tool steps with their input and result, a risky-output flag, the plan and notices. Before this the transport
// dropped all of them except a one-line tool summary.

/// <summary>Reasoning text as the model produces it (<c>reasoning.delta</c> / <c>reasoning.available</c>).</summary>
public sealed record ReasoningDelta(string Text) : ChatEvent;

/// <summary>The final reasoning carried by <c>message.complete</c>, for a turn whose reasoning did not stream.</summary>
public sealed record FinalReasoning(string Text) : ChatEvent;

/// <summary>What the agent is up to right now, in a few words (<c>thinking.delta</c> frames, <c>tool.generating</c>).</summary>
public sealed record ActivityLabel(string Text) : ChatEvent;

/// <summary>The status line (<c>status.update</c>, or a provider wait explained in a thinking frame); empty clears it.</summary>
public sealed record StatusLine(string Text) : ChatEvent;

/// <summary>A tool call began (<c>tool.start</c>).</summary>
public sealed record ToolStarted(string Id, string Name, string? Detail, string? Input) : ChatEvent;

/// <summary>A tool call ended (<c>tool.complete</c>).</summary>
public sealed record ToolFinished(string Id, string? Summary, double? DurationSeconds, string? Input, string? Output, bool Failed) : ChatEvent;

/// <summary>The gateway flagged a tool's output as high risk (<c>tool.output_risk</c>), e.g. text trying to give the agent orders.</summary>
public sealed record ToolRiskFlagged(string Id, IReadOnlyList<string> Findings, bool Redacted) : ChatEvent;

/// <summary>The agent's plan was written or updated (<c>todo.updated</c>).</summary>
public sealed record PlanUpdated(TodoList Plan) : ChatEvent;

/// <summary>A notice from the agent for the person (<c>notice</c>).</summary>
public sealed record AgentNotice(string Text) : ChatEvent;

public enum TodoStatus { Pending, InProgress, Completed, Cancelled }

/// <summary>One step of the agent's plan; <see cref="Parent"/> nests it under another step.</summary>
public sealed record TodoItem(string Id, string Content, TodoStatus Status, string? Parent = null);

/// <summary>The agent's plan for the running turn, as its <c>todo_list</c> tool last wrote it (BookahHermes Todo.kt).</summary>
public sealed record TodoList(IReadOnlyList<TodoItem> Items, int Revision)
{
    public const int MaxItems = 200;

    public bool Active => Items.Any(i => i.Status is TodoStatus.Pending or TodoStatus.InProgress);
    /// <summary>Cancelled steps count on neither side, as on Desktop.</summary>
    public int Done => Items.Count(i => i.Status == TodoStatus.Completed);
    public int Total => Items.Count(i => i.Status != TodoStatus.Cancelled);

    /// <summary>Items in reading order with their depth: each under its parent; a parent cycle shows flat rather than vanishing.</summary>
    public IReadOnlyList<(TodoItem Item, int Depth)> Tree()
    {
        var ids = Items.Select(i => i.Id).ToHashSet();
        var children = Items.Where(i => i.Parent is not null && ids.Contains(i.Parent)).GroupBy(i => i.Parent!).ToDictionary(g => g.Key, g => g.ToList());
        var seen = new HashSet<string>();
        var output = new List<(TodoItem, int)>();
        void Visit(TodoItem item, int depth)
        {
            if (!seen.Add(item.Id)) return;
            output.Add((item, depth));
            if (children.TryGetValue(item.Id, out var kids)) foreach (var k in kids) Visit(k, depth + 1);
        }
        foreach (var root in Items.Where(i => i.Parent is null || !ids.Contains(i.Parent))) Visit(root, 0);
        foreach (var rest in Items.Where(i => !seen.Contains(i.Id))) Visit(rest, 0);
        return output;
    }

    /// <summary>A <c>{todos, revision}</c> snapshot; null when malformed, or the empty list of an agent that never planned.</summary>
    public static TodoList? Parse(JsonNode? snapshot)
    {
        if (snapshot is not JsonObject o || o["todos"] is not JsonArray todos) return null;
        var revision = LiveEvents.Int(o["revision"]) ?? 0;
        var items = new List<TodoItem>();
        foreach (var node in todos)
        {
            if (node is not JsonObject item) continue;
            TodoStatus? status = LiveEvents.Str(item["status"]) switch
            {
                "pending" => TodoStatus.Pending,
                "in_progress" => TodoStatus.InProgress,
                "completed" => TodoStatus.Completed,
                "cancelled" => TodoStatus.Cancelled,
                _ => null
            };
            var id = LiveEvents.Text(item["id"]);
            var content = LiveEvents.Text(item["content"]);
            if (status is null || id is null || content is null) continue;
            var parent = LiveEvents.Text(item["parent"]);
            items.Add(new TodoItem(id, content, status.Value, parent == id ? null : parent));
            if (items.Count >= MaxItems) break;
        }
        if (items.Count == 0 && revision == 0) return null;
        return new TodoList(items, revision);
    }
}

/// <summary>Turns the gateway's live events into <see cref="ChatEvent"/>s; tolerant of missing and oddly typed fields.</summary>
public static partial class LiveEvents
{
    public const int MaxReasoningChars = 200_000;

    /// <summary>Desktop's provider-wait texts (store/provider-wait.ts, via BookahHermes): these go to the status line, not the label.</summary>
    [GeneratedRegex(@"^(?:⏳|⚠|↻|⚙)\s*(?:(?:still\s+)?waiting on|loading|processing prompt|no (?:output|response)|model returned|rate limited|provider (?:overloaded|temporarily unavailable))", RegexOptions.IgnoreCase)]
    private static partial Regex ProviderWait();

    public static IEnumerable<ChatEvent> From(string type, JsonNode? payload)
    {
        switch (type)
        {
            case "reasoning.delta":
            case "reasoning.available":
                if (Str(payload?["text"]) is { Length: > 0 } r) yield return new ReasoningDelta(r);
                break;
            case "thinking.delta":
                if (Str(payload?["text"])?.Trim() is { Length: > 0 } frame)
                    yield return ProviderWait().IsMatch(frame) ? new StatusLine(frame) : new ActivityLabel(frame);
                break;
            case "tool.generating":
                if (Str(payload?["name"]) is { Length: > 0 } name) yield return new ActivityLabel("preparing " + name + "…");
                break;
            case "tool.start":
                if (Str(payload?["tool_id"]) is { Length: > 0 } startId)
                    yield return new ToolStarted(startId, Str(payload?["name"]) ?? "tool",
                        Str(payload?["context"]) ?? Str(payload?["preview"]), Input(payload?["args"], Str(payload?["args_text"])));
                break;
            case "tool.complete":
                if (Str(payload?["tool_id"]) is { Length: > 0 } doneId)
                {
                    var resultText = Str(payload?["result_text"]) ?? (payload?["result"] is JsonNode res
                        ? (res is JsonValue v && v.TryGetValue<string>(out var s) ? s : res.ToJsonString())
                        : null);
                    yield return new ToolFinished(doneId, Str(payload?["summary"]), Double(payload?["duration_s"]),
                        Input(payload?["args"], null), ToolDetails.Output(resultText), ToolDetails.Failed(resultText));
                }
                break;
            case "tool.output_risk":
                if (Str(payload?["tool_id"]) is { Length: > 0 } riskId && Str(payload?["risk"]) == "high")
                    yield return new ToolRiskFlagged(riskId,
                        (payload?["findings"] as JsonArray ?? []).Select(f => Str(f)).OfType<string>().Take(20).ToList(),
                        payload?["redacted"] is JsonValue rv && rv.TryGetValue<bool>(out var red) && red);
                break;
            case "status.update":
                yield return new StatusLine(Str(payload?["text"])?.Trim() ?? "");
                break;
            case "todo.updated":
                if (TodoList.Parse(payload) is { } plan) yield return new PlanUpdated(plan);
                break;
            case "notice":
                if (Str(payload?["message"])?.Trim() is { Length: > 0 } notice) yield return new AgentNotice(notice);
                break;
        }
    }

    private static string? Input(JsonNode? args, string? argsText)
    {
        if (args is not null)
        {
            using var doc = JsonDocument.Parse(args.ToJsonString());
            if (ToolDetails.Input(doc.RootElement) is { } shown) return shown;
        }
        return string.IsNullOrWhiteSpace(argsText) ? null : argsText.Length > ToolDetails.MaxChars ? argsText[..ToolDetails.MaxChars] + "…" : argsText;
    }

    internal static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Ids and the like may come as numbers.</summary>
    internal static string? Text(JsonNode? n) =>
        n is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()).Trim() is { Length: > 0 } t ? t : null : null;

    internal static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i
        : n is JsonValue d && d.TryGetValue<double>(out var x) ? (int)x : null;

    internal static double? Double(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}
