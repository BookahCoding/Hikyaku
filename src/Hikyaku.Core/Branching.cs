using System.Globalization;

namespace Hikyaku.Core;

/// <summary>Where <c>session.branch</c> put the copy: its stored id (open it like any stored chat) and title.</summary>
public sealed record BranchResult(string StoredSessionId, string? Title);

/// <summary>Implemented by the live gateway transport; lets the UI branch a chat without knowing the transport type.</summary>
public interface IBranchingTransport
{
    /// <param name="count">Null copies the whole chat (<c>session.branch_whole</c>); otherwise the first <paramref name="count"/> stored rows (<c>session.branch</c>).</param>
    Task<BranchResult> BranchSessionAsync(ProfileKey profile, string runtimeSessionId, int? count, CancellationToken ct);
}

/// <summary>Every stored row of a chat, oldest first. <see cref="Complete"/> is false when the page cap was reached.</summary>
public sealed record StoredRows(IReadOnlyList<SessionMessage> Rows, bool Complete);

/// <summary>
/// Counts stored rows the way <c>session.branch</c> does (<c>_visible_branch_history</c>): only user and assistant rows
/// with text count, hidden or not, tool rows do not. "Branch from here" keeps everything up to and including the chosen
/// message; a reply keeps all its steps (every row up to the next prompt).
/// </summary>
public static class BranchPlanner
{
    public static bool Counts(SessionMessage m) =>
        m.Role is "user" or "assistant"
        && !string.IsNullOrWhiteSpace(string.IsNullOrEmpty(m.DisplayContent) ? m.Content : m.DisplayContent);

    /// <summary>The <c>count</c> for <c>session.branch</c>, or null when the row is unknown or nothing would be copied.</summary>
    public static int? CountThrough(IReadOnlyList<SessionMessage> rows, long targetRowId)
    {
        var index = -1;
        for (var i = 0; i < rows.Count; i++)
            if (long.TryParse(rows[i].Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id == targetRowId) { index = i; break; }
        if (index < 0) return null;

        var end = index + 1;
        if (rows[index].Role != "user")
            while (end < rows.Count && rows[end].Role != "user") end++;

        var count = 0;
        for (var i = 0; i < end; i++) if (Counts(rows[i])) count++;
        return count > 0 ? count : null;
    }
}
