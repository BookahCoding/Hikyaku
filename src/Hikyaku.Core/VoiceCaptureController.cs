namespace Hikyaku.Core;

/// <summary>
/// Explicit opt-in voice capture: gesture → record → preview → cancel/transcribe → composer text.
/// Bound to gateway/profile/chat generation. Never auto-sends. No background recording.
/// </summary>
public sealed class VoiceCaptureController : IDisposable
{
    private readonly IMicrophoneCapture _mic;
    private readonly IAudioTransport _transport;
    private readonly ISpeechPlayer _player;
    private readonly VoiceCaptureLimits _limits;
    private readonly object _gate = new();
    private long _generation;
    private VoiceScope? _scope;
    private VoicePhase _phase = VoicePhase.Off;
    private VoiceAudioClip? _pending;
    private string? _transcript;
    private string? _status;
    private VoiceTransportCapability _capability = VoiceTransportCapability.Unknown;
    private float _level;
    private bool _disposed;
    private CancellationTokenSource? _transcribeCts;
    private bool _expectingCapture;
    private bool _reading;
    private CancellationTokenSource? _readCts;

    public VoiceCaptureController(
        IMicrophoneCapture mic,
        IAudioTransport transport,
        ISpeechPlayer player,
        VoiceCaptureLimits? limits = null)
    {
        _mic = mic;
        _transport = transport;
        _player = player;
        _limits = limits ?? VoiceCaptureLimits.Default;
        _mic.CaptureCompleted += OnCaptureCompleted;
        _mic.CaptureFailed += OnCaptureFailed;
        _mic.LevelChanged += OnLevel;
        _player.Ended += OnPlayerEnded;
    }

    public VoicePhase Phase { get { lock (_gate) return _phase; } }
    public bool IsRecording { get { lock (_gate) return _phase == VoicePhase.Recording; } }
    public bool HasPreview { get { lock (_gate) return _pending is not null; } }
    public VoiceAudioClip? PendingAudio { get { lock (_gate) return _pending; } }
    public string? Transcript { get { lock (_gate) return _transcript; } }
    public string? Status { get { lock (_gate) return _status; } }
    public float Level { get { lock (_gate) return _level; } }
    public VoiceTransportCapability Capability { get { lock (_gate) return _capability; } }
    public VoiceScope? Scope { get { lock (_gate) return _scope; } }
    public bool DidAutoSend => false;
    public long Generation { get { lock (_gate) return _generation; } }

    public event Action? Changed;

    public const string UnsupportedCopy =
        "Voice transcription is unavailable on this gateway — no /api/audio/transcribe route. " +
        "Capture UI stays gated until a source-backed audio endpoint is present.";

    public const string SetupRequiredCopy =
        "Voice requires a signed-in Hermes dashboard session for this profile.";

    public const string GestureRequiredCopy =
        "Microphone capture starts only from an explicit user gesture.";

    public void BindScope(VoiceScope? scope)
    {
        long gen;
        lock (_gate)
        {
            if (_scope is { } cur && scope is { } next
                && cur.Profile.GatewayId == next.Profile.GatewayId
                && cur.Profile.ProfileId == next.Profile.ProfileId
                && string.Equals(cur.ChatId, next.ChatId, StringComparison.Ordinal))
                return;

            gen = ++_generation;
            _scope = scope;
            DiscardUnlocked("Scope changed — pending audio discarded.");
        }
        _mic.Cancel();
        StopPlayback();
        CancelTranscribe();
        Raise();
        _ = gen;
    }

    public async Task RefreshCapabilityAsync(CancellationToken ct = default)
    {
        VoiceScope? scope;
        long gen;
        lock (_gate)
        {
            scope = _scope;
            gen = _generation;
        }
        if (scope is null)
        {
            lock (_gate)
            {
                _capability = VoiceTransportCapability.Unknown;
                _status = "Select a chat to use the microphone.";
                if (_phase is not VoicePhase.Recording and not VoicePhase.Preview and not VoicePhase.Transcribing)
                    _phase = VoicePhase.Off;
            }
            Raise();
            return;
        }

        var cap = await _transport.ProbeAsync(scope.Profile.ProfileId, ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (gen != _generation) return;
            _capability = cap;
            if (cap == VoiceTransportCapability.Unsupported)
            {
                DiscardUnlocked(UnsupportedCopy);
                _phase = VoicePhase.Unsupported;
            }
            else if (cap == VoiceTransportCapability.SetupRequired)
            {
                _status = SetupRequiredCopy;
                if (_phase is VoicePhase.Off or VoicePhase.Unsupported or VoicePhase.Error)
                    _phase = VoicePhase.Off;
            }
            else
            {
                if (_phase == VoicePhase.Unsupported)
                    _phase = VoicePhase.Off;
                _status = null;
            }
        }
        Raise();
    }

