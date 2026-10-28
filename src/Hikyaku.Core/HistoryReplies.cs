namespace Hikyaku.Core;

/// <summary>One tool call of a stored reply, with its arguments and result already made readable.</summary>
public sealed record HistoryToolStep(string Id, string Name, string? Input, string? Output, bool Failed);

/// <summary>A stored tool call as the gateway saved it on an assistant row (<c>tool_calls[]</c>).</summary>
public sealed record StoredToolCall(string? Id, string Name, string? Input);

/// <summary>A chat message built from stored rows: a prompt, or a whole reply (every step of it).</summary>
public sealed record HistoryEntry(
    bool IsUser,
    string Text,
    /// <summary>The row that identifies the message for edit / branch: the prompt's row, or the reply's last row.</summary>
    string LastRowId,
    /// <summary>Every stored row this entry stands for (a reply merges several).</summary>
    IReadOnlyList<string> RowIds,
    IReadOnlyList<HistoryToolStep> Tools,
    string? SentText = null,
    /// <summary>The reply's reasoning: every step's, joined (Stage 44).</summary>
    string Reasoning = "");

/// <summary>
/// Stored rows → messages. A turn stores a row per assistant step and per tool result; consecutive assistant rows
/// (text and tool-call steps) merge into one reply listing every tool it used, as BookahHermes <c>historyToMessages</c>
/// does. Tool results and system / hidden rows are not messages. A reply whose first steps sit on an older, unloaded
/// page starts a bubble of its own on the page that has it.
/// </summary>
public static class HistoryReplies
{
    public static IReadOnlyList<HistoryEntry> Merge(IEnumerable<SessionMessage> rows)
    {
        var all = rows.ToList();
        var results = new Dictionary<string, SessionMessage>(StringComparer.Ordinal);
        foreach (var r in all)
            if (r.Role.Equals("tool", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(r.ToolCallId))
                results[r.ToolCallId!] = r;

        var entries = new List<HistoryEntry>();
        var open = false;   // the last entry is a reply that may take more steps
        foreach (var m in all)
        {
            if (string.Equals(m.DisplayKind, "hidden", StringComparison.OrdinalIgnoreCase)) continue;
            var body = string.IsNullOrEmpty(m.DisplayContent) ? m.Content : m.DisplayContent!;
            if (m.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                open = false;
                if (string.IsNullOrWhiteSpace(body)) continue;
                var sent = !string.IsNullOrEmpty(m.DisplayContent) && m.DisplayContent != m.Content ? m.Content : null;
                entries.Add(new HistoryEntry(true, body, m.Id, [m.Id], [], sent));
                continue;
            }
            if (!m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)) continue;

            var failedTurn = string.Equals(m.DisplayKind, "failed_turn", StringComparison.OrdinalIgnoreCase);
            var tools = new List<HistoryToolStep>();
            var index = 0;
            foreach (var call in m.ToolCalls ?? [])
            {
                SessionMessage? result = null;
                if (call.Id is not null) results.TryGetValue(call.Id, out result);
                tools.Add(new HistoryToolStep(
                    call.Id ?? (m.Id + "-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    call.Name, call.Input, ToolDetails.Output(result?.Content), ToolDetails.Failed(result?.Content)));
                index++;
            }
            var text = body ?? "";
            var hasText = !string.IsNullOrWhiteSpace(text);
            if (!hasText && tools.Count == 0 && string.IsNullOrWhiteSpace(m.Reasoning)) continue;   // a step with nothing to show

            if (open && !failedTurn && entries[^1] is { IsUser: false } prev)
            {
                entries[^1] = prev with
                {
                    Text = hasText ? (prev.Text.Length > 0 ? prev.Text + "\n\n" + text : text) : prev.Text,
                    LastRowId = m.Id,
                    RowIds = [.. prev.RowIds, m.Id],
                    Tools = [.. prev.Tools, .. tools],
                    Reasoning = JoinReasoning(prev.Reasoning, m.Reasoning),
                };
                continue;
            }
            entries.Add(new HistoryEntry(false, hasText ? text : "", m.Id, [m.Id], tools, null, m.Reasoning?.Trim() ?? ""));
            open = !failedTurn;
        }
        return entries;
    }

    private static string JoinReasoning(string before, string? next) =>
        string.IsNullOrWhiteSpace(next) ? before : before.Length == 0 ? next.Trim() : before + "\n\n" + next.Trim();
}
