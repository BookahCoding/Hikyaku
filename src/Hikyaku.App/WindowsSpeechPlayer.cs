using System.IO;
using System.Windows.Media;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>
/// Local-only playback: WAV via in-memory <see cref="System.Media.SoundPlayer"/>;
/// MPEG via ephemeral temp + MediaPlayer (honest policy — delete on stop/cancel/ended of owning session;
/// <see cref="SpeechPlaybackPolicy.AllowsSilentPrivatePersistence"/> is false).
/// No remote URLs, no shell execution. Cancellation and session ownership are honored.
/// </summary>
public sealed class WindowsSpeechPlayer : ISpeechPlayer
{
    private readonly object _gate = new();
    private System.Media.SoundPlayer? _wav;
    private MediaPlayer? _media;
    private string? _tempPath;
    private long _sessionId;
    private bool _disposed;
    private CancellationTokenRegistration _ctReg;

    public event Action? Ended;

    public Task PlayAsync(SpokenAudio audio, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!SpeechPlaybackPolicy.IsSupportedMime(audio.MimeType))
            throw new InvalidOperationException("Unsupported audio format for playback.");
        if (SpeechPlaybackPolicy.AllowsShellExecution)
            throw new InvalidOperationException("Shell playback is forbidden.");
        if (SpeechPlaybackPolicy.AllowsSilentPrivatePersistence)
            throw new InvalidOperationException("Silent private audio persistence is forbidden.");
        ct.ThrowIfCancellationRequested();

        Stop();
        long session;
        lock (_gate)
        {
            _sessionId++;
            session = _sessionId;
            _ctReg = ct.Register(() => StopSession(session));
        }

        var mime = audio.MimeType.Trim().ToLowerInvariant();
        if (mime is "audio/wav" or "audio/wave" or "audio/x-wav")
        {
            var ms = new MemoryStream(audio.Bytes, writable: false);
            var player = new System.Media.SoundPlayer(ms);
            lock (_gate)
            {
                if (session != _sessionId) { player.Dispose(); return Task.CompletedTask; }
                _wav = player;
            }
            // PlaySync on a worker so the end of the clip is known (SoundPlayer.Play has no completion signal).
            _ = Task.Run(() =>
            {
                try { player.PlaySync(); } catch { /* stopped or unplayable */ }
                StopSession(session);
            });
            return Task.CompletedTask;
        }

        if (mime is "audio/mpeg" or "audio/mp3")
        {
            // MediaPlayer is a dispatcher object: it must be created, driven and closed on the UI thread, or MediaEnded never fires and Stop cannot stop it.
            if (System.Windows.Application.Current?.Dispatcher is { } ui && !ui.CheckAccess())
                return ui.InvokeAsync(() => PlayMpeg(audio.Bytes, session)).Task;
            PlayMpeg(audio.Bytes, session);
            return Task.CompletedTask;
        }

        throw new InvalidOperationException("Unsupported audio format for playback.");
    }

    private void PlayMpeg(byte[] bytes, long session)
    {
        {
            // MediaPlayer requires a URI — ephemeral temp only; deleted on stop/cancel/ended for THIS session.
            var path = Path.Combine(Path.GetTempPath(), "hikyaku-voice-" + Guid.NewGuid().ToString("n") + ".mp3");
            File.WriteAllBytes(path, bytes);
            var media = new MediaPlayer();
            media.MediaEnded += (_, _) => StopSession(session);
            media.MediaFailed += (_, _) => StopSession(session);
            lock (_gate)
            {
                if (session != _sessionId)
                {
                    try { media.Close(); } catch { /* ignore */ }
                    TryDelete(path);
                    return;
                }
                _tempPath = path;
                _media = media;
            }
            media.Open(new Uri(path, UriKind.Absolute));
            media.Play();
        }
    }

    public void Stop()
    {
        long session;
        lock (_gate) session = _sessionId;
        StopSession(session);
    }

    private void StopSession(long session)
    {
        System.Media.SoundPlayer? wav;
        MediaPlayer? media;
        string? temp;
        lock (_gate)
        {
            if (session != _sessionId) return; // stale MediaEnded must not stop newer playback
            wav = _wav;
            _wav = null;
            media = _media;
            _media = null;
            temp = _tempPath;
            _tempPath = null;
            _ctReg.Dispose();
            _ctReg = default;
        }
        var wasActive = wav is not null || media is not null;
        try { wav?.Stop(); } catch { /* ignore */ }
        try { wav?.Dispose(); } catch { /* ignore */ }
        if (media is not null)
        {
            void Release() { try { media.Stop(); } catch { /* ignore */ } try { media.Close(); } catch { /* ignore */ } }
            if (media.Dispatcher.CheckAccess()) Release();
            else media.Dispatcher.BeginInvoke(Release);                       // a dispatcher object is only touched on its own thread
        }
        TryDelete(temp);
        if (wasActive) Ended?.Invoke();
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
