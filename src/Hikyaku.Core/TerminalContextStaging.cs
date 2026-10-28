namespace Hikyaku.Core;

/// <summary>
/// Stages approved terminal selection for the next real prompt — labelled pending until Send.
/// Not a delivered chat bubble; not an implicit send.
/// </summary>
public sealed class TerminalContextStaging
{
    private readonly object _gate = new();
    private TerminalShareDestination? _destination;
    private string? _payload;

    public string? PendingLabel
    {
        get
        {
            lock (_gate)
            {
                if (_payload is null) return null;
                return "Terminal selection staged (pending until Send) → " + (_destination?.Label ?? "");
            }
        }
    }

    public bool HasPending
    {
        get { lock (_gate) return _payload is not null; }
    }

    public void Stage(TerminalShareDestination destination, string approvedPayload)
    {
        if (string.IsNullOrWhiteSpace(approvedPayload))
            throw new ArgumentException("Approved payload required.", nameof(approvedPayload));
        lock (_gate)
        {
            _destination = destination;
            _payload = approvedPayload;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _destination = null;
            _payload = null;
        }
    }

    /// <summary>Build prompt text including staged context when destination still matches. Does not consume.</summary>
    public string BuildNextPrompt(TerminalShareDestination current, string userText)
    {
        string? payload;
        TerminalShareDestination? dest;
        lock (_gate)
        {
            payload = _payload;
            dest = _destination;
        }
        if (payload is null || dest is null || !SameDestination(dest, current))
            return userText ?? "";

        var body = "Terminal selection (approved):\n```\n" + payload + "\n```";
        if (string.IsNullOrWhiteSpace(userText))
            return body;
        return body + "\n\n" + userText.Trim();
    }

    public void Consume()
    {
        Clear();
    }

    private static bool SameDestination(TerminalShareDestination a, TerminalShareDestination b) =>
        a.Profile.GatewayId == b.Profile.GatewayId
        && a.Profile.ProfileId == b.Profile.ProfileId
        && string.Equals(a.SessionId ?? "", b.SessionId ?? "", StringComparison.Ordinal);
}

/// <summary>Safe ConPTY viewport bounds used by UI resize coalescing.</summary>
public static class TerminalViewport
{
    public const int MinColumns = 20;
    public const int MinRows = 8;

    public static (int Columns, int Rows) Clamp(int columns, int rows) =>
        (Math.Max(MinColumns, columns), Math.Max(MinRows, rows));
}

/// <summary>Bounds dispatcher work: many Changed marks collapse to one pending paint.</summary>
public sealed class TerminalUiCoalesce
{
    private int _pending;
    public int PendingCount => Volatile.Read(ref _pending);
    public void MarkChanged() => Interlocked.Exchange(ref _pending, 1);
    public bool Consume() => Interlocked.Exchange(ref _pending, 0) != 0;
}
