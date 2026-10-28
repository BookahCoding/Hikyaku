using System.Text;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>The microphone and speaker of one voice chat (<see cref="WinMmVoiceIo"/> on a real PC).</summary>
public interface IVoiceChatAudio : IDisposable
{
    event Action<short[], long>? MicFrame;
    event Action<bool>? PlayingChanged;
    event Action? SpeechDrained;
    bool IsPlaying { get; }
    void BeginSpeech(int sampleRate, int channels);
    void QueuePcm(byte[] pcm16le);
    void EndSpeech();
    void StopSpeech();
}

/// <summary>What a voice chat needs from the gateway and the PC; <see cref="GatewayVoiceChatBackend"/> is the real one.</summary>
public interface IVoiceChatBackend
{
    (IVoiceChatAudio? Audio, string? Problem) OpenAudio();
    Task<VoiceChatSettings> LoadSettingsAsync(string profile, CancellationToken ct);
    Task<string> TranscribeAsync(byte[] wav, string profile, CancellationToken ct);
    /// <summary>The speak-stream socket, or null when the gateway cannot open one.</summary>
    Task<ISpeechStream?> OpenSpeechAsync(string profile, CancellationToken ct);
    Task<SpokenAudio> SpeakAsync(string text, string profile, CancellationToken ct);
    /// <summary>Plays a whole clip and completes when it has finished (or was stopped).</summary>
    Task PlayClipAsync(SpokenAudio audio, CancellationToken ct);
    void StopClip();
    Task LeaseAsync(bool active, string profile);
}

/// <summary>
/// 0.6.0 — the voice chat in the chat box (spec docs/superpowers/specs/2026-10-28-voice-chat-design.md): a headset button in
/// both views, a bar with a level meter, a plain status word and Stop. <see cref="VoiceChatSession"/> makes every decision;
/// this wires it to the open chat (send, stop, the reply as it grows, Needs-you) and to the gateway's voice.
/// </summary>
public sealed partial class MainViewModel
{
    private Func<IVoiceChatBackend?>? _voiceChatBackend;
    private Dispatcher? _voiceChatDispatcher;
    private VoiceChatRun? _voiceChatRun;
    private string _voiceChatStatus = "", _voiceChatProblem = "";
    private double _voiceChatLevel;

    public ICommand ToggleVoiceChatCommand { get; private set; } = new RelayCommand(_ => { });
    public ICommand DismissVoiceChatProblemCommand => new RelayCommand(_ => VoiceChatProblem = "");

    public void AttachVoiceChat(Func<IVoiceChatBackend?> backend)
    {
        _voiceChatBackend = backend;
        _voiceChatDispatcher = Dispatcher.CurrentDispatcher;
        ToggleVoiceChatCommand = new RelayCommand(_ => ToggleVoiceChat());
        Raise(nameof(ToggleVoiceChatCommand));
        Raise(nameof(VoiceChatButtonVisibility));
    }

    public bool VoiceChatActive => _voiceChatRun is not null;
    public Visibility VoiceChatButtonVisibility =>
        _voiceChatBackend is not null && Shows(ExperienceFeature.VoiceChat) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VoiceChatBarVisibility => VoiceChatActive || _voiceChatProblem.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VoiceChatStopVisibility => VoiceChatActive ? Visibility.Visible : Visibility.Collapsed;
    public string VoiceChatButtonTip => VoiceChatActive ? "End the voice chat (Esc)" : "Talk with Hermes: a hands-free voice chat";

    public string VoiceChatStatus
    {
        get => _voiceChatStatus;
        private set { if (_voiceChatStatus == value) return; _voiceChatStatus = value; Raise(nameof(VoiceChatStatus)); }
    }

    /// <summary>Why the last voice chat ended or could not start, in plain words; empty when nothing went wrong.</summary>
    public string VoiceChatProblem
    {
        get => _voiceChatProblem;
        private set
        {
            if (_voiceChatProblem == value) return;
            _voiceChatProblem = value;
            Raise(nameof(VoiceChatProblem));
            Raise(nameof(VoiceChatProblemVisibility));
            Raise(nameof(VoiceChatBarVisibility));
        }
    }

    public Visibility VoiceChatProblemVisibility => _voiceChatProblem.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public double VoiceChatLevel
    {
        get => _voiceChatLevel;
        private set { if (Math.Abs(_voiceChatLevel - value) < 0.01) return; _voiceChatLevel = value; Raise(nameof(VoiceChatLevel)); }
    }

