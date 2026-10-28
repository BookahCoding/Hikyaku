using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hikyaku.Core;

namespace Hikyaku.App;

public partial class MainViewModel
{
    private VoiceCaptureController? _voice;
    private string _voiceStatus = "Microphone is default off. Explicit tap to record.";
    private string _voicePhaseLabel = "Mic: off";
    private string _voiceTranscriptDraft = "";
    private bool _voiceRecording;
    private bool _voiceHasPreview;
    private bool _voiceUnsupported;
    private Visibility _voicePreviewVisibility = Visibility.Collapsed;
    private Brush? _voiceActiveBrush;
    private long _voicePaintedGeneration = -1;
    private int _voicePaintQueued;

    private Dispatcher? _voiceDispatcher;

    public void AttachVoice(VoiceCaptureController controller)
    {
        _voice = controller;
        _voiceDispatcher = Dispatcher.CurrentDispatcher; // the thread that owns this view model, not whichever Application is current
        controller.Changed += OnVoiceChanged;
        ToggleMicCommand = new RelayCommand(_ => _ = ToggleMicAsync(), _ => CanToggleMic());
        StopMicCommand = new RelayCommand(_ => StopMic(), _ => _voiceRecording);
        CancelVoicePreviewCommand = new RelayCommand(_ => CancelVoicePreview(), _ => _voiceHasPreview);
        TranscribeVoiceCommand = new RelayCommand(_ => _ = TranscribeVoiceAsync(), _ => _voiceHasPreview && !_voiceUnsupported);
        InsertVoiceTranscriptCommand = new RelayCommand(_ => InsertVoiceTranscript(), _ => !string.IsNullOrWhiteSpace(_voiceTranscriptDraft) || (_voice?.Transcript?.Length > 0));
        PlayVoicePreviewCommand = new RelayCommand(_ => _ = PlayVoicePreviewAsync(), _ => _voiceHasPreview);
        StopVoicePlaybackCommand = new RelayCommand(_ => _voice?.StopPlayback());
        ReadAloudMessageCommand = new RelayCommand(p => { if (p is MessageVM m) _ = ReadAloudAsync(m); }, CanReadAloud);
        SyncVoiceScope();
        PaintVoice();
        Raise(nameof(ToggleMicCommand));
        Raise(nameof(StopMicCommand));
        Raise(nameof(CancelVoicePreviewCommand));
        Raise(nameof(TranscribeVoiceCommand));
        Raise(nameof(InsertVoiceTranscriptCommand));
        Raise(nameof(PlayVoicePreviewCommand));
        Raise(nameof(StopVoicePlaybackCommand));
        Raise(nameof(ReadAloudMessageCommand));
    }

    public ICommand? ToggleMicCommand { get; private set; }
    public ICommand? StopMicCommand { get; private set; }
    public ICommand? CancelVoicePreviewCommand { get; private set; }
    public ICommand? TranscribeVoiceCommand { get; private set; }
    public ICommand? InsertVoiceTranscriptCommand { get; private set; }
    public ICommand? PlayVoicePreviewCommand { get; private set; }
    public ICommand? StopVoicePlaybackCommand { get; private set; }
    public ICommand? ReadAloudMessageCommand { get; private set; }

    private MessageVM? _readingMessage;

    private bool CanReadAloud(object? p) =>
        p is MessageVM m && _voice is not null && m.ReadAloudVisibility == Visibility.Visible
        && (m.IsReading || (!_busy && SelectedConversation?.History.Contains(m) == true));

    private void SetReadingMessage(MessageVM? m)
    {
        if (ReferenceEquals(_readingMessage, m)) return;
        if (_readingMessage is not null) _readingMessage.IsReading = false;
        _readingMessage = m;
        if (m is not null) m.IsReading = true;
    }

