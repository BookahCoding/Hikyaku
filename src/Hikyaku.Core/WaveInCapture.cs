namespace Hikyaku.Core;

/// <summary>
/// Windows waveIn capture via injectable <see cref="IWaveInBackend"/> / <see cref="ICaptureClock"/>.
/// Production defaults to winmm + wall clock. Automated tests inject fakes — never real mic.
/// Real device / permission acceptance remains pending.
/// </summary>
public sealed class WindowsWaveInCapture : IMicrophoneCapture
{
    private readonly IWaveInBackend _backend;
    private readonly ICaptureClock _clock;
    private readonly object _gate = new();
    private bool _capturing;
    private bool _disposed;
    private bool _finish;
    private bool _cancel;
    private int _sessionId;
    private int _handle;
    private WaveInBufferToken[]? _buffers;
    private MemoryStream? _pcm;
    private VoiceCaptureLimits _limits = VoiceCaptureLimits.Default;
    private Thread? _thread;
    private long _startedAtMs;

    public WindowsWaveInCapture(IWaveInBackend? backend = null, ICaptureClock? clock = null)
    {
        _backend = backend ?? CreateDefaultBackend();
        _clock = clock ?? new WallCaptureClock();
        _backend.DataAvailable += OnData;
        if (_backend is FakeWaveInBackend fake)
            fake.RegisterWatchdogPump(() => { _ = WatchdogOnce(); });
    }

    private static IWaveInBackend CreateDefaultBackend()
    {
        if (!OperatingSystem.IsWindows())
            return new FakeWaveInBackend { FailOnStart = true };
        return new WinMmWaveInBackend();
    }

    public bool IsCapturing { get { lock (_gate) return _capturing; } }

    public event Action<float>? LevelChanged;
    public event Action<VoiceAudioClip>? CaptureCompleted;
    public event Action<MicrophoneCaptureError, string>? CaptureFailed;

    public Task<MicrophoneStartResult> StartAsync(VoiceCaptureLimits limits, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsWindows() && _backend is WinMmWaveInBackend)
            return Task.FromResult(new MicrophoneStartResult(false, MicrophoneCaptureError.Failed, "Windows microphone capture only."));

        lock (_gate)
        {
            if (_capturing)
                return Task.FromResult(new MicrophoneStartResult(false, MicrophoneCaptureError.DeviceBusy, "Microphone is busy."));
        }