    private void ToggleVoiceChat()
    {
        if (VoiceChatActive) EndVoiceChat(null);
        else _ = StartVoiceChatAsync();
    }

    private async Task StartVoiceChatAsync()
    {
        var convo = SelectedConversation;
        if (convo is null || VoiceChatActive) return;
        VoiceChatProblem = "";
        var backend = _voiceChatBackend?.Invoke();
        if (backend is null) { VoiceChatProblem = "Connect to your gateway first, then start the voice chat."; return; }
        var (audio, problem) = backend.OpenAudio();
        if (audio is null) { VoiceChatProblem = problem ?? "Hikyaku could not open the microphone."; return; }
        var run = new VoiceChatRun(this, backend, audio, convo);
        _voiceChatRun = run;
        VoiceChatStatus = "Starting…";
        RaiseVoiceChatState();
        var settings = VoiceChatSettings.Defaults;
        try { settings = await backend.LoadSettingsAsync(convo.Profile.ProfileId, run.Token).ConfigureAwait(true); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* the defaults are fine */ }
        catch (OperationCanceledException) { return; }
        if (!ReferenceEquals(_voiceChatRun, run)) return;
        run.Start(settings, ActiveProfile.Label(convo.Profile, _profileNames));
    }

    /// <summary>Ends the voice chat (Stop, Esc, a chat switch, closing the window); the text turn is left running.</summary>
    public void EndVoiceChat(string? why)
    {
        if (_voiceChatRun is not { } run) return;
        if (run.Session is { } session) session.End(why);
        else FinishVoiceChat(run, why);
    }

    private void OnVoiceChatChanged(VoiceChatRun run)
    {
        if (!ReferenceEquals(_voiceChatRun, run) || run.Session is not { } s) return;
        VoiceChatLevel = s.Level;
        VoiceChatStatus = s.Phase switch
        {
            VoiceChatPhase.Listening => "Listening…",
            VoiceChatPhase.Transcribing => "Sending…",
            VoiceChatPhase.Thinking => "Thinking…",
            VoiceChatPhase.Speaking => "Speaking…",
            VoiceChatPhase.WaitingForYou => "Waiting for you: check the screen",
            _ => VoiceChatStatus
        };
        if (s.Phase == VoiceChatPhase.Ended) FinishVoiceChat(run, s.Problem);
    }

    private void FinishVoiceChat(VoiceChatRun run, string? problem)
    {
        if (!ReferenceEquals(_voiceChatRun, run)) return;
        _voiceChatRun = null;
        run.Dispose();
        VoiceChatStatus = "";
        VoiceChatLevel = 0;
        VoiceChatProblem = problem ?? "";
        RaiseVoiceChatState();
    }

    private void RaiseVoiceChatState()
    {
        Raise(nameof(VoiceChatActive));
        Raise(nameof(VoiceChatBarVisibility));
        Raise(nameof(VoiceChatStopVisibility));
        Raise(nameof(VoiceChatButtonTip));
    }

    /// <summary>One voice chat: the session, the open chat it talks to, and its audio. Implements the session's host.</summary>
    private sealed class VoiceChatRun : IVoiceChatHost, IDisposable
    {
        private readonly MainViewModel _vm;
        private readonly CancellationTokenSource _cts = new();
        private readonly DispatcherTimer _poll;
        private readonly object _gate = new();
        private SpeechJob? _speech;
        private int _historyAtSend, _ticks, _turn;
        private bool _turnActive, _sawBusy, _lastNeeds;
        private string _lastReply = "";

        public VoiceChatRun(MainViewModel vm, IVoiceChatBackend backend, IVoiceChatAudio audio, ConversationVM conversation)
        {
            _vm = vm;
            Backend = backend;
            Audio = audio;
            Conversation = conversation;
            _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => PollTurn(), vm._voiceChatDispatcher!);
        }

        public IVoiceChatBackend Backend { get; }
        public IVoiceChatAudio Audio { get; }
        public ConversationVM Conversation { get; }
        public VoiceChatSession? Session { get; private set; }
        public CancellationToken Token => _cts.Token;
        public string Profile => Conversation.Profile.ProfileId;