    public async Task<VoiceActionResult> StartCaptureAsync(bool userGesture, CancellationToken ct = default)
    {
        if (_disposed) return new VoiceActionResult(false, "Voice controller disposed.");
        if (!userGesture) return new VoiceActionResult(false, GestureRequiredCopy);

        VoiceScope scope;
        long gen;
        lock (_gate)
        {
            if (_scope is null)
                return new VoiceActionResult(false, "Select a chat before using the microphone.");
            if (_capability == VoiceTransportCapability.Unsupported)
                return new VoiceActionResult(false, UnsupportedCopy);
            if (_capability == VoiceTransportCapability.SetupRequired)
                return new VoiceActionResult(false, SetupRequiredCopy);
            if (_phase is VoicePhase.Recording or VoicePhase.Transcribing)
                return new VoiceActionResult(false, "Already capturing.");
            scope = _scope;
            DiscardUnlocked(null);
            _player.Stop();
            gen = _generation;
            _expectingCapture = true;
            _phase = VoicePhase.Recording;
            _status = "Recording — tap stop when finished.";
        }
        Raise();

        var started = await _mic.StartAsync(_limits, ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (gen != _generation || !_expectingCapture)
            {
                _mic.Cancel();
                return new VoiceActionResult(false, "Capture cancelled — profile or chat changed.");
            }
            if (!started.Ok)
            {
                _expectingCapture = false;
                _phase = started.Error == MicrophoneCaptureError.PermissionDenied
                    ? VoicePhase.PermissionDenied
                    : VoicePhase.Error;
                _status = started.Message ?? "Microphone failed.";
                return new VoiceActionResult(false, _status);
            }
        }
        Raise();
        return new VoiceActionResult(true);
    }

    public VoiceActionResult StopCapture()
    {
        long gen;
        lock (_gate)
        {
            if (_phase != VoicePhase.Recording)
                return new VoiceActionResult(false, "Not recording.");
            gen = _generation;
        }
        _mic.Finish();
        // Fake mic completes synchronously; Windows completes via callback.
        lock (_gate)
        {
            if (gen != _generation) return new VoiceActionResult(false, "Capture cancelled — scope changed.");
            if (_phase == VoicePhase.Recording && _pending is null && !_mic.IsCapturing)
            {
                // Wait for CaptureCompleted; Fake already fired.
            }
            if (_pending is { } clip)
            {
                if (clip.Bytes.LongLength > _limits.MaxBytes)
                {
                    DiscardUnlocked("Recording exceeds size limit.");
                    return new VoiceActionResult(false, "Recording exceeds size limit.");
                }
                _phase = VoicePhase.Preview;
                _status = "Preview — transcribe, play, send to composer, or cancel.";
                _expectingCapture = false;
            }
        }
        Raise();
        lock (_gate)
        {
            if (_pending is null && _phase == VoicePhase.Recording)
            {
                // Held fake or async Windows — stay recording until callback; mark stop requested via Finish.
                _status = "Finishing capture…";
            }
            if (_pending is null && _phase != VoicePhase.Preview && !_mic.IsCapturing && _expectingCapture == false)
            {
                // completed without pending → error already handled
            }
            return _pending is not null
                ? new VoiceActionResult(true)
                : _phase == VoicePhase.Recording
                    ? new VoiceActionResult(true) // async finish in flight
                    : new VoiceActionResult(_phase == VoicePhase.Preview, _status);
        }
    }

    /// <summary>Fake mic duration pump — tests only; production uses watchdog inside capture.</summary>
    public Task PumpCaptureAsync()
    {
        if (_mic is FakeMicrophoneCapture fake && fake.EmitDurationMs >= _limits.MaxDurationMs && fake.IsCapturing)
        {
            _mic.Finish();
            lock (_gate)
            {
                if (_pending is not null)
                {
                    _phase = VoicePhase.Preview;
                    _status = "Max duration reached — preview ready.";
                    _expectingCapture = false;
                }
            }
            Raise();
        }
        return Task.CompletedTask;
    }

    public void CancelPreview()
    {
        CancelTranscribe();
        _player.Stop();
        _mic.Cancel();
        lock (_gate) DiscardUnlocked("Cancelled — audio discarded.");
        Raise();
    }

    public void EditTranscript(string text)
    {
        lock (_gate)
        {
            if (_phase is not VoicePhase.Preview and not VoicePhase.Transcribing and not VoicePhase.Off)
                return;
            _transcript = text;
        }
        Raise();
    }

