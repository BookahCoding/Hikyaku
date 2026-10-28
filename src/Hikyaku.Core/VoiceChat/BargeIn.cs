namespace Hikyaku.Core;

/// <summary>
/// Talking over a reply to cut in (the profile's <c>voice.barge_in</c>, on unless it is set off). <see cref="Multiplier"/> is
/// <c>voice.barge_in_threshold_multiplier</c> (lower trips sooner; null keeps the stock levels); <see cref="GraceMs"/> is
/// <c>voice.barge_in_grace_seconds</c>, how long the voice's first sound cannot trip it.
/// </summary>
public sealed record BargeInSettings(bool Enabled = true, double? Multiplier = null, long GraceMs = 500);

/// <summary>
/// Decides, frame by frame, when someone talks over the agent's turn, and then when they have finished (BookahHermes
/// voice/BargeIn.kt @ 5f00986b, after Desktop's voice-barge-in.ts and tools/voice_mode.full_duplex_listen):
/// the room's quiet floor is measured only while nothing plays; while the voice plays the bar is raised over its own echo,
/// which is learnt from its louder syllables, and capped so speech can still clear it; speech has to hold (most of the last
/// 300 ms, or 500 ms over a loud echo); the voice's first moment after a real gap cannot trip it. Once tripped, the
/// utterance runs until a pause, and <see cref="Spoke"/> tells someone talking from the speaker hearing itself.
/// Levels are <see cref="VoiceLevel"/>'s.
/// </summary>
public sealed class BargeIn
{
    public enum Step
    {
        /// <summary>Nobody has cut in.</summary>
        Waiting,
        /// <summary>Someone just started talking over it: cut the voice now.</summary>
        Tripped,
        /// <summary>They are still talking.</summary>
        Hearing,
        /// <summary>They have finished; the utterance is complete.</summary>
        Done,
    }

    public const double DefaultMultiplier = 3.0;
    private const long CalibrationMs = 400;
    private const long SustainedMs = 300;
    private const long SustainedOverEchoMs = 500;
    private const long TalkingMs = 100;
    private const float SustainedMajority = 0.8f;
    private const float MinTriggerLevel = 0.025f;
    private const double FloorMultiplier = 3.5;
    private const double PlaybackMinLevel = 0.05;
    private const double BleedMultiplier = 1.8;
    private const double MinBleedMargin = 1.2;
    private const float BleedPercentile = 0.8f;
    private const float TriggerCeilingLevel = 0.25f;
    private const float EchoBarCeilingLevel = 0.4f;
    private const float BleedLearnHeadroom = 1.25f;
    private const long PlaybackGapForGraceMs = 1_000;
    private const int FloorSamples = 150;
    private const float QuietFraction = 0.8f;
    private const float QuietOverFloor = 1.6f;
    private const long UtteranceMaxMs = 30_000;
    /// <summary>How much sound before the trip is kept, so the utterance has its first syllable.</summary>
    public const long PreRollMs = 800;
    private const long HoldSettleMs = 300;
    private const int SpokeFrames = 8;
    private const long NobodyAfterMs = 900;

    private readonly BargeInSettings _settings;
    private readonly long _silenceMs;
    private readonly float _floorMultiplier, _playbackMin, _bleedMultiplier;
    private readonly Levels _floorSamples = new(FloorSamples), _bleedSamples = new(FloorSamples);
    private readonly LinkedList<(long At, bool Above)> _recent = new();
    private float _floor, _bleed;
    private long _calibratedSince = -1, _lastPlayingAt, _graceUntil, _trippedAt, _quietSince = -1;
    private bool _floorLocked, _wasPlaying, _playbackSeen, _graceIsEcho = true;
    private int _spokeFrames;

    public BargeIn(BargeInSettings settings, long silenceMs = 1_250, float roomFloor = 0f)
    {
        _settings = settings;
        _silenceMs = silenceMs;
        var scale = settings.Multiplier is { } m && double.IsFinite(m) && m > 0 ? m / DefaultMultiplier : 1.0;
        _floorMultiplier = (float)(FloorMultiplier * scale);
        _playbackMin = (float)(PlaybackMinLevel * scale);
        _bleedMultiplier = (float)Math.Max(BleedMultiplier * scale, MinBleedMargin);   // a lower sensitivity still stays over the echo
        if (roomFloor > 0f) AddFloor(roomFloor);
    }

    public bool Tripped { get; private set; }

    /// <summary>Someone went on talking once the voice held, past its last echo; not just the voice.</summary>
    public bool Spoke { get; private set; }

    /// <summary>The room's quiet floor so far, for the next listen in the chat; 0 when it has not heard the room quiet.</summary>
    public float RoomFloor => _floor;

