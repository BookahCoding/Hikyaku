namespace Hikyaku.Core;

public enum VoiceChatPhase { Off, Listening, Transcribing, Thinking, Speaking, WaitingForYou, Ended }

/// <summary>
/// What a voice chat asks of the app. Every call must return at once (do the work in the background) and report back through
/// the session's <c>On…</c> methods.
/// </summary>
public interface IVoiceChatHost
{
    /// <summary>Transcribe a WAV (16 kHz mono 16-bit); answer with <see cref="VoiceChatSession.OnTranscribed"/> or <see cref="VoiceChatSession.OnTranscribeFailed"/>.</summary>
    void Transcribe(byte[] wav);
    /// <summary>Send this as an ordinary message of the open chat; its reply comes back as <see cref="VoiceChatSession.OnReplyText"/>.</summary>
    void Send(string text);
    /// <summary>Stop the reply being written (someone talked over it).</summary>
    void CancelTurn();
    void SpeakStart();
    void SpeakAppend(string text);
    void SpeakFinish();
    void SpeakStop();
    /// <summary>Say one fixed sentence (not part of a reply).</summary>
    void SayFixed(string line);
}

/// <summary>
/// The chained voice chat (Hermes Desktop's default voice conversation; BookahHermes VoiceController @ 5f00986b): listen until
/// the speaker pauses, send what they said, speak the reply while it is written, listen again. Talking over the voice stops it
/// and sends what was said; talking while it thinks stops that turn only once real words are heard. A stop phrase ends the
/// chat and is never sent; so does a third listen in a row that hears nobody, or a second failure in a row. While Hermes waits
/// for an approval or an answer the voice stops, says so, and nothing heard is taken as an answer. Ending the chat never stops
/// the text turn. Inputs may come from any thread; host calls are made outside the session's lock.
/// </summary>
public sealed class VoiceChatSession(IVoiceChatHost host, VoiceChatSettings settings, string? agentName)
{
    public const string NeedsYouLine = "Hermes needs you. Please check the screen.";
    public const string NobodySpokeProblem = "Nobody spoke, so the voice chat ended.";
    public const string MicSilentProblem =
        "The microphone sends no sound at all. Windows may be blocking it: Settings, Privacy & security, Microphone, "
        + "\"Let desktop apps access your microphone\". Or it is muted.";
    /// <summary>How long a microphone may send pure digital silence (every sample zero) before the chat says so.</summary>
    private const long SilentMicMs = 1_500;
    private const int SampleRate = 16_000;
    private const int MaxQuietListens = 3;
    private const int MaxFailures = 2;

    private readonly object _gate = new();
    private readonly List<Action> _pending = new();
    private readonly List<short> _recording = new();
    private readonly Queue<short[]> _preRoll = new();
    private int _preRollSamples;

    private EndOfSpeech? _eos;
    private BargeIn? _barge;
    private long _listenStart = -1;
    private int _quiet, _failures;
    private bool _cutIn, _hearing, _speechOpen, _speechStopped, _turnEnded, _playing, _waiting;
    private string _sent = "", _replyFull = "", _lastSpoken = "";
    private float _roomFloor;
    private long _firstFrameMs = -1;
    private bool _micAlive;

    public VoiceChatPhase Phase { get; private set; } = VoiceChatPhase.Off;
    /// <summary>Why the chat ended or what last went wrong, in plain words; null when nothing did.</summary>
    public string? Problem { get; private set; }
    /// <summary>The microphone's level now (0..1), for the meter.</summary>
    public float Level { get; private set; }
    public event Action? Changed;

    public void Start() => Run(() =>
    {
        if (Phase != VoiceChatPhase.Off) return;
        _quiet = _failures = 0;
        StartListening();
    });

    public void End(string? why) => Run(() => EndLocked(why));

    public void OnFrame(ReadOnlySpan<short> pcm16k, long atMs)
    {
        var copy = pcm16k.ToArray();
        Run(() => Frame(copy, atMs));
    }

