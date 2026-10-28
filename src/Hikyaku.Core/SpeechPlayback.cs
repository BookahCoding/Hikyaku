namespace Hikyaku.Core;

public interface ISpeechPlayer : IDisposable
{
    Task PlayAsync(SpokenAudio audio, CancellationToken ct = default);
    void Stop();
    /// <summary>Raised when playback of the current audio finished or was stopped (not for a stale, replaced session).</summary>
    event Action? Ended;
}

public sealed class FakeSpeechPlayer : ISpeechPlayer
{
    private readonly object _gate = new();
    private CancellationTokenSource? _holdCts;
    private long _sessionId;
    private bool _idle = true;

    public event Action? Ended;
    public int PlayCount { get; private set; }
    public int StopCount { get; private set; }
    public string? LastMime { get; private set; }
    public bool FailNext { get; set; }
    public int HoldMs { get; set; }
    public long CurrentSessionId { get { lock (_gate) return _sessionId; } }
    public bool IsIdle { get { lock (_gate) return _idle; } }

    public async Task PlayAsync(SpokenAudio audio, CancellationToken ct = default)
    {
        if (!SpeechPlaybackPolicy.IsSupportedMime(audio.MimeType))
            throw new InvalidOperationException("Unsupported audio format for playback.");
        if (FailNext)
        {
            FailNext = false;
            throw new InvalidOperationException("Corrupt or unreadable audio.");
        }

        ct.ThrowIfCancellationRequested();
        long session;
        lock (_gate)
        {
            _sessionId++;
            session = _sessionId;
            _idle = false;
            PlayCount++;
            LastMime = audio.MimeType;
            _holdCts?.Cancel();
            _holdCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        if (HoldMs > 0)
        {
            try
            {
                await Task.Delay(HoldMs, _holdCts!.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var ended = false;
                lock (_gate)
                {
                    if (session == _sessionId)
                    {
                        ended = !_idle; // Stop() already announced the end
                        _idle = true;
                        if (ended) StopCount++;
                    }
                }
                if (ended) Ended?.Invoke();
                throw;
            }
        }

        var finished = false;
        lock (_gate)
        {
            if (session == _sessionId && !_idle)
            {
                _idle = true;
                finished = true;
            }
        }
        if (finished) Ended?.Invoke();
    }

    /// <summary>Simulate MediaEnded for an older session — must not stop a newer play.</summary>
    public void SimulateMediaEnded(long endedSessionId)
    {
        lock (_gate)
        {
            if (endedSessionId != _sessionId) return;
            _idle = true;
            StopCount++;
        }
        Ended?.Invoke();
    }

    public void Stop()
    {
        bool wasPlaying;
        lock (_gate)
        {
            wasPlaying = !_idle;
            _holdCts?.Cancel();
            _idle = true;
            StopCount++;
        }
        if (wasPlaying) Ended?.Invoke();
    }

    public void Dispose() => Stop();
}
