namespace Hikyaku.Core;

/// <summary>
/// How a voice-chat listen decides you're done talking (BookahHermes voice/Speech.kt @ 5f00986b, after Desktop's
/// tools.voice_mode timings). <see cref="SpeechLevel"/> is a level as <see cref="VoiceLevel"/> measures it.
/// </summary>
public sealed record VoiceActivity(float SpeechLevel = 0.025f, long SilenceMs = 1_250, long IdleMs = 12_000, long MaxMs = 60_000);

/// <summary>A frame's loudness: RMS of 16-bit samples, scaled so 1.0 is loud speech (×4) and clipped to 1.</summary>
public static class VoiceLevel
{
    public static float Of(ReadOnlySpan<short> pcm)
    {
        if (pcm.Length == 0) return 0f;
        double sum = 0;
        foreach (var s in pcm) sum += (double)s * s;
        var rms = Math.Sqrt(sum / pcm.Length) / 32768.0;
        return (float)Math.Min(1.0, rms * 4.0);
    }
}

/// <summary>
/// Decides, frame by frame, when someone has finished talking. The bar rises with the measured background noise: speech must
/// stand clearly above it, and quiet is anything back near it. A blip shorter than a syllable inside the pause does not
/// start it over; a syllable does.
/// </summary>
public sealed class EndOfSpeech(VoiceActivity activity)
{
    private const long CalibrationMs = 300;
    private const float SpeechOverNoise = 2.5f;
    private const float QuietOverNoise = 1.6f;
    private const float QuietFraction = 0.8f;
    private const long SpeechRunMs = 60;      // a syllable's worth at speech level counts as talking again
    private const long MurmurRunMs = 200;     // the same between quiet and speech, which noise reaches more often

    private long _silenceSince = -1, _loudSince = -1, _lastFrameEnd;

    public bool HeardSpeech { get; private set; }

    /// <summary>The measured background level; negative until the first frame.</summary>
    public float Noise { get; private set; } = -1f;

    /// <summary>Feeds one frame's <paramref name="level"/> ending at <paramref name="elapsedMs"/>; true once the listen should end.</summary>
    public bool OnFrame(float level, long elapsedMs)
    {
        var frameStart = _lastFrameEnd;
        _lastFrameEnd = elapsedMs;
        // The opening moment only measures the room; nobody starts talking that fast.
        if (elapsedMs <= CalibrationMs)
        {
            Noise = Noise < 0 ? level : Noise + (level - Noise) * 0.2f;
            return false;
        }
        var speechBar = Math.Max(activity.SpeechLevel, Noise * SpeechOverNoise);
        var quietBar = Math.Max(activity.SpeechLevel * QuietFraction, Noise * QuietOverNoise);
        var speech = level >= speechBar;
        if (speech)
        {
            HeardSpeech = true;
            Noise += (level - Noise) * 0.002f;            // a steady loud room lifts the bar, slowly enough that a sentence doesn't
        }
        else
        {
            // The background noise follows the quiet frames: down quickly, up slowly.
            Noise = Noise < 0 ? level : level < Noise ? Noise + (level - Noise) * 0.3f : Noise + (level - Noise) * 0.02f;
        }
        if (HeardSpeech && level < quietBar)
        {
            _loudSince = -1;
            if (_silenceSince < 0) _silenceSince = elapsedMs;
            if (elapsedMs - _silenceSince >= activity.SilenceMs) return true;
        }
        else if (HeardSpeech)
        {
            if (_loudSince < 0) _loudSince = frameStart;
            if (elapsedMs - _loudSince >= (speech ? SpeechRunMs : MurmurRunMs)) _silenceSince = -1;
        }
        if (!HeardSpeech && activity.IdleMs > 0 && elapsedMs >= activity.IdleMs) return true;
        return elapsedMs >= activity.MaxMs;
    }
}