    /// <summary>Explicit per-message gesture: sends this reply's text to the gateway's text-to-speech and plays the audio here.</summary>
    private async Task ReadAloudAsync(MessageVM m)
    {
        if (_voice is null) return;
        if (ReferenceEquals(_readingMessage, m))
        {
            _voice.StopPlayback();
            SetReadingMessage(null);
            VoiceStatus = "Stopped reading.";
            return;
        }
        if (_readingMessage is not null)
        {
            // End the previous reply's audio first: its end notification must not clear this request's label.
            _voice.StopPlayback();
            SetReadingMessage(null);
        }
        SyncVoiceScope();
        SetReadingMessage(m);
        VoiceStatus = "Reading aloud. The message text is sent to this gateway's text-to-speech.";
        var result = await _voice.ReadAloudAsync(m.Markdown);
        if (result.Ok) return;
        if (ReferenceEquals(_readingMessage, m)) SetReadingMessage(null);
        if (result.Error != VoiceCaptureController.StoppedCopy)
            VoiceStatus = result.Error ?? "Could not read aloud.";
    }

    public string VoiceStatus
    {
        get => _voiceStatus;
        private set { if (_voiceStatus == value) return; _voiceStatus = value; Raise(nameof(VoiceStatus)); Raise(nameof(VoiceStatusVisibility)); }
    }

    /// <summary>
    /// Routine housekeeping lines ("Scope changed — pending audio discarded.", the default hint) are hidden in both views since the
    /// live test (they sat under the chat box all the time); anything about a problem or progress is shown.
    /// </summary>
    public Visibility VoiceStatusVisibility =>
        !IsRoutineVoiceStatus(_voiceStatus) ? Visibility.Visible : Visibility.Collapsed;

    private static bool IsRoutineVoiceStatus(string s) =>
        s.Length == 0 || s.StartsWith("Microphone is default off", StringComparison.Ordinal) || s.StartsWith("Scope changed", StringComparison.Ordinal)
        || s.StartsWith("Microphone closed", StringComparison.Ordinal) || s.StartsWith("Cancelled", StringComparison.Ordinal) || s.StartsWith("Stopped reading", StringComparison.Ordinal);

    public string VoicePhaseLabel
    {
        get => _voicePhaseLabel;
        private set { if (_voicePhaseLabel == value) return; _voicePhaseLabel = value; Raise(nameof(VoicePhaseLabel)); }
    }

    public string VoiceTranscriptDraft
    {
        get => _voiceTranscriptDraft;
        set
        {
            if (_voiceTranscriptDraft == value) return;
            _voiceTranscriptDraft = value;
            _voice?.EditTranscript(value);
            Raise(nameof(VoiceTranscriptDraft));
            ((RelayCommand?)InsertVoiceTranscriptCommand)?.Refresh();
        }
    }

    public bool VoiceRecording
    {
        get => _voiceRecording;
        private set { if (_voiceRecording == value) return; _voiceRecording = value; Raise(nameof(VoiceRecording)); Raise(nameof(VoiceActiveIndicatorVisibility)); Raise(nameof(MicIdleVisibility)); Raise(nameof(MicRecordingVisibility)); }
    }