        try
        {
            return Task.FromResult(StartCore(limits));
        }
        catch (Exception ex)
        {
            CleanupPartialStartup();
            return Task.FromResult(MapStartException(ex));
        }
    }

    private MicrophoneStartResult StartCore(VoiceCaptureLimits limits)
    {
        // Per-session identity — old watchdog/callbacks must not own the new capture.
        int session;
        lock (_gate)
        {
            session = ++_sessionId;
            _finish = false;
            _cancel = false;
            _limits = limits;
            _pcm = new MemoryStream();
            _startedAtMs = _clock.ElapsedMilliseconds;
            _handle = 0;
            _buffers = null;
        }

        var format = new WaveFormatEx
        {
            wFormatTag = 1,
            nChannels = 1,
            nSamplesPerSec = (uint)limits.SampleRateHz,
            wBitsPerSample = 16,
            nBlockAlign = 2,
            nAvgBytesPerSec = (uint)(limits.SampleRateHz * 2),
            cbSize = 0
        };

        if (_backend.GetNumDevs() <= 0)
            return new MicrophoneStartResult(false, MicrophoneCaptureError.NoDevice, "No microphone device.");

        var mr = _backend.Open(in format, session, out var handle);
        if (mr != 0)
            return new MicrophoneStartResult(false, ClassifyOpenError(mr), DescribeMmError(mr));

        lock (_gate)
        {
            if (session != _sessionId) { _backend.Close(handle); return new MicrophoneStartResult(false, MicrophoneCaptureError.Failed, "Capture superseded."); }
            _handle = handle;
        }

        var buffers = new WaveInBufferToken[2];
        var preparedCount = 0;
        try
        {
            for (var i = 0; i < buffers.Length; i++)
            {
                var buf = _backend.CreateBuffer(Math.Max(320, limits.SampleRateHz / 10 * 2));
                buffers[i] = buf;
                lock (_gate) _buffers = buffers.Take(i + 1).ToArray();
                mr = _backend.PrepareHeader(handle, buf);
                if (mr != 0) throw new InvalidOperationException(DescribeMmError(mr));
                preparedCount++;
                mr = _backend.AddBuffer(handle, buf);
                if (mr != 0) throw new InvalidOperationException(DescribeMmError(mr));
            }
        }
        catch
        {
            _ = preparedCount;
            CleanupPartialStartup();
            throw;
        }

        lock (_gate)
        {
            if (session != _sessionId || _cancel)
            {
                CleanupPartialStartup();
                return new MicrophoneStartResult(false, MicrophoneCaptureError.Failed, "Capture cancelled.");
            }
            _buffers = buffers;
        }

        mr = _backend.Start(handle);
        if (mr != 0)
        {
            CleanupPartialStartup();
            return new MicrophoneStartResult(false, ClassifyOpenError(mr), DescribeMmError(mr));
        }

        lock (_gate)
        {
            if (session != _sessionId || _cancel)
            {
                CleanupPartialStartup();
                return new MicrophoneStartResult(false, MicrophoneCaptureError.Failed, "Capture cancelled.");
            }
            _capturing = true;
        }

        _thread = new Thread(WatchdogLoop) { IsBackground = true, Name = "Hikyaku.WaveInWatch" };
        _thread.Start();
        return new MicrophoneStartResult(true);
    }

    private void WatchdogLoop()
    {
        while (true)
        {
            if (!WatchdogOnce()) return;
            _clock.Sleep(50);
        }
    }

    /// <summary>One watchdog tick — also pumped by <see cref="FakeWaveInBackend"/> in tests.</summary>
    public bool WatchdogOnce()
    {
        int session;
        bool capturing;
        bool finish;
        bool cancel;
        long started;
        int maxMs;
        lock (_gate)
        {
            session = _sessionId;
            capturing = _capturing;
            finish = _finish;
            cancel = _cancel;
            started = _startedAtMs;
            maxMs = _limits.MaxDurationMs;
        }
        if (!capturing) return false;
        // Wall-clock bound (not received-byte elapsed) so silent capture cannot run forever.
        var wall = _clock.ElapsedMilliseconds - started;
        if (cancel)
        {
            CompleteDiscard(session);
            return false;
        }
        if (finish || wall >= maxMs)
        {
            CompleteKeep(session);
            return false;
        }
        return true;
    }

    private void OnData(WaveInDataEvent ev)
    {
        try
        {
            bool cancel;
            bool finish;
            bool accept;
            int handle;
            lock (_gate)
            {
                accept = _capturing && ev.SessionId == _sessionId;
                cancel = _cancel;
                finish = _finish || !_capturing;
                handle = _handle;
                if (accept && !cancel && ev.BytesRecorded > 0 && _pcm is not null)
                {
                    var n = (int)Math.Min(ev.BytesRecorded, (uint)ev.Buffer.Scratch.Length);
                    if (n > 0)
                    {
                        _pcm.Write(ev.Buffer.Scratch, 0, n);
                        if (_pcm.Length > _limits.MaxBytes)
                            _finish = true;
                    }
                }
            }

            if (ev.BytesRecorded > 0)
                LevelChanged?.Invoke(Math.Min(1f, ev.BytesRecorded / 4000f));

            if (accept && !cancel && !finish && handle != 0)
                _backend.AddBuffer(handle, ev.Buffer);
        }
        catch
        {
            CaptureFailed?.Invoke(MicrophoneCaptureError.Failed, "Microphone read failed.");
        }
    }

    public void Finish()
    {
        lock (_gate) _finish = true;
    }

    public void Cancel()
    {
        lock (_gate) _cancel = true;
    }

    private void CompleteKeep(int session)
    {
        byte[]? wav = null;
        lock (_gate)
        {
            if (!_capturing || session != _sessionId) return;
            _capturing = false;
            if (_pcm is not null)
            {
                var pcm = _pcm.ToArray();
                if (pcm.Length > 0)
                    wav = WavBuilder.FromPcm16Mono(pcm, _limits.SampleRateHz);
            }
        }
        CleanupDevice();
        LevelChanged?.Invoke(0f);
        if (wav is { Length: > 0 })
            CaptureCompleted?.Invoke(new VoiceAudioClip(wav, _limits.CaptureMime));
        else
            CaptureFailed?.Invoke(MicrophoneCaptureError.Failed, "No audio captured.");
    }

    private void CompleteDiscard(int session)
    {
        lock (_gate)
        {
            if (!_capturing || session != _sessionId) return;
            _capturing = false;
            _pcm?.Dispose();
            _pcm = null;
        }
        CleanupDevice();
        LevelChanged?.Invoke(0f);
    }

    private void CleanupPartialStartup()
    {
        CleanupDevice();
        lock (_gate) _capturing = false;
    }

    private void CleanupDevice()
    {
        int handle;
        WaveInBufferToken[]? buffers;
        lock (_gate)
        {
            handle = _handle;
            buffers = _buffers;
            _handle = 0;
            _buffers = null;
            _pcm?.Dispose();
            _pcm = null;
        }
        if (handle == 0 && buffers is null) return;
        try
        {
            if (handle != 0)
            {
                try { _backend.Stop(handle); } catch { /* ignore */ }
                try { _backend.Reset(handle); } catch { /* ignore */ }
            }
            if (buffers is not null)
            {
                foreach (var b in buffers)
                {
                    try { if (handle != 0) _backend.UnprepareHeader(handle, b); } catch { /* ignore */ }
                    try { _backend.FreeBuffer(b); } catch { /* ignore */ }
                }
            }
            if (handle != 0)
                try { _backend.Close(handle); } catch { /* ignore */ }
        }
        catch { /* best-effort */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        try { _thread?.Join(500); } catch { /* ignore */ }
        int session;
        lock (_gate) session = _sessionId;
        CompleteDiscard(session);
        _backend.DataAvailable -= OnData;
    }

    private static MicrophoneStartResult MapStartException(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Contains("access", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("privacy", StringComparison.OrdinalIgnoreCase))
            return new MicrophoneStartResult(false, MicrophoneCaptureError.PermissionDenied, "Microphone permission denied.");
        if (msg.Contains("busy", StringComparison.OrdinalIgnoreCase))
            return new MicrophoneStartResult(false, MicrophoneCaptureError.DeviceBusy, "Microphone is busy.");
        return new MicrophoneStartResult(false, MicrophoneCaptureError.Failed, msg);
    }

    private static MicrophoneCaptureError ClassifyOpenError(int mr) => mr switch
    {
        4 => MicrophoneCaptureError.DeviceBusy,
        2 => MicrophoneCaptureError.NoDevice,
        6 => MicrophoneCaptureError.NoDevice,
        _ => MicrophoneCaptureError.Failed
    };

    private static string DescribeMmError(int mr) => mr switch
    {
        4 => "Microphone is busy.",
        2 => "No microphone device.",
        6 => "No microphone device.",
        7 => "Out of memory for microphone.",
        _ => $"Microphone error ({mr})."
    };
}
