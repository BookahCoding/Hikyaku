using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Hikyaku.Core;
using Windows.Media.Audio;
using Windows.Media.Core;
using Windows.Media.Render;
using Windows.Storage.Streams;

namespace Hikyaku.App;

/// <summary>
/// Local-only playback through Windows' AudioGraph, from memory (nothing is written to disk). Live finding (0.5.0): WPF's
/// MediaPlayer could not open the gateway's MP3 on the maintainer's PC (0xC00D11B1) and ended without a sound or a word,
/// and WinRT's MediaPlayer refused every format in an unpackaged app (SourceNotSupported), while AudioGraph played MP3, WAV
/// (16-bit and float), OGG/Opus, FLAC and AAC to the end. A clip that cannot be opened fails <see cref="PlayAsync"/>, so the
/// caller can say why. No remote URLs, no shell execution. Cancellation and session ownership are honoured.
/// </summary>
public sealed class WindowsSpeechPlayer : ISpeechPlayer
{
    private readonly object _gate = new();
    private AudioGraph? _graph;
    private MediaSourceAudioInputNode? _input;
    private long _sessionId, _stoppedSession = -1;
    private bool _disposed;
    private CancellationTokenRegistration _ctReg;

    public event Action? Ended;

    public async Task PlayAsync(SpokenAudio audio, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!SpeechPlaybackPolicy.IsSupportedMime(audio.MimeType))
            throw new InvalidOperationException("Unsupported audio format for playback.");
        if (SpeechPlaybackPolicy.AllowsShellExecution)
            throw new InvalidOperationException("Shell playback is forbidden.");
        ct.ThrowIfCancellationRequested();

        Stop();
        long session;
        lock (_gate) session = ++_sessionId;

        var created = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Speech));
        if (created.Graph is not { } graph)
            throw new InvalidOperationException("Windows could not open the sound output (" + created.Status + ").");
        try
        {
            var output = await graph.CreateDeviceOutputNodeAsync();
            if (output.DeviceOutputNode is not { } speaker)
                throw new InvalidOperationException("Windows could not open the speakers (" + output.Status + ").");
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(audio.Bytes.AsBuffer());
            stream.Seek(0);
            var source = MediaSource.CreateFromStream(stream, audio.MimeType.Trim().ToLowerInvariant());
            var opened = await graph.CreateMediaSourceAudioInputNodeAsync(source);
            if (opened.Node is not { } input)
                throw new InvalidOperationException($"Windows could not play this audio ({audio.MimeType}, {opened.Status}).");
            input.AddOutgoingConnection(speaker);
            input.MediaSourceCompleted += (_, _) => StopSession(session);
            graph.UnrecoverableErrorOccurred += (_, _) => StopSession(session);
            lock (_gate)
            {
                if (session != _sessionId || _stoppedSession == session || ct.IsCancellationRequested) { graph.Dispose(); return; }   // stopped meanwhile
                _graph = graph;
                _input = input;
                _ctReg = ct.Register(() => StopSession(session));
            }
            graph.Start();
        }
        catch
        {
            lock (_gate) { if (ReferenceEquals(_graph, graph)) { _graph = null; _input = null; } }
            graph.Dispose();
            throw;
        }
    }

    public void Stop()
    {
        long session;
        lock (_gate) { session = _sessionId; _stoppedSession = session; }   // also stops a clip that is still being set up
        StopSession(session);
    }

    private void StopSession(long session)
    {
        AudioGraph? graph;
        lock (_gate)
        {
            if (session != _sessionId) return;     // a stale end must not stop newer playback
            graph = _graph;
            _graph = null;
            _input = null;
            _ctReg.Dispose();
            _ctReg = default;
        }
        if (graph is null) return;
        try { graph.Stop(); } catch { /* already stopping */ }
        try { graph.Dispose(); } catch { /* ignore */ }
        Ended?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
