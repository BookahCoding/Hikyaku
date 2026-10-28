namespace Hikyaku.Core;

/// <summary>One refresh of the Inbox as the poller sees it.</summary>
public sealed record InboxPollResult(InboxSnapshot Snapshot, Following Following, InboxConnectionState State, string GatewayId);

/// <summary>
/// Turns newly appeared Inbox runs into toasts, by Android's rules (Inbox.kt): a job set to Notify notifies for its runs, and the
/// failures flag means any failed run notifies; Quiet jobs only land in the Inbox. The toast carries the job name, the outcome and
/// the profile, never the run's output. The first refresh only learns what exists (no replay of history, and none when a job is
/// switched to Notify later), runs already read elsewhere are skipped, bursts are coalesced, and a toast is not announced late
/// if the window was in front when the run arrived.
/// </summary>
public sealed class InboxNotifier
{
    public const string Title = "Scheduled job result";
    private const int MaxRemembered = 5000;

    private readonly Func<ProfileKey, string, string, ToastDeliveryResult> _notify;
    private readonly Func<bool> _suppressWhile;
    private readonly ToastThrottle _throttle;
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();
    private bool _primed;

    public InboxNotifier(Func<ProfileKey, string, string, ToastDeliveryResult> notify, Func<bool> suppressWhile, ToastThrottle? throttle = null)
    {
        _notify = notify;
        _suppressWhile = suppressWhile;
        _throttle = throttle ?? new ToastThrottle(TimeSpan.FromMinutes(1));
    }

    public ToastDeliveryResult? LastResult { get; private set; }

    /// <summary>Whether anything about this following can produce a notification (otherwise there is no reason to poll).</summary>
    public static bool WantsNotifications(Following f) =>
        f.Failures || f.Default == FollowMode.Notify || f.Jobs.Values.Any(m => m == FollowMode.Notify);

    public void OnRefreshed(InboxSnapshot snapshot, Following following, string gatewayId)
    {
        List<InboxRun> eligible;
        lock (_gate)
        {
            var fresh = snapshot.Runs.Where(r => !_announced.Contains(r.Key)).ToList();
            foreach (var r in fresh) Remember(r.Key);
            if (!_primed) { _primed = true; return; }                       // learn what exists; never replay history
            eligible = fresh.Where(r => !snapshot.Read.Contains(r.Key)
                                        && (following.Of(r.Job) == FollowMode.Notify || (following.Failures && r.Failed))).ToList();
        }
        if (eligible.Count == 0 || _suppressWhile()) return;

        foreach (var group in eligible.GroupBy(r => r.Bot))
        {
            if (!_throttle.TryAcquire("inbox")) return;
            var profile = new ProfileKey(gatewayId, group.Key);
            var first = group.First();
            var body = group.Count() == 1
                ? $"{first.Title} {(first.Failed ? "failed" : "finished")} · {group.Key}"
                : $"{group.Count()} new results · {group.Key}";
            try { LastResult = _notify(profile, Title, body); }
            catch (Exception ex) { LastResult = new ToastDeliveryResult(ToastObservation.NotObserved, "notify failed: " + ex.Message); }
        }
    }

    private void Remember(string key)
    {
        if (!_announced.Add(key)) return;
        _order.Enqueue(key);
        while (_order.Count > MaxRemembered) _announced.Remove(_order.Dequeue());
    }
}

/// <summary>
/// Polls the Inbox on an interval, but only while notifying is wanted: the first refresh learns what is followed, and polling
/// ends when nothing asks for notifications or the herald-push plugin is missing. A dropped connection or a failed refresh is
/// retried. <see cref="Kick"/> refreshes now and restarts a stopped poll (the user changed what is followed). A stopped poll
/// never delivers a late answer.
/// </summary>
public sealed class InboxNotifyPoller : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(2);

    private readonly Func<CancellationToken, Task<InboxPollResult>> _refresh;
    private readonly Action<InboxPollResult> _deliver;
    private readonly TimeSpan _interval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private TaskCompletionSource? _kick;
    private int _kickPending;
    private bool _wantsLast;

    public InboxNotifyPoller(Func<CancellationToken, Task<InboxPollResult>> refresh, Action<InboxPollResult> deliver,
        TimeSpan? interval = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _refresh = refresh;
        _deliver = deliver;
        _interval = interval ?? DefaultInterval;
        _delay = delay ?? Task.Delay;
    }

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
        _kick?.TrySetResult();
    }

    /// <summary>Refresh now (and start again if polling had ended).</summary>
    public void Kick()
    {
        bool running;
        lock (_gate) running = _loop is { IsCompleted: false };
        if (!running) { Start(); return; }
        Interlocked.Exchange(ref _kickPending, 1);
        _kick?.TrySetResult();
    }

    public void Dispose()
    {
        Stop();
        lock (_gate) { _cts?.Dispose(); _cts = null; }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            InboxPollResult? result = null;
            try { result = await _refresh(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { /* keep what is known; retried below if notifications are wanted */ }
            if (ct.IsCancellationRequested) return;                          // stopped meanwhile: discard

            var keepGoing = _wantsLast;
            if (result is not null)
            {
                try { _deliver(result); } catch (Exception) { /* a listener must not end polling */ }
                _wantsLast = InboxNotifier.WantsNotifications(result.Following);
                keepGoing = result.State switch
                {
                    InboxConnectionState.SetupRequired => false,             // a missing plugin will not fix itself
                    InboxConnectionState.Ready => _wantsLast,
                    _ => true                                                 // disconnected / error: retry
                };
            }
            if (!keepGoing) return;

            try { await WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task WaitAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _kickPending, 0) == 1) return;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _kick = tcs;
        if (Interlocked.Exchange(ref _kickPending, 0) == 1) { _kick = null; return; }
        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = _delay(_interval, timerCts.Token);
        var winner = await Task.WhenAny(timer, tcs.Task).ConfigureAwait(false);
        timerCts.Cancel();
        _kick = null;
        ct.ThrowIfCancellationRequested();
        if (winner == timer) await timer.ConfigureAwait(false);
    }
}
