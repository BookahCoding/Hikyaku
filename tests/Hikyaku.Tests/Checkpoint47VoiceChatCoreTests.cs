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

    [Fact]
    public void The_level_is_scaled_rms_clipped_to_one()
    {
        Assert.Equal(0f, VoiceLevel.Of(new short[320]));
        Assert.Equal(1f, VoiceLevel.Of(Enumerable.Range(0, 320).Select(i => (short)(i % 2 == 0 ? 32767 : -32767)).ToArray()));
        Assert.InRange(VoiceLevel.Of(Enumerable.Repeat((short)819, 320).ToArray()), 0.099f, 0.101f);   // 819/32768 × 4 ≈ 0.1
    }
}
