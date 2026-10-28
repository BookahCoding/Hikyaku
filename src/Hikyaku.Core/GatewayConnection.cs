using System.Text.Json.Nodes;

namespace Hikyaku.Core;

/// <summary>
/// Keeps one authenticated /api/ws JSON-RPC connection alive: fresh WS ticket per attempt,
/// exponential backoff, terminal SessionExpired / Failed (BookahHermes GatewayConnection.kt).
/// </summary>
public sealed class GatewayConnection : IAsyncDisposable
{
    public static readonly TimeSpan[] BackoffSchedule =
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30)
    };

    public static readonly TimeSpan StableConnection = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultReadyTimeout = TimeSpan.FromSeconds(15);

    private readonly Func<CancellationToken, Task<string>> _mintTicket;
    private readonly Func<string, CancellationToken, Task<IRpcTransport>> _openSocket;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _readyTimeout;
    private readonly object _gate = new();
    private ConnectionPhase _phase = new ConnectionIdle();
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private TaskCompletionSource? _wake;
    private int _started;

    public GatewayConnection(
        Func<CancellationToken, Task<string>> mintTicket,
        Func<string, CancellationToken, Task<IRpcTransport>> openSocket,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? readyTimeout = null)
    {
        _mintTicket = mintTicket;
        _openSocket = openSocket;
        _delay = delay ?? Task.Delay;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _readyTimeout = readyTimeout ?? DefaultReadyTimeout;
    }

    public ConnectionPhase Phase
    {
        get { lock (_gate) return _phase; }
    }

    public JsonRpcClient? Client => Phase is ConnectionConnected c ? c.Client : null;

    public event Action<ConnectionPhase>? PhaseChanged;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            // Already running: treat as retry wake.
            RetryNow();
            return;
        }
        _loopCts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_loopCts.Token));
    }

    public void Stop()
    {
        Interlocked.Exchange(ref _started, 0);
        try { _loopCts?.Cancel(); } catch { /* ignore */ }
        _wake?.TrySetResult();
        var client = Client;
        SetPhase(new ConnectionIdle());
        if (client is not null)
            _ = client.DisposeAsync().AsTask();
    }

    /// <summary>Skip current backoff, or restart after Failed.</summary>
    public void RetryNow()
    {
        if (_loop is null || _loop.IsCompleted)
        {
            Interlocked.Exchange(ref _started, 0);
            Start();
            return;
        }
        _wake?.TrySetResult();
    }

    public async Task WaitConnectedAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var phase = Phase;
            switch (phase)
            {
                case ConnectionConnected:
                    return;
                case ConnectionSessionExpired:
                    throw new AuthFailedException("session_expired", "Dashboard session expired");
                case ConnectionFailed f:
                    throw new InvalidOperationException(f.Reason);
                case ConnectionIdle when _loop is null || _loop.IsCompleted:
                    Start();
                    break;
            }
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        var ticketRejections = 0;
        while (!ct.IsCancellationRequested)
        {
            attempt++;
            SetPhase(new ConnectionConnecting(attempt));

            string ticket;
            try
            {
                ticket = await _mintTicket(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (AuthFailedException ex) when (ex.Kind == "session_expired")
            {
                SetPhase(new ConnectionSessionExpired());
                return;
            }
            catch (Exception ex)
            {
                await BackoffAsync(attempt, ex.Message, ct).ConfigureAwait(false);
                continue;
            }

            DateTimeOffset? connectedAt = null;
            Exception? failure = null;
            JsonRpcClient? client = null;
            IRpcTransport? socket = null;
            try
            {
                socket = await _openSocket(ticket, ct).ConfigureAwait(false);
                client = new JsonRpcClient(socket);
                using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readyCts.CancelAfter(_readyTimeout);
                var ready = await client.Ready.WaitAsync(readyCts.Token).ConfigureAwait(false);
                connectedAt = _clock();
                SetPhase(new ConnectionConnected(client, ready));
                // Wait for pump end without consuming Events (StreamChatAsync / Needs-you own that channel).
                var closeCause = await client.Closed.WaitAsync(ct).ConfigureAwait(false);
                failure = closeCause is TransportClosedException tce
                    ? tce
                    : closeCause ?? new TransportClosedException(null, "Connection lost");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                failure = new TimeoutException("Gateway never said it was ready");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (Phase is ConnectionConnected)
                    SetPhase(new ConnectionConnecting(attempt)); // transient while classifying
                if (client is not null)
                {
                    try { await client.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
                }
            }

            if (connectedAt is { } at && _clock() - at > StableConnection)
            {
                attempt = 0;
                ticketRejections = 0;
            }

            switch (failure)
            {
                case TransportClosedException { Code: GatewayCloseCodes.RequestGuard }:
                    SetPhase(new ConnectionFailed(
                        "The gateway refused the connection (Host/Origin guard, 4403). Use the exact dashboard address."));
                    return;
                case TransportClosedException { Code: GatewayCloseCodes.TicketRejected }:
                    if (++ticketRejections > 2)
                    {
                        SetPhase(new ConnectionSessionExpired());
                        return;
                    }
                    await BackoffAsync(Math.Max(attempt, 1), "Ticket rejected; minting a fresh one", ct).ConfigureAwait(false);
                    break;
                default:
                    await BackoffAsync(Math.Max(attempt, 1), failure?.Message ?? "Connection failed", ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task BackoffAsync(int attempt, string reason, CancellationToken ct)
    {
        var idx = Math.Clamp(attempt - 1, 0, BackoffSchedule.Length - 1);
        var delay = BackoffSchedule[idx];
        var retryAt = _clock() + delay;
        SetPhase(new ConnectionReconnecting(attempt, retryAt, reason));
        var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _wake = wake;
        try
        {
            var delayTask = _delay(delay, ct);
            var completed = await Task.WhenAny(delayTask, wake.Task).ConfigureAwait(false);
            if (completed == delayTask) await delayTask.ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_wake, wake)) _wake = null;
        }
    }

    private void SetPhase(ConnectionPhase phase)
    {
        lock (_gate) _phase = phase;
        PhaseChanged?.Invoke(phase);
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _loopCts?.Dispose();
    }
}