        public void Start(VoiceChatSettings settings, string agentName)
        {
            var session = new VoiceChatSession(this, settings, agentName);
            Session = session;
            session.Changed += () => _vm._voiceChatDispatcher!.BeginInvoke(() => _vm.OnVoiceChatChanged(this));
            Audio.MicFrame += (frame, ms) => session.OnFrame(frame, ms);
            Audio.PlayingChanged += playing => session.OnSpeechPlaying(playing);
            Audio.SpeechDrained += session.OnSpeechDone;
            _ = Backend.LeaseAsync(true, Profile);
            _poll.Start();
            session.Start();
        }

        // ---- the session's host: every call returns at once ----

        public void Transcribe(byte[] wav) => _ = Task.Run(async () =>
        {
            try { Session!.OnTranscribed(await Backend.TranscribeAsync(wav, Profile, Token).ConfigureAwait(false)); }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
            catch (AudioTransportUnsupportedException) { Session!.End("This gateway has no speech-to-text set up, so a voice chat cannot work."); }
            catch (Exception) { Session!.OnTranscribeFailed("The gateway could not turn what you said into text."); }
        });

        public void Send(int turn, string text)
        {
            lock (_gate) { _turnActive = false; }               // the previous turn's end is not this one's
            _vm._voiceChatDispatcher!.BeginInvoke(async () =>
            {
                try { await SendOnUiAsync(turn, text); }
                catch (Exception) { Session?.OnTurnEnded(turn, true, "What you said could not be sent."); }   // never let it close the app
            });
        }

        /// <summary>This voice chat is still the one running, in the chat it belongs to.</summary>
        private bool Current => ReferenceEquals(_vm._voiceChatRun, this) && ReferenceEquals(_vm.SelectedConversation, Conversation)
                                && !Token.IsCancellationRequested && Session is { Phase: not VoiceChatPhase.Ended };

        private async Task SendOnUiAsync(int turn, string text)
        {
            if (!ReferenceEquals(_vm._voiceChatRun, this)) return;
            if (!ReferenceEquals(_vm.SelectedConversation, Conversation)) { _vm.EndVoiceChat(null); return; }
            // A cut-in stops the turn first; sending while it still runs would steer it instead.
            for (var waited = 0; _vm._busy && waited < 10_000; waited += 50) await Task.Delay(50).ConfigureAwait(true);
            // Everything may have changed while waiting: the chat ended, another chat is open, or a newer turn was decided.
            if (!Current || Session!.Turn != turn) return;
            if (_vm._busy) { Session.OnTurnEnded(turn, true, "Hermes was still busy, so what you said was not sent."); return; }
            lock (_gate)
            {
                _historyAtSend = Conversation.History.Count;
                _lastReply = "";
                _lastNeeds = false;
                _sawBusy = false;
                _ticks = 0;
                _turn = turn;
                _turnActive = true;
            }
            // Only what was said: never the typed draft, staged files or terminal context, and never an edit of an older message.
            await _vm.SendCoreAsync(null, textOverride: text, textOnly: true).ConfigureAwait(true);
        }

        /// <summary>The reply as it grows, Needs-you, and the end of the turn, read from the open chat (UI thread).</summary>
        private void PollTurn()
        {
            if (Session is not { } session) return;
            bool active;
            int turn;
            lock (_gate) { active = _turnActive; turn = _turn; }
            // Needs-you is read while a turn runs and while the chat waits for an answer (the turn may end meanwhile).
            if (!active && session.Phase != VoiceChatPhase.WaitingForYou) return;
            // Only this chat's own requests: another chat's or a scheduled job's approval is not this conversation's.
            var sessionId = Conversation.SessionId ?? Conversation.StoredSessionId;
            var needs = sessionId is not null && _vm.NeedsYouItems.Any(i => i.Request.SessionId == sessionId);
            if (needs != _lastNeeds) { _lastNeeds = needs; session.OnNeedsYou(needs); }
            if (!active) return;
            _ticks++;
            if (_vm._busy) _sawBusy = true;
            var reply = Conversation.History.Skip(_historyAtSend).LastOrDefault(m => m.Role == "Assistant");
            if (reply is not null && reply.Text != _lastReply)
            {
                _lastReply = reply.Text;
                session.OnReplyText(turn, reply.Text);
            }
            if (!_vm._busy && (_sawBusy || _ticks > 20))
            {
                lock (_gate) _turnActive = false;
                session.OnTurnEnded(turn, reply?.Failure is not null, reply?.Failure?.Title);
            }
        }