    public void OnTranscribed(string text) => Run(() =>
    {
        if (Phase != VoiceChatPhase.Transcribing) return;
        var said = text.Trim();
        if (_cutIn)
        {
            _cutIn = false;
            if (said.Length > 0 && VoiceStopPhrases.IsStop(said, settings.StopPhrases, agentName)) { EndLocked(null); return; }
            if (said.Length == 0 || IsEcho(said)) { Resume(); return; }
            if (!_turnEnded) _pending.Add(host.CancelTurn);
            SendLocked(said);
            return;
        }
        if (said.Length == 0) { QuietListen(); return; }
        if (VoiceStopPhrases.IsStop(said, settings.StopPhrases, agentName)) { EndLocked(null); return; }
        if (IsEcho(said)) { StartListening(); return; }                  // the speaker heard its own voice
        _quiet = 0;
        SendLocked(said);
    });

    public void OnTranscribeFailed(string why) => Run(() =>
    {
        if (Phase != VoiceChatPhase.Transcribing) return;
        _cutIn = false;
        Problem = why;
        if (++_failures >= MaxFailures) { EndLocked(why); return; }
        StartListening();
    });

    public void OnReplyText(string full) => Run(() =>
    {
        if (!InTurn()) return;
        _replyFull = full;
        if (_waiting || _speechStopped || _hearing) return;
        SpeakNew(complete: false);
    });

    public void OnTurnEnded(bool failed, string? why) => Run(() =>
    {
        if (!InTurn() && Phase != VoiceChatPhase.Transcribing) return;
        _turnEnded = true;
        if (failed)
        {
            Problem = why ?? "The reply failed.";
            if (++_failures >= MaxFailures) { EndLocked(Problem); return; }
        }
        else _failures = 0;
        if (Phase == VoiceChatPhase.Transcribing || _hearing || _waiting) return;   // a cut-in or Needs-you decides what is next
        if (!_speechStopped) SpeakNew(complete: true);
        if (_speechOpen) _pending.Add(host.SpeakFinish);
        else StartListening();
    });

    public void OnSpeechPlaying(bool playing) => Run(() =>
    {
        _playing = playing;
        if (playing && Phase == VoiceChatPhase.Thinking && !_waiting) Phase = VoiceChatPhase.Speaking;
    });

    public void OnSpeechDone() => Run(() =>
    {
        _playing = false;
        if (Phase is not (VoiceChatPhase.Thinking or VoiceChatPhase.Speaking)) return;
        _speechOpen = false;
        if (_turnEnded && !_hearing) StartListening();
        else if (Phase == VoiceChatPhase.Speaking) Phase = VoiceChatPhase.Thinking;
    });

    public void OnNeedsYou(bool waiting) => Run(() =>
    {
        if (waiting && !_waiting && InTurn())
        {
            _waiting = true;
            _hearing = false;
            _cutIn = false;
            if (_speechOpen) { _pending.Add(host.SpeakStop); _speechOpen = false; }
            _pending.Add(() => host.SayFixed(NeedsYouLine));
            Phase = VoiceChatPhase.WaitingForYou;
        }
        else if (!waiting && _waiting)
        {
            _waiting = false;
            if (_turnEnded) StartListening();
            else { Phase = VoiceChatPhase.Thinking; NewBargeIn(); }
        }
    });

    // ---- inside the lock ----

    private void Frame(short[] pcm, long atMs)
    {
        Level = VoiceLevel.Of(pcm);
        // Live finding: a microphone Windows keeps from desktop apps delivers frames of exact zeros, never noise.
        if (!_micAlive)
        {
            if (_firstFrameMs < 0) _firstFrameMs = atMs;
            if (Array.Exists(pcm, s => s != 0)) _micAlive = true;
            else if (atMs - _firstFrameMs >= SilentMicMs) { EndLocked(MicSilentProblem); return; }
        }
        switch (Phase)
        {
            case VoiceChatPhase.Listening:
                if (_listenStart < 0) _listenStart = atMs - 20;
                if (_recording.Count < settings.Activity.MaxMs * SampleRate / 1000) _recording.AddRange(pcm);
                if (_eos!.OnFrame(Level, atMs - _listenStart))
                {
                    if (!_eos.HeardSpeech) QuietListen();
                    else Transcribe(_recording.ToArray());
                }
                break;
            case VoiceChatPhase.Thinking or VoiceChatPhase.Speaking when !_waiting && _barge is not null:
                KeepPreRoll(pcm);
                if (_hearing) _recording.AddRange(pcm);
                switch (_barge.OnFrame(Level, atMs, _playing))
                {
                    case BargeIn.Step.Tripped:
                        _hearing = true;
                        if (_speechOpen) { _pending.Add(host.SpeakStop); _speechOpen = false; _speechStopped = true; }
                        _recording.Clear();
                        foreach (var f in _preRoll) _recording.AddRange(f);
                        break;
                    case BargeIn.Step.Done:
                        _hearing = false;
                        _roomFloor = _barge.RoomFloor;
                        if (_barge.Spoke) { _cutIn = true; Transcribe(_recording.ToArray()); }
                        else Resume();
                        break;
                }
                break;
        }
    }