    public Step OnFrame(float level, long nowMs, bool playing)
    {
        if (Tripped) return Endpoint(level, nowMs);
        if (!_floorLocked)
        {
            if (!playing)
            {
                if (_calibratedSince < 0) _calibratedSince = nowMs;
                AddFloor(level);
            }
            if (playing || _calibratedSince >= 0 && nowMs - _calibratedSince >= CalibrationMs) _floorLocked = true;
        }
        // A grace only when the voice starts after a real gap, so it flickering between sentences cannot chain them.
        if (playing && !_wasPlaying)
        {
            if (!_playbackSeen || nowMs - _lastPlayingAt >= PlaybackGapForGraceMs)
            {
                _graceUntil = nowMs + _settings.GraceMs;
                _graceIsEcho = !TalkingAt(nowMs);
            }
            _playbackSeen = true;
        }
        _wasPlaying = playing;
        if (playing) _lastPlayingAt = nowMs;

        var trigger = Trigger(playing);
        if (_floorLocked && !playing && level < trigger) AddFloor(level);      // the room drifts; follow it while quiet
        var inGrace = nowMs < _graceUntil;
        // The voice's echo is learnt from all of its first moment, then only from what stays under the bar, close to what is
        // learnt already, and while nothing stood over the bar (that is someone talking just under it).
        if (playing && (inGrace && _graceIsEcho || !inGrace && level < trigger && level <= _bleed * BleedLearnHeadroom && !_recent.Any(r => r.Above)))
            AddBleed(level);
        var above = _floorLocked && level >= trigger && !inGrace;
        _recent.AddLast((nowMs, above));
        while (_recent.Count > 0 && nowMs - _recent.First!.Value.At > SustainedOverEchoMs) _recent.RemoveFirst();
        var hold = playing && trigger > PlayingBar() ? SustainedOverEchoMs : SustainedMs;
        long since = nowMs;
        int frames = 0, aboveCount = 0;
        foreach (var (at, over) in _recent)
        {
            if (nowMs - at > hold) continue;
            if (frames++ == 0) since = at;
            if (over) aboveCount++;
        }
        var span = nowMs - since;
        if (above && span >= hold * SustainedMajority && aboveCount >= frames * SustainedMajority)
        {
            Tripped = true;
            _trippedAt = nowMs;
            return Step.Tripped;
        }
        return Step.Waiting;
    }

    /// <summary>The bar speech has to clear now.</summary>
    internal float Trigger(bool playing) =>
        !playing ? QuietBar() : Math.Max(PlayingBar(), Math.Min(_bleed * _bleedMultiplier, EchoBarCeilingLevel));

    private float QuietBar() => Math.Max(MinTriggerLevel, _floor * _floorMultiplier);

    private float PlayingBar() => Math.Min(Math.Max(QuietBar(), _playbackMin), TriggerCeilingLevel);

    private bool TalkingAt(long nowMs)
    {
        int frames = 0, aboveCount = 0;
        foreach (var (at, over) in _recent)
        {
            if (nowMs - at > TalkingMs) continue;
            frames++;
            if (over) aboveCount++;
        }
        return frames >= 3 && aboveCount >= frames * SustainedMajority;
    }

    private Step Endpoint(float level, long nowMs)
    {
        if (nowMs - _trippedAt >= HoldSettleMs && level >= PlayingBar() && ++_spokeFrames >= SpokeFrames) Spoke = true;
        if (level >= Math.Max(MinTriggerLevel * QuietFraction, _floor * QuietOverFloor)) _quietSince = -1;
        else if (_quietSince < 0) _quietSince = nowMs;
        if (!Spoke && nowMs - _trippedAt >= NobodyAfterMs) return Step.Done;   // the speaker hearing itself, or a knock
        var done = _quietSince >= 0 && nowMs - _quietSince >= _silenceMs || nowMs - _trippedAt >= UtteranceMaxMs;
        return done ? Step.Done : Step.Hearing;
    }

    private void AddBleed(float level)
    {
        _bleedSamples.Add(level);
        // Its louder syllables, not its average: the voice's peaks are what could pass for someone talking.
        _bleed = _bleedSamples[Math.Min((int)(_bleedSamples.Count * BleedPercentile), _bleedSamples.Count - 1)];
    }

    private void AddFloor(float level)
    {
        _floorSamples.Add(level);
        _floor = _floorSamples[_floorSamples.Count / 2];
    }

    /// <summary>The last <c>capacity</c> levels, kept sorted as they come, so a percentile needs no sort per frame.</summary>
    private sealed class Levels(int capacity)
    {
        private readonly Queue<float> _arrived = new();
        private readonly List<float> _sorted = new();
        public int Count => _sorted.Count;
        public float this[int index] => _sorted[index];

        public void Add(float level)
        {
            _arrived.Enqueue(level);
            _sorted.Insert(Slot(level), level);
            if (_arrived.Count > capacity) _sorted.RemoveAt(Slot(_arrived.Dequeue()));
        }

        private int Slot(float level)
        {
            var i = _sorted.BinarySearch(level);
            return i < 0 ? ~i : i;
        }
    }
}