        public void CancelTurn() => _vm._voiceChatDispatcher!.BeginInvoke(() =>
        {
            if (_vm.CancelCommand.CanExecute(null)) _vm.CancelCommand.Execute(null);
        });

        public void SpeakStart()
        {
            var job = new SpeechJob(this);
            Interlocked.Exchange(ref _speech, job)?.Stop();
            _ = job.RunAsync();
        }

        public void SpeakAppend(string text) => _speech?.Append(text);
        public void SpeakFinish() => _speech?.Finish();

        public void SpeakStop()
        {
            Interlocked.Exchange(ref _speech, null)?.Stop();
            Audio.StopSpeech();
            Backend.StopClip();
        }

        public void SayFixed(string line)
        {
            SpeakStart();
            SpeakAppend(line);
            SpeakFinish();
        }

        public void Dispose()
        {
            _poll.Stop();
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            Interlocked.Exchange(ref _speech, null)?.Stop();
            try { Audio.StopSpeech(); Backend.StopClip(); } catch (Exception) { /* closing anyway */ }
            Audio.Dispose();
            _ = Backend.LeaseAsync(false, Profile);
        }

        /// <summary>
        /// One reply's speech: streamed through the speak-stream socket while the reply is written, or, when the gateway cannot
        /// (no socket, or it answers "fallback"), the finished reply voiced at once (<c>/api/audio/speak</c>).
        /// </summary>
        private sealed class SpeechJob(VoiceChatRun run)
        {
            private readonly Channel<string?> _text = Channel.CreateUnbounded<string?>();
            private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(run.Token);

            /// <summary>Still the speech the chat wants: a stopped or replaced job touches neither the speaker nor the session.</summary>
            private bool Current => ReferenceEquals(Volatile.Read(ref run._speech), this) && !_cts.IsCancellationRequested;

            public void Append(string text) => _text.Writer.TryWrite(text);
            public void Finish() => _text.Writer.TryWrite(null);
            public void Stop() { try { _cts.Cancel(); } catch (ObjectDisposedException) { } }

            public async Task RunAsync()
            {
                var ct = _cts.Token;
                var all = new StringBuilder();
                ISpeechStream? stream = null;
                var fallback = false;
                try
                {
                    stream = await run.Backend.OpenSpeechAsync(run.Profile, ct).ConfigureAwait(false);
                    var pump = stream is null ? Task.CompletedTask : PumpAsync(stream, () => fallback = true, ct);
                    await foreach (var text in _text.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        if (text is null)
                        {
                            if (stream is not null && !fallback) await stream.FinishAsync().ConfigureAwait(false);
                            break;
                        }
                        all.Append(text);
                        if (stream is not null && !fallback) await stream.AppendAsync(text).ConfigureAwait(false);
                    }
                    await pump.ConfigureAwait(false);
                    if (stream is null || fallback) await SpeakWholeAsync(all.ToString(), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (stream is not null) await stream.StopAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (Current) run.Session?.OnSpeechDone();       // the loop goes on; the reply is on screen
                }
                finally
                {
                    if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
                    _cts.Dispose();
                }
            }

            private async Task PumpAsync(ISpeechStream stream, Action onFallback, CancellationToken ct)
            {
                await foreach (var e in stream.Events(ct).ConfigureAwait(false))
                {
                    if (!Current) return;
                    switch (e)
                    {
                        case SpeechStart start: run.Audio.BeginSpeech(start.SampleRate, start.Channels); break;
                        case SpeechAudio audio: run.Audio.QueuePcm(audio.Pcm); break;
                        case SpeechFallback: onFallback(); return;
                        case SpeechEnd: Ended(); return;
                    }
                }
                Ended();                                            // a dropped socket ends the speech with what was said
            }

            private void Ended()
            {
                if (!Current) return;
                run.Audio.EndSpeech();
                if (!run.Audio.IsPlaying) run.Session?.OnSpeechDone();
            }

            private async Task SpeakWholeAsync(string text, CancellationToken ct)
            {
                var prepared = SpeechText.Prepare(text);
                if (prepared.Problem != SpeechTextProblem.None) { if (Current) run.Session?.OnSpeechDone(); return; }
                var clip = await run.Backend.SpeakAsync(prepared.Text, run.Profile, ct).ConfigureAwait(false);
                if (!Current) return;
                run.Session?.OnSpeechPlaying(true);
                await run.Backend.PlayClipAsync(clip, ct).ConfigureAwait(false);
                if (!Current) return;                               // stopped while it played: the session has moved on
                run.Session?.OnSpeechPlaying(false);
                run.Session?.OnSpeechDone();
            }
        }
    }
}

