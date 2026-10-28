using System.Text.Json.Nodes;

namespace Hikyaku.Core;

/// <summary>What a live session on the gateway is doing, from <c>session.active_list</c> (BookahHermes ActiveSessions.kt).</summary>
public enum LiveStatus
{
    Idle,
    /// <summary>The agent is still being built. Not a turn: every cold <c>session.resume</c> builds one.</summary>
    Starting,
    /// <summary>A server request (approval, question, secret…) waits for an answer.</summary>
    Waiting,
    Working
}

public static class LiveStatusExtensions
{
    /// <summary>A turn is under way (it may be blocked on the user). <see cref="LiveStatus.Starting"/> is not a turn.</summary>
    public static bool IsRunning(this LiveStatus s) => s is LiveStatus.Working or LiveStatus.Waiting;

    /// <summary>"streaming" and "resuming" are in the contract and mean a turn is going; anything unknown is idle.</summary>
    public static LiveStatus ParseWire(string? value) => value switch
    {
        "starting" => LiveStatus.Starting,
        "waiting" => LiveStatus.Waiting,
        "working" or "streaming" or "resuming" => LiveStatus.Working,
        _ => LiveStatus.Idle
    };
}

/// <summary>One live session: its runtime id on the gateway, its status and its title (blank before it has one).</summary>
public sealed record LiveSession(string RuntimeId, LiveStatus Status, string Title);

/// <summary>
/// The gateway's live sessions by stored session id. The answer covers every profile on the gateway process, so callers
/// must only match it against chats they already list (profile-scoped).
/// </summary>
public sealed record LiveSnapshot(
    IReadOnlyDictionary<string, LiveSession> Rows, DateTimeOffset? AskedAt, bool Unsupported, string? Error)
{
    public static LiveSnapshot Empty { get; } = new(new Dictionary<string, LiveSession>(), null, false, null);

    /// <summary>At least one successful answer has been read.</summary>
    public bool HasAnswer => AskedAt is not null;

    public bool SameAs(LiveSnapshot o) =>
        Unsupported == o.Unsupported && Error == o.Error && HasAnswer == o.HasAnswer && Rows.Count == o.Rows.Count
        && Rows.All(kv => o.Rows.TryGetValue(kv.Key, out var other) && other == kv.Value);
}

public static class ActiveListParser
{
    /// <summary>Rows without a <c>session_key</c> are skipped; a missing <c>id</c> falls back to the key.</summary>
    public static IReadOnlyDictionary<string, LiveSession> Parse(JsonNode? result)
    {
        var map = new Dictionary<string, LiveSession>(StringComparer.Ordinal);
        if (result is not JsonObject o || o["sessions"] is not JsonArray rows) return map;
        foreach (var node in rows)
        {
            if (node is not JsonObject row) continue;
            var key = Str(row, "session_key");
            if (string.IsNullOrWhiteSpace(key)) continue;
            var id = Str(row, "id");
            map[key] = new LiveSession(string.IsNullOrWhiteSpace(id) ? key : id, LiveStatusExtensions.ParseWire(Str(row, "status")), Str(row, "title") ?? "");
        }
        return map;
    }

    private static string? Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>
/// Asks <c>session.active_list</c> on a poll interval and shortly after a nudge (the owner calls <see cref="Nudge"/> on
/// <c>sessions.changed</c>, <c>message.start</c>, <c>message.complete</c>). A gateway without the method (METHOD_NOT_FOUND)
/// is marked unsupported and never asked again; any other failure keeps the last answer. Runs only between
/// <see cref="Start"/> and <see cref="Stop"/>; a stopped poller never publishes a late answer.
/// </summary>
public sealed class LiveSessionsPoller : IDisposable
{
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultSettle = TimeSpan.FromMilliseconds(500);

    private readonly Func<CancellationToken, Task<JsonNode?>> _ask;
    private readonly TimeSpan _poll, _settle;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private LiveSnapshot _current = LiveSnapshot.Empty;
    private int _nudgePending;
    private TaskCompletionSource? _nudgeTcs;

    public LiveSessionsPoller(Func<CancellationToken, Task<JsonNode?>> ask, TimeSpan? poll = null, TimeSpan? settle = null,
        Func<DateTimeOffset>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _ask = ask;
        _poll = poll ?? DefaultPoll;
        _settle = settle ?? DefaultSettle;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    public event Action? Changed;

    public LiveSnapshot Current { get { lock (_gate) return _current; } }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false }) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loop = Task.Run(() => LoopAsync(ct));
        }
    }

    public void Stop()
    {
        lock (_gate) { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } }
        _nudgeTcs?.TrySetResult();
    }

    /// <summary>An event suggests a status may have changed: ask again once things settle.</summary>
    public void Nudge()
    {
        Interlocked.Exchange(ref _nudgePending, 1);
        _nudgeTcs?.TrySetResult();
    }

    /// <summary>Forget every row (profile/gateway change, disconnect): nothing old may show as live.</summary>
    public void Clear() => Publish(LiveSnapshot.Empty);

    public void Dispose()
    {
        Stop();
        lock (_gate) { _cts?.Dispose(); _cts = null; }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (await AskOnceAsync(ct).ConfigureAwait(false)) return;
            bool nudged;
            try { nudged = await WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (!nudged) continue;
            try { await _delay(_settle, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            Interlocked.Exchange(ref _nudgePending, 0);
        }
    }

    /// <summary>True when the loop should end (stopped, or the gateway cannot answer).</summary>
    private async Task<bool> AskOnceAsync(CancellationToken ct)
    {
        JsonNode? result;
        try { result = await _ask(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return true; }
        catch (RpcException ex) when (ex.Code == -32601)
        {
            if (!ct.IsCancellationRequested) Publish(new LiveSnapshot(Current.Rows, Current.AskedAt, true, null));
            return true;
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) Publish(new LiveSnapshot(Current.Rows, Current.AskedAt, false, ex.Message));
            return ct.IsCancellationRequested;
        }
        if (ct.IsCancellationRequested) return true;     // stopped meanwhile: discard
        Publish(new LiveSnapshot(ActiveListParser.Parse(result), _clock(), false, null));
        return false;
    }

    private async Task<bool> WaitAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _nudgePending, 0) == 1) return true;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _nudgeTcs = tcs;
        if (Interlocked.CompareExchange(ref _nudgePending, 0, 1) == 1) { _nudgeTcs = null; return true; }
        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = _delay(_poll, timerCts.Token);
        var winner = await Task.WhenAny(timer, tcs.Task).ConfigureAwait(false);
        timerCts.Cancel();
        _nudgeTcs = null;
        ct.ThrowIfCancellationRequested();
        if (winner == timer) { await timer.ConfigureAwait(false); return false; }
        return true;
    }

    private void Publish(LiveSnapshot next)
    {
        bool changed;
        lock (_gate)
        {
            changed = !_current.SameAs(next);
            _current = next;
        }
        if (changed) Changed?.Invoke();
    }
}
