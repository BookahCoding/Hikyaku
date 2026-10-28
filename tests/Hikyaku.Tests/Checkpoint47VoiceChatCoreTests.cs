using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 47 (0.6.0 voice chat) — the decisions a voice chat makes from the microphone and the transcript, ported from
/// BookahHermes voice/{Speech,BargeIn}.kt @ 5f00986b: when the speaker has finished, when someone talks over the agent,
/// when a transcript is only the agent hearing itself, what ends the chat, and what of a reply can be spoken.
/// </summary>
public class Checkpoint47VoiceChatCoreTests
{
    /// <summary>Feeds levels in 20 ms frames from <paramref name="start"/>; returns the time the detector said "done", or -1.</summary>
    private static long Feed(EndOfSpeech eos, ref long t, float level, long durationMs)
    {
        for (var end = t + durationMs; t < end;)
        {
            t += 20;
            if (eos.OnFrame(level, t)) return t;
        }
        return -1;
    }

    [Fact]
    public void A_pause_after_speech_ends_the_turn_after_the_silence_time()
    {
        var eos = new EndOfSpeech(new VoiceActivity());
        long t = 0;
        Assert.Equal(-1, Feed(eos, ref t, 0.005f, 300));          // the room
        Assert.Equal(-1, Feed(eos, ref t, 0.2f, 1000));           // speech
        var speechEnd = t;
        var done = Feed(eos, ref t, 0.005f, 1500);
        Assert.True(eos.HeardSpeech);
        Assert.InRange(done, speechEnd + 1240, speechEnd + 1280);
    }

    [Fact]
    public void A_short_blip_in_the_pause_does_not_start_it_over_but_a_syllable_does()
    {
        var blip = new EndOfSpeech(new VoiceActivity());
        long t = 0;
        Feed(blip, ref t, 0.005f, 300);
        Feed(blip, ref t, 0.2f, 1000);
        var speechEnd = t;
        Feed(blip, ref t, 0.005f, 600);
        Feed(blip, ref t, 0.2f, 40);                              // shorter than a syllable
        var done = Feed(blip, ref t, 0.005f, 1500);
        Assert.InRange(done, speechEnd + 1240, speechEnd + 1280);

        var syllable = new EndOfSpeech(new VoiceActivity());
        t = 0;
        Feed(syllable, ref t, 0.005f, 300);
        Feed(syllable, ref t, 0.2f, 1000);
        Feed(syllable, ref t, 0.005f, 600);
        Feed(syllable, ref t, 0.2f, 100);                         // a syllable: talking again
        var again = t;
        var done2 = Feed(syllable, ref t, 0.005f, 1500);
        Assert.InRange(done2, again + 1240, again + 1280);
    }

    [Fact]
    public void Only_quiet_gives_up_after_the_idle_time_without_speech()
    {
        var eos = new EndOfSpeech(new VoiceActivity());
        long t = 0;
        var done = Feed(eos, ref t, 0.005f, 13_000);
        Assert.Equal(12_000, done);
        Assert.False(eos.HeardSpeech);
    }

    /// <summary>Feeds 20 ms frames from <paramref name="levelAt"/> to the barge-in detector; returns the first step that is not Waiting.</summary>
    private static (BargeIn.Step Step, long At) Run(BargeIn b, ref long t, long durationMs, Func<long, float> levelAt, bool playing)
    {
        for (var end = t + durationMs; t < end;)
        {
            t += 20;
            var step = b.OnFrame(levelAt(t), t, playing);
            if (step != BargeIn.Step.Waiting) return (step, t);
        }
        return (BargeIn.Step.Waiting, t);
    }

    [Fact]
    public void Someone_talking_in_a_quiet_room_trips_it()
    {
        var b = new BargeIn(new BargeInSettings());
        long t = 0;
        Assert.Equal(BargeIn.Step.Waiting, Run(b, ref t, 500, _ => 0.01f, playing: false).Step);
        Assert.Equal(BargeIn.Step.Tripped, Run(b, ref t, 400, _ => 0.3f, playing: false).Step);
    }

    [Fact]
    public void The_voices_own_echo_from_a_loudspeaker_never_trips_it_but_a_person_over_it_does()
    {
        var b = new BargeIn(new BargeInSettings());
        long t = 0;
        Run(b, ref t, 500, _ => 0.01f, playing: false);                          // the room
        // Five seconds of the voice coming back through the microphone: syllables rising and falling up to 0.15.
        var echo = Run(b, ref t, 5000, at => 0.03f + 0.12f * (float)Math.Abs(Math.Sin(at / 90.0)), playing: true);
        Assert.Equal(BargeIn.Step.Waiting, echo.Step);
        // A person talking over it, steadily well above the echo.
        Assert.Equal(BargeIn.Step.Tripped, Run(b, ref t, 800, _ => 0.45f, playing: true).Step);
    }

    [Fact]
    public void A_trip_with_nobody_talking_on_is_over_quickly_and_says_nobody_spoke()
    {
        var b = new BargeIn(new BargeInSettings());
        long t = 0;
        Run(b, ref t, 500, _ => 0.01f, playing: false);
        Assert.Equal(BargeIn.Step.Tripped, Run(b, ref t, 400, _ => 0.3f, playing: false).Step);
        var tripAt = t;
        BargeIn.Step step;
        do { t += 20; step = b.OnFrame(0.01f, t, playing: false); } while (step == BargeIn.Step.Hearing && t - tripAt < 5000);
        Assert.Equal(BargeIn.Step.Done, step);
        Assert.False(b.Spoke);
        Assert.InRange(t - tripAt, 880, 940);
    }

    [Fact]
    public void Sequence_ratio_matches_python_difflib_and_echo_is_recognised()
    {
        Assert.Equal(0.75, TtsEcho.SequenceRatio("abcd".ToList(), "bcde".ToList()), 3);
        Assert.Equal(1.0, TtsEcho.SequenceRatio(new List<char>(), new List<char>()), 3);
        Assert.True(TtsEcho.IsEcho("the weather is sunny today", "The weather is sunny today in Athens."));
        Assert.False(TtsEcho.IsEcho("yes", "The weather is sunny today in Athens, with a light breeze."));
        Assert.True(TtsEcho.IsEcho("light breeze from the north west", "It is sunny in Athens today, with a light breeze from the north west and clear skies."));
        Assert.False(TtsEcho.IsEcho("what about tomorrow in London", "It is sunny in Athens today, with a light breeze from the north west and clear skies."));
        Assert.False(TtsEcho.IsEcho("", "anything"));
    }

    [Fact]
    public void The_level_is_scaled_rms_clipped_to_one()
    {
        Assert.Equal(0f, VoiceLevel.Of(new short[320]));
        Assert.Equal(1f, VoiceLevel.Of(Enumerable.Range(0, 320).Select(i => (short)(i % 2 == 0 ? 32767 : -32767)).ToArray()));
        Assert.InRange(VoiceLevel.Of(Enumerable.Repeat((short)819, 320).ToArray()), 0.099f, 0.101f);   // 819/32768 × 4 ≈ 0.1
    }
}