/// <summary>The real voice chat backend: the gateway's audio routes with the app's session, and this PC's microphone and speaker.</summary>
public sealed class GatewayVoiceChatBackend(Func<DashboardAuth?> auth) : IVoiceChatBackend
{
    private readonly WindowsSpeechPlayer _player = new();
    private readonly string _lease = "hikyaku-voice-" + Guid.NewGuid().ToString("N")[..8];

    private DashboardAuth Auth => auth() ?? throw new InvalidOperationException("Not connected to the gateway.");

    public (IVoiceChatAudio? Audio, string? Problem) OpenAudio() => WinMmVoiceIo.Open();

    public async Task<VoiceChatSettings> LoadSettingsAsync(string profile, CancellationToken ct) =>
        VoiceChatSettings.From(await new ConfigApi(Auth).GetValuesAsync(profile, ct).ConfigureAwait(false));

    public Task<string> TranscribeAsync(byte[] wav, string profile, CancellationToken ct) =>
        new AudioApi(Auth).TranscribeAsync(wav, "audio/wav", profile, ct);

    public Task<ISpeechStream?> OpenSpeechAsync(string profile, CancellationToken ct) => SpeechStreamClient.OpenAsync(Auth, profile, ct);

    public Task<SpokenAudio> SpeakAsync(string text, string profile, CancellationToken ct) => new AudioApi(Auth).SpeakAsync(text, profile, ct);

    public async Task PlayClipAsync(SpokenAudio audio, CancellationToken ct)
    {
        var ended = new TaskCompletionSource();
        void OnEnded() => ended.TrySetResult();
        _player.Ended += OnEnded;
        try
        {
            await _player.PlayAsync(audio, ct).ConfigureAwait(false);
            await ended.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally { _player.Ended -= OnEnded; }
    }

    public void StopClip() => _player.Stop();

    public async Task LeaseAsync(bool active, string profile)
    {
        if (auth() is not { } a) return;
        var api = new AudioApi(a);
        await api.SetTtsLeaseAsync(_lease, active, profile, default).ConfigureAwait(false);
        await api.SetSttLeaseAsync(_lease, active, profile, default).ConfigureAwait(false);
    }
}

/// <summary>Demo, smoke and UI acceptance runs: a voice chat that never opens a device and hears nothing (screenshots only).</summary>
internal sealed class FixtureVoiceChatBackend : IVoiceChatBackend
{
    private sealed class SilentAudio : IVoiceChatAudio
    {
        public event Action<short[], long>? MicFrame { add { } remove { } }
        public event Action<bool>? PlayingChanged { add { } remove { } }
        public event Action? SpeechDrained { add { } remove { } }
        public bool IsPlaying => false;
        public void BeginSpeech(int sampleRate, int channels) { }
        public void QueuePcm(byte[] pcm16le) { }
        public void EndSpeech() { }
        public void StopSpeech() { }
        public void Dispose() { }
    }

    public (IVoiceChatAudio? Audio, string? Problem) OpenAudio() => (new SilentAudio(), null);
    public Task<VoiceChatSettings> LoadSettingsAsync(string profile, CancellationToken ct) => Task.FromResult(VoiceChatSettings.Defaults);
    public Task<string> TranscribeAsync(byte[] wav, string profile, CancellationToken ct) => Task.FromResult("");
    public Task<ISpeechStream?> OpenSpeechAsync(string profile, CancellationToken ct) => Task.FromResult<ISpeechStream?>(null);
    public Task<SpokenAudio> SpeakAsync(string text, string profile, CancellationToken ct) => Task.FromResult(new SpokenAudio([], "audio/wav"));
    public Task PlayClipAsync(SpokenAudio audio, CancellationToken ct) => Task.CompletedTask;
    public void StopClip() { }
    public Task LeaseAsync(bool active, string profile) => Task.CompletedTask;
}
