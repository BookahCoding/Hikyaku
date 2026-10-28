namespace Hikyaku.Core;

public sealed record MicrophoneStartResult(bool Ok, MicrophoneCaptureError Error = MicrophoneCaptureError.None, string? Message = null);

/// <summary>One-shot microphone capture. Tests use <see cref="FakeMicrophoneCapture"/> — never live hardware in CI.</summary>
public interface IMicrophoneCapture : IDisposable
{
    bool IsCapturing { get; }
    event Action<float>? LevelChanged;
    event Action<VoiceAudioClip>? CaptureCompleted;
    event Action<MicrophoneCaptureError, string>? CaptureFailed;

    Task<MicrophoneStartResult> StartAsync(VoiceCaptureLimits limits, CancellationToken ct = default);
    /// <summary>Stop and keep bytes (if any).</summary>
    void Finish();
    /// <summary>Stop and discard.</summary>
    void Cancel();
}

/// <summary>Synthetic loopback capture for fixtures — does not open a real microphone.</summary>
public sealed class FakeMicrophoneCapture : IMicrophoneCapture
{
    private readonly object _gate = new();
    private bool _capturing;
    private bool _held;
    private bool _disposed;
    private VoiceCaptureLimits? _limits;
    private TaskCompletionSource<VoiceAudioClip?>? _holdTcs;

    public byte[] NextPcm { get; set; } = new byte[640];
    public MicrophoneCaptureError NextError { get; set; } = MicrophoneCaptureError.None;
    public bool HoldUntilFinish { get; set; }
    public int EmitDurationMs { get; set; }
    public bool IsCapturing { get { lock (_gate) return _capturing; } }

    public event Action<float>? LevelChanged;
    public event Action<VoiceAudioClip>? CaptureCompleted;
    public event Action<MicrophoneCaptureError, string>? CaptureFailed;

    public Task<MicrophoneStartResult> StartAsync(VoiceCaptureLimits limits, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (NextError != MicrophoneCaptureError.None)
        {
            var msg = NextError switch
            {
                MicrophoneCaptureError.PermissionDenied => "Microphone permission denied.",
                MicrophoneCaptureError.NoDevice => "No microphone device.",
                MicrophoneCaptureError.DeviceBusy => "Microphone is busy.",
                MicrophoneCaptureError.Disconnected => "Microphone disconnected.",
                _ => "Microphone failed."
            };
            return Task.FromResult(new MicrophoneStartResult(false, NextError, msg));
        }

        lock (_gate)
        {
            if (_capturing) return Task.FromResult(new MicrophoneStartResult(false, MicrophoneCaptureError.DeviceBusy, "Already capturing."));
            _capturing = true;
            _limits = limits;
            _held = HoldUntilFinish;
            if (_held) _holdTcs = new TaskCompletionSource<VoiceAudioClip?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        LevelChanged?.Invoke(0.2f);
        return Task.FromResult(new MicrophoneStartResult(true));
    }

    public void Finish()
    {
        VoiceAudioClip? clip = null;
        TaskCompletionSource<VoiceAudioClip?>? hold = null;
        lock (_gate)
        {
            if (!_capturing) return;
            if (_held)
            {
                hold = _holdTcs;
                // Stay capturing until ReleaseHeldCapture — Finish only signals keep.
                return;
            }
            _capturing = false;
            var limits = _limits ?? VoiceCaptureLimits.Default;
            var duration = EmitDurationMs > 0 ? EmitDurationMs : 0;
            if (duration > limits.MaxDurationMs)
            {
                // Still produce clip; controller enforces duration stop separately via Pump.
            }
            var wav = WavBuilder.FromPcm16Mono(NextPcm, limits.SampleRateHz);
            clip = new VoiceAudioClip(wav, limits.CaptureMime);
        }
        if (clip is not null)
        {
            LevelChanged?.Invoke(0f);
            CaptureCompleted?.Invoke(clip);
        }
        hold?.TrySetResult(null);
    }

    public void Cancel()
    {
        TaskCompletionSource<VoiceAudioClip?>? hold;
        lock (_gate)
        {
            if (!_capturing) return;
            _capturing = false;
            hold = _holdTcs;
            _holdTcs = null;
            _held = false;
        }
        hold?.TrySetResult(null);
        LevelChanged?.Invoke(0f);
    }

    /// <summary>Complete a held capture (tests for late callbacks after scope switch).</summary>
    public void ReleaseHeldCapture(byte[]? wavOrNull)
    {
        TaskCompletionSource<VoiceAudioClip?>? hold;
        lock (_gate)
        {
            hold = _holdTcs;
            _holdTcs = null;
            _held = false;
            _capturing = false;
        }
        if (wavOrNull is { Length: > 0 })
            CaptureCompleted?.Invoke(new VoiceAudioClip(wavOrNull, "audio/wav"));
        hold?.TrySetResult(wavOrNull is null ? null : new VoiceAudioClip(wavOrNull, "audio/wav"));
        LevelChanged?.Invoke(0f);
    }

    /// <summary>Simulate disconnect/failure mid-capture (fixtures).</summary>
    public void FailCapture(MicrophoneCaptureError error, string message)
    {
        lock (_gate)
        {
            if (!_capturing) return;
            _capturing = false;
            _held = false;
            _holdTcs?.TrySetResult(null);
            _holdTcs = null;
        }
        CaptureFailed?.Invoke(error, message);
        LevelChanged?.Invoke(0f);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
    }
}

// WindowsWaveInCapture lives in WaveInCapture.cs (injectable native/clock seam).