    public async Task<VoiceActionResult> TranscribePreviewAsync(CancellationToken ct = default)
    {
        VoiceAudioClip clip;
        VoiceScope scope;
        long gen;
        lock (_gate)
        {
            if (_pending is null) return new VoiceActionResult(false, "No preview to transcribe.");
            if (_capability == VoiceTransportCapability.Unsupported)
                return new VoiceActionResult(false, UnsupportedCopy);
            clip = _pending;
            scope = _scope ?? throw new InvalidOperationException("No scope");
            gen = _generation;
            _phase = VoicePhase.Transcribing;
            _status = "Transcribing…";
        }
        Raise();

        CancelTranscribe();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _transcribeCts = cts;
        try
        {
            var text = await _transport.TranscribeAsync(clip.Bytes, clip.MimeType, scope.Profile.ProfileId, cts.Token)
                .ConfigureAwait(false);
            lock (_gate)
            {
                if (gen != _generation || _pending is null)
                    return new VoiceActionResult(false, "Transcription discarded — scope changed or cancelled.");
                _transcript = text;
                _phase = VoicePhase.Preview;
                _status = "Transcript ready — edit, then insert into composer (Send is separate).";
            }
            Raise();
            return new VoiceActionResult(true);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (gen == _generation && _phase == VoicePhase.Transcribing)
                {
                    _phase = VoicePhase.Preview;
                    _status = "Transcription cancelled.";
                }
            }
            Raise();
            return new VoiceActionResult(false, "Transcription cancelled.");
        }
        catch (AudioTransportUnsupportedException)
        {
            // The transcribe route itself is absent: the authoritative "unsupported" signal (status probe is not).
            lock (_gate)
            {
                if (gen != _generation) return new VoiceActionResult(false, "Transcription discarded.");
                _capability = VoiceTransportCapability.Unsupported;
                DiscardUnlocked(UnsupportedCopy);
                _phase = VoicePhase.Unsupported;
            }
            Raise();
            return new VoiceActionResult(false, UnsupportedCopy);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (gen != _generation) return new VoiceActionResult(false, "Transcription discarded.");
                _phase = VoicePhase.Preview;
                _status = ex.Message;
            }
            Raise();
            return new VoiceActionResult(false, ex.Message);
        }
    }

    /// <summary>Moves editable transcript into composer; discards raw audio. Does not Send.</summary>
    public string? TakeTranscriptForComposer()
    {
        string? text;
        lock (_gate)
        {
            text = _transcript;
            _pending = null;
            _transcript = null;
            _phase = VoicePhase.Off;
            _status = text is null ? null : "Transcript in composer — review, then Send.";
        }
        _player.Stop();
        Raise();
        return text;
    }

    public async Task<VoiceActionResult> PlayPreviewAsync(CancellationToken ct = default)
    {
        VoiceAudioClip clip;
        lock (_gate)
        {
            if (_pending is null) return new VoiceActionResult(false, "No preview to play.");
            clip = _pending;
        }
        try
        {
            await _player.PlayAsync(new SpokenAudio(clip.Bytes, clip.MimeType), ct).ConfigureAwait(false);
            return new VoiceActionResult(true);
        }
        catch (Exception ex)
        {
            return new VoiceActionResult(false, ex.Message);
        }
    }

    public async Task<VoiceActionResult> PlayReplyAsync(SpokenAudio audio, CancellationToken ct = default)
    {
        if (!SpeechPlaybackPolicy.IsSupportedMime(audio.MimeType))
            return new VoiceActionResult(false, "Unsupported audio format for playback.");
        try
        {
            await _player.PlayAsync(audio, ct).ConfigureAwait(false);
            return new VoiceActionResult(true);
        }
        catch (Exception ex)
        {
            return new VoiceActionResult(false, ex.Message);
        }
    }

    /// <summary>True from the moment a read-aloud request starts until the audio ends or is stopped.</summary>
    public bool IsReading { get { lock (_gate) return _reading; } }

    /// <summary>Result text when a read was stopped, replaced or discarded; not an error to show.</summary>
    public const string StoppedCopy = "Stopped.";

    public const string ReadUnsupportedCopy =
        "This gateway has no text-to-speech route, so replies cannot be read aloud.";

    /// <summary>
    /// Reads one reply aloud: explicit gesture only. The reply text (prepared by <see cref="SpeechText"/>)
    /// is sent to the gateway's text-to-speech for the bound profile; the audio is played locally and not kept.
    /// A chat or profile switch, a stop, or a newer request discards a late answer.
    /// </summary>
    public async Task<VoiceActionResult> ReadAloudAsync(string markdown, CancellationToken ct = default)
    {
        VoiceScope? scope;
        long gen;
        lock (_gate) { scope = _scope; gen = _generation; }
        if (scope is null) return new VoiceActionResult(false, "Open a chat first.");

        var prepared = SpeechText.Prepare(markdown);
        if (prepared.Problem == SpeechTextProblem.Empty)
            return new VoiceActionResult(false, "There is no text in this message to read aloud.");
        if (prepared.Problem == SpeechTextProblem.TooLong)
            return new VoiceActionResult(false, "This message is too long to read aloud.");

        // Stop whatever is speaking first: its Ended notification must not clear the new request's state.
        CancellationTokenSource? previous;
        lock (_gate) { previous = _readCts; }
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
        _player.Stop();

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed) return new VoiceActionResult(false, "Voice is closed.");
            cts = _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _reading = true;
        }
        Raise();

        try
        {
            var audio = await _transport.SpeakAsync(prepared.Text, scope.Profile.ProfileId, cts.Token).ConfigureAwait(false);
            if (cts.IsCancellationRequested || !IsCurrentRead(cts, gen))
                return new VoiceActionResult(false, StoppedCopy);
            if (!SpeechPlaybackPolicy.IsSupportedMime(audio.MimeType))
            {
                FinishRead(cts);
                return new VoiceActionResult(false, "The gateway returned audio in a format this app cannot play.");
            }
            if (audio.Bytes.Length == 0)
            {
                FinishRead(cts);
                return new VoiceActionResult(false, "The gateway returned no audio.");
            }
            await _player.PlayAsync(audio, cts.Token).ConfigureAwait(false);
            return new VoiceActionResult(true);
        }
        catch (OperationCanceledException)
        {
            FinishRead(cts);
            return new VoiceActionResult(false, StoppedCopy);
        }
        catch (AudioTransportUnsupportedException)
        {
            FinishRead(cts);
            return new VoiceActionResult(false, ReadUnsupportedCopy);
        }
        catch (Exception ex)
        {
            FinishRead(cts);
            return new VoiceActionResult(false, "Could not read aloud: " + ex.Message);
        }
    }

    private bool IsCurrentRead(CancellationTokenSource cts, long gen)
    {
        lock (_gate) return ReferenceEquals(_readCts, cts) && _generation == gen && !_disposed;
    }

    private void FinishRead(CancellationTokenSource cts)
    {
        var changed = false;
        lock (_gate)
        {
            if (ReferenceEquals(_readCts, cts)) { _readCts = null; if (_reading) { _reading = false; changed = true; } }
        }
        if (changed) Raise();
    }

    private void OnPlayerEnded()
    {
        var changed = false;
        lock (_gate)
        {
            _readCts = null;
            if (_reading) { _reading = false; changed = true; }
        }
        if (changed) Raise();
    }

    public void StopPlayback()
    {
        CancellationTokenSource? cts;
        lock (_gate) { cts = _readCts; _readCts = null; }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        _player.Stop();
        bool changed;
        lock (_gate) { changed = _reading; _reading = false; }
        if (changed) Raise();
    }

    private void OnCaptureCompleted(VoiceAudioClip clip)
    {
        var raise = false;
        lock (_gate)
        {
            if (!_expectingCapture)
                return; // late callback after cancel/switch
            _expectingCapture = false;
            if (clip.Bytes.LongLength > _limits.MaxBytes)
            {
                DiscardUnlocked("Recording exceeds size limit.");
                raise = true;
            }
            else
            {
                _pending = clip;
                _phase = VoicePhase.Preview;
                _status = "Preview — transcribe, play, insert to composer, or cancel.";
                raise = true;
            }
        }
        // Never invoke Changed while holding _gate — UI may Dispatcher.Invoke back into Phase getters.
        if (raise) Raise();
    }

    private void OnCaptureFailed(MicrophoneCaptureError error, string message)
    {
        var raise = false;
        lock (_gate)
        {
            if (!_expectingCapture && _phase != VoicePhase.Recording) return;
            _expectingCapture = false;
            _pending = null;
            _phase = error == MicrophoneCaptureError.PermissionDenied
                ? VoicePhase.PermissionDenied
                : VoicePhase.Error;
            _status = message;
            raise = true;
        }
        if (raise) Raise();
    }

    private void OnLevel(float level)
    {
        lock (_gate) _level = level;
        Raise();
    }

    private void DiscardUnlocked(string? status)
    {
        _pending = null;
        _transcript = null;
        _expectingCapture = false;
        _level = 0;
        if (_phase is not VoicePhase.Unsupported)
            _phase = VoicePhase.Off;
        if (status is not null) _status = status;
    }

    private void CancelTranscribe()
    {
        try { _transcribeCts?.Cancel(); } catch { /* ignore */ }
        _transcribeCts?.Dispose();
        _transcribeCts = null;
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelTranscribe();
        StopPlayback();
        _player.Ended -= OnPlayerEnded;
        _mic.CaptureCompleted -= OnCaptureCompleted;
        _mic.CaptureFailed -= OnCaptureFailed;
        _mic.LevelChanged -= OnLevel;
        _mic.Cancel();
        _mic.Dispose();
        _player.Stop();
        _player.Dispose();
        lock (_gate)
        {
            _generation++;
            DiscardUnlocked(null);
            _pending = null;
        }
    }
}