    public bool VoiceHasPreview
    {
        get => _voiceHasPreview;
        private set
        {
            if (_voiceHasPreview == value) return;
            _voiceHasPreview = value;
            Raise(nameof(VoiceHasPreview));
            VoicePreviewVisibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public Visibility VoicePreviewVisibility
    {
        get => _voicePreviewVisibility;
        private set { if (_voicePreviewVisibility == value) return; _voicePreviewVisibility = value; Raise(nameof(VoicePreviewVisibility)); }
    }

    public Visibility VoiceActiveIndicatorVisibility =>
        VoiceRecording ? Visibility.Visible : Visibility.Collapsed;

    public Brush VoiceActiveBrush =>
        _voiceActiveBrush ??= new SolidColorBrush(Color.FromRgb(0xC4, 0x3E, 0x3E));

    public bool VoiceUnsupported
    {
        get => _voiceUnsupported;
        private set { if (_voiceUnsupported == value) return; _voiceUnsupported = value; Raise(nameof(VoiceUnsupported)); }
    }

    internal void SyncVoiceScope()
    {
        if (_voice is null) return;
        var convo = SelectedConversation;
        if (convo is null)
        {
            _voice.BindScope(null);
            ClearVoiceDraftLocal();
            _voicePaintedGeneration = _voice.Generation;
            PaintVoice();
            return;
        }
        var chatId = convo.SessionId
            ?? convo.StoredSessionId
            ?? ("local:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(convo));
        _voice.BindScope(new VoiceScope(convo.Profile, chatId));
        // BindScope discards controller audio/transcript; VM draft must not survive into the new chat.
        ClearVoiceDraftLocal();
        _voicePaintedGeneration = _voice.Generation;
        _ = _voice.RefreshCapabilityAsync();
        PaintVoice();
    }

    private void ClearVoiceDraftLocal()
    {
        if (_voiceTranscriptDraft.Length == 0) return;
        _voiceTranscriptDraft = "";
        Raise(nameof(VoiceTranscriptDraft));
        ((RelayCommand?)InsertVoiceTranscriptCommand)?.Refresh();
    }

    private bool CanToggleMic()
    {
        if (_voice is null || SelectedConversation is null) return false;
        if (_voice.Phase == VoicePhase.Unsupported) return false;
        if (_voice.Phase == VoicePhase.Transcribing) return false;
        return true;
    }

    private async Task ToggleMicAsync()
    {
        if (_voice is null) return;
        SyncVoiceScope();
        if (_voice.IsRecording)
        {
            StopMic();
            return;
        }
        if (_voice.HasPreview)
        {
            // Second tap while previewing does not auto-restart — user must cancel or insert.
            VoiceStatus = "Preview pending — cancel, insert to composer, or stop playback first.";
            return;
        }
        var result = await _voice.StartCaptureAsync(userGesture: true);
        if (!result.Ok)
            VoiceStatus = result.Error ?? "Could not start microphone.";
        PaintVoice();
    }

    private void StopMic()
    {
        if (_voice is null) return;
        var result = _voice.StopCapture();
        if (!result.Ok && result.Error is not null)
            VoiceStatus = result.Error;
        PaintVoice();
    }

    private void CancelVoicePreview()
    {
        _voice?.CancelPreview();
        VoiceTranscriptDraft = "";
        PaintVoice();
    }

    private async Task TranscribeVoiceAsync()
    {
        if (_voice is null) return;
        var result = await _voice.TranscribePreviewAsync();
        if (!result.Ok)
            VoiceStatus = result.Error ?? "Transcription failed.";
        else
            VoiceTranscriptDraft = _voice.Transcript ?? "";
        PaintVoice();
    }

    private void InsertVoiceTranscript()
    {
        if (_voice is null) return;
        // Refuse stale draft belonging to a previous scope generation.
        if (_voice.Generation != _voicePaintedGeneration && string.IsNullOrWhiteSpace(_voice.Transcript))
        {
            ClearVoiceDraftLocal();
            VoiceStatus = "Transcript discarded — chat or profile changed.";
            PaintVoice();
            return;
        }
        // Prefer edited draft; sync into controller first.
        if (!string.IsNullOrWhiteSpace(VoiceTranscriptDraft))
            _voice.EditTranscript(VoiceTranscriptDraft);
        var text = _voice.TakeTranscriptForComposer();
        if (string.IsNullOrWhiteSpace(text))
        {
            VoiceStatus = "No transcript to insert. Transcribe first, or type in the preview box.";
            return;
        }
        InputText = string.IsNullOrWhiteSpace(InputText) ? text : InputText.TrimEnd() + " " + text;
        ClearVoiceDraftLocal();
        VoiceStatus = "Transcript in composer — review, then use Send (not auto-sent).";
        PaintVoice();
    }

    private async Task PlayVoicePreviewAsync()
    {
        if (_voice is null) return;
        var result = await _voice.PlayPreviewAsync();
        if (!result.Ok)
            VoiceStatus = result.Error ?? "Playback failed.";
    }

    private void OnVoiceChanged()
    {
        var dispatcher = _voiceDispatcher ?? Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            PaintVoice();
            return;
        }
        // Non-blocking coalesced paint — never Dispatcher.Invoke (lock inversion / UI stall risk).
        if (Interlocked.Exchange(ref _voicePaintQueued, 1) == 1) return;
        _ = dispatcher.BeginInvoke(DispatcherPriority.DataBind, () =>
        {
            Interlocked.Exchange(ref _voicePaintQueued, 0);
            PaintVoice();
        });
    }

    private void PaintVoice()
    {
        if (_voice is null) return;
        var gen = _voice.Generation;
        if (gen != _voicePaintedGeneration)
        {
            _voicePaintedGeneration = gen;
            // New ownership generation: drop any surviving VM draft; adopt controller transcript only.
            var owned = _voice.Transcript ?? "";
            if (!string.Equals(_voiceTranscriptDraft, owned, StringComparison.Ordinal))
            {
                _voiceTranscriptDraft = owned;
                Raise(nameof(VoiceTranscriptDraft));
            }
        }
        else if (_voice.Transcript is { Length: > 0 } t && string.IsNullOrEmpty(VoiceTranscriptDraft))
        {
            VoiceTranscriptDraft = t;
        }

        if (!_voice.IsReading && _readingMessage is not null) SetReadingMessage(null);
        VoiceRecording = _voice.IsRecording;
        VoiceHasPreview = _voice.HasPreview;
        VoiceUnsupported = _voice.Phase == VoicePhase.Unsupported
            || _voice.Capability == VoiceTransportCapability.Unsupported;
        if (_voice.Status is { Length: > 0 } s)
            VoiceStatus = s;
        VoicePhaseLabel = _voice.Phase switch
        {
            VoicePhase.Recording => "Mic: recording",
            VoicePhase.Preview => "Mic: preview",
            VoicePhase.Transcribing => "Mic: transcribing",
            VoicePhase.PermissionDenied => "Mic: permission denied",
            VoicePhase.Unsupported => "Mic: unavailable",
            VoicePhase.Error => "Mic: error",
            _ => VoiceUnsupported ? "Mic: unavailable" : "Mic: off"
        };
        ((RelayCommand?)ToggleMicCommand)?.Refresh();
        ((RelayCommand?)StopMicCommand)?.Refresh();
        ((RelayCommand?)CancelVoicePreviewCommand)?.Refresh();
        ((RelayCommand?)TranscribeVoiceCommand)?.Refresh();
        ((RelayCommand?)InsertVoiceTranscriptCommand)?.Refresh();
        ((RelayCommand?)PlayVoicePreviewCommand)?.Refresh();
    }

    /// <summary>Synthetic UI acceptance — fixture mic only, never real hardware.</summary>
    public async Task AcceptanceRunVoiceFixtureAsync()
    {
        if (_voice is null) return;
        SyncVoiceScope();
        await _voice.RefreshCapabilityAsync();
        if (_voice.Capability == VoiceTransportCapability.Unsupported)
        {
            VoiceStatus = VoiceCaptureController.UnsupportedCopy;
            PaintVoice();
            return;
        }
        var started = await _voice.StartCaptureAsync(userGesture: true);
        if (!started.Ok)
        {
            VoiceStatus = started.Error ?? "fixture mic start failed";
            PaintVoice();
            return;
        }
        _voice.StopCapture();
        await _voice.TranscribePreviewAsync();
        VoiceTranscriptDraft = _voice.Transcript ?? VoiceTranscriptDraft;
        InsertVoiceTranscript();
        PaintVoice();
    }

    public void ShutdownVoice()
    {
        if (_voice is null) return;
        _voice.Changed -= OnVoiceChanged;
        _voice.Dispose();
        _voice = null;
        VoiceRecording = false;
        VoiceHasPreview = false;
        VoiceTranscriptDraft = "";
        VoicePhaseLabel = "Mic: off";
        VoiceStatus = "Microphone closed.";
    }
}
