using System.Globalization;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>
/// Stored rows → chat bubbles. A real chat stores a row per tool call and per tool result, and the assistant row that
/// asked for a tool has no text of its own; <see cref="HistoryReplies"/> merges a turn's steps into one reply that lists
/// its tool calls (shown as collapsed disclosures), so there are no blank bubbles and no one-bubble-per-step replies.
/// Every shown bubble keeps its stored row id (a reply: its last row, for branch) and all the rows it stands for
/// (for de-duplication across pages).
/// </summary>
internal static class HistoryMapping
{
    public static IReadOnlyList<MessageVM> ToMessages(IEnumerable<SessionMessage> rows)
    {
        var list = new List<MessageVM>();
        foreach (var e in HistoryReplies.Merge(rows))
        {
            var vm = new MessageVM(e.IsUser ? "You" : "Assistant", e.Text)
            {
                RowId = ParseRowId(e.LastRowId),
                SentText = e.SentText
            };
            foreach (var id in e.RowIds)
                if (ParseRowId(id) is { } row) vm.CoveredRowIds.Add(row);
            foreach (var t in e.Tools)
                vm.Tools.Add(new ToolStepVM(t.Name, t.Input, t.Output, t.Failed));
            if (e.Reasoning.Length > 0) vm.SetStoredReasoning(e.Reasoning);
            list.Add(vm);
        }
        return list;
    }

    public static long? ParseRowId(string? id) =>
        long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
}