    private void Transcribe(short[] samples)
    {
        Phase = VoiceChatPhase.Transcribing;
        var pcm = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
        var wav = WavBuilder.FromPcm16Mono(pcm, SampleRate);
        _pending.Add(() => host.Transcribe(wav));
    }

    private void SendLocked(string said)
    {
        _failures = Math.Min(_failures, MaxFailures - 1);
        Phase = VoiceChatPhase.Thinking;
        _sent = _replyFull = "";
        _speechOpen = _speechStopped = _turnEnded = _hearing = _playing = false;
        NewBargeIn();
        _pending.Add(() => host.Send(said));
    }

    /// <summary>Back to where the turn was after a cut-in that was nobody (an echo, a knock, nothing said).</summary>
    private void Resume()
    {
        if (_turnEnded && !_speechOpen) { StartListening(); return; }
        Phase = _speechOpen && _playing ? VoiceChatPhase.Speaking : VoiceChatPhase.Thinking;
        NewBargeIn();
    }

    private void SpeakNew(bool complete)
    {
        var speakable = SpeakableText.SoFar(_replyFull, complete);
        var part = SpeakableText.NewPart(_sent, speakable);
        if (part.Trim().Length == 0) return;
        if (!_speechOpen) { _pending.Add(host.SpeakStart); _speechOpen = true; }
        _pending.Add(() => host.SpeakAppend(part));
        _sent = speakable;
        _lastSpoken = speakable;
    }

    private void QuietListen()
    {
        if (++_quiet >= MaxQuietListens) { EndLocked(NobodySpokeProblem); return; }
        StartListening();
    }

    private void StartListening()
    {
        Phase = VoiceChatPhase.Listening;
        _eos = new EndOfSpeech(settings.Activity);
        _barge = null;
        _listenStart = -1;
        _recording.Clear();
        _cutIn = _hearing = _speechStopped = _waiting = false;
    }

    private void NewBargeIn()
    {
        _barge = settings.BargeIn.Enabled ? new BargeIn(settings.BargeIn, settings.Activity.SilenceMs, _roomFloor) : null;
        _recording.Clear();
    }

    private void KeepPreRoll(short[] pcm)
    {
        _preRoll.Enqueue(pcm);
        _preRollSamples += pcm.Length;
        while (_preRollSamples > BargeIn.PreRollMs * SampleRate / 1000 && _preRoll.Count > 1)
            _preRollSamples -= _preRoll.Dequeue().Length;
    }

    private void EndLocked(string? why)
    {
        if (Phase is VoiceChatPhase.Off or VoiceChatPhase.Ended) return;
        Phase = VoiceChatPhase.Ended;
        Problem = why;
        _pending.Add(host.SpeakStop);                                 // the text turn is left running
    }

    private bool InTurn() => Phase is VoiceChatPhase.Thinking or VoiceChatPhase.Speaking or VoiceChatPhase.WaitingForYou
        || Phase == VoiceChatPhase.Transcribing && _cutIn;

    private bool IsEcho(string said) => _lastSpoken.Length > 0 && TtsEcho.IsEcho(said, _lastSpoken);

    /// <summary>Runs one input under the lock, then the host calls it produced and the change notice, outside it.</summary>
    private void Run(Action input)
    {
        Action[] calls;
        lock (_gate)
        {
            input();
            calls = _pending.ToArray();
            _pending.Clear();
        }
        foreach (var call in calls) call();
        Changed?.Invoke();
    }
}
