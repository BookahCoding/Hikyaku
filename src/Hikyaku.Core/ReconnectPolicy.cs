namespace Hikyaku.Core;

/// <summary>Bounded exponential backoff. Returns null once attempts are exhausted.</summary>
public sealed class ReconnectPolicy(int maxAttempts = 5, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null)
{
    private readonly TimeSpan _base = baseDelay ?? TimeSpan.FromMilliseconds(500);
    private readonly TimeSpan _max = maxDelay ?? TimeSpan.FromSeconds(15);

    public int MaxAttempts => maxAttempts;

    /// <param name="attempt">Zero-based retry number.</param>
    public TimeSpan? DelayFor(int attempt)
    {
        if (attempt < 0 || attempt >= maxAttempts) return null;
        var ms = _base.TotalMilliseconds * Math.Pow(2, attempt);
        return TimeSpan.FromMilliseconds(Math.Min(ms, _max.TotalMilliseconds));
    }

    /// <summary>Runs <paramref name="op"/>, retrying transient failures with backoff; honours cancellation.</summary>
    public async Task RunAsync(Func<CancellationToken, Task> op, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { await op(ct).ConfigureAwait(false); return; }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                ct.ThrowIfCancellationRequested(); // a failure caused by cancelling is a cancellation, not a retry
                var d = DelayFor(attempt);
                if (d is null) throw;
                await delay(d.Value, ct).ConfigureAwait(false);
            }
        }
    }
}
