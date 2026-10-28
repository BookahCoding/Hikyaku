using System.Text.RegularExpressions;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 30: read a reply aloud — speakable text, one explicit request, honest states, no stuck "reading".</summary>
public class Checkpoint30ReadAloudTests
{
    private static VoiceScope Scope(string chat = "chat-1") => new(new ProfileKey("gw", "profile-a"), chat);

    private static (VoiceCaptureController Ctl, FixtureAudioTransport Transport, FakeSpeechPlayer Player) Make()
    {
        var transport = new FixtureAudioTransport();
        var player = new FakeSpeechPlayer();
        var ctl = new VoiceCaptureController(new FakeMicrophoneCapture(), transport, player);
        ctl.BindScope(Scope());
        return (ctl, transport, player);
    }

    private const string Fence = "```";

    // ---- SpeechText ----

    [Fact]
    public void Code_blocks_become_a_spoken_note_once()
    {
        var r = SpeechText.Prepare("Run this:\n\n" + Fence + "bash\nrm -rf x\nls\n" + Fence + "\n\nThen done.");
        Assert.True(r.Ok);
        Assert.DoesNotContain("rm -rf", r.Text);
        Assert.Single(Regex.Matches(r.Text, "code omitted"));
        Assert.Contains("Then done.", r.Text);
    }

    [Fact]
    public void Unclosed_fence_hides_everything_after_it()
    {
        var r = SpeechText.Prepare("Before\n" + Fence + "\nsecret code\nmore code");
        Assert.True(r.Ok);
        Assert.DoesNotContain("secret code", r.Text);
        Assert.Contains("Before", r.Text);
    }

    [Fact]
    public void Links_are_read_as_their_text_and_bare_urls_as_link()
    {
        var r = SpeechText.Prepare("See [the docs](https://example.com/a?b=1) or https://example.com/x now.");
        Assert.Equal("See the docs or link now.", r.Text);
    }

    [Fact]
    public void Markup_characters_are_dropped()
    {
        var r = SpeechText.Prepare("## Title\n\n- **bold** and *italic*\n1. `inline` thing\n> quoted\n\n---");
        Assert.DoesNotContain("#", r.Text);
        Assert.DoesNotContain("*", r.Text);
        Assert.DoesNotContain("`", r.Text);
        Assert.DoesNotContain(">", r.Text);
        Assert.Contains("Title", r.Text);
        Assert.Contains("bold and italic", r.Text);
        Assert.Contains("code omitted thing", r.Text);
        Assert.Contains("quoted", r.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    public void Nothing_speakable_is_reported(string? input)
    {
        var r = SpeechText.Prepare(input);
        Assert.False(r.Ok);
        Assert.Equal(SpeechTextProblem.Empty, r.Problem);
    }

    [Fact]
    public void A_reply_that_is_only_code_says_so_rather_than_being_silent()
    {
        var r = SpeechText.Prepare(Fence + "\nonly code\n" + Fence);
        Assert.True(r.Ok);
        Assert.Equal("code omitted.", r.Text);
    }

    [Fact]
    public void Too_long_is_refused_not_truncated()
    {
        var r = SpeechText.Prepare(new string('a', SpeechText.MaxChars + 1));
        Assert.Equal(SpeechTextProblem.TooLong, r.Problem);
        Assert.Equal("", r.Text);
        Assert.True(SpeechText.Prepare(new string('a', SpeechText.MaxChars)).Ok);
    }

    // ---- controller ----

    [Fact]
    public async Task Read_aloud_sends_prepared_text_for_the_bound_profile_and_plays_it()
    {
        var (ctl, transport, player) = Make();
        using var _ = ctl;
        var r = await ctl.ReadAloudAsync("Hello **there**\n" + Fence + "\nsecretcode\n" + Fence);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(1, transport.SpeakCalls);
        Assert.Equal("profile-a", transport.LastSpeakProfile);
        Assert.DoesNotContain("**", transport.LastSpokenText);
        Assert.DoesNotContain("secretcode", transport.LastSpokenText);
        Assert.Equal(1, player.PlayCount);
    }

    [Fact]
    public async Task Reading_ends_when_the_audio_ends_so_it_never_sticks()
    {
        var (ctl, _, player) = Make();
        using var _ = ctl;
        player.HoldMs = 5_000;
        var task = ctl.ReadAloudAsync("Hello");
        await WaitFor(() => ctl.IsReading && player.PlayCount == 1);
        Assert.True(ctl.IsReading);
        player.SimulateMediaEnded(player.CurrentSessionId);
        Assert.False(ctl.IsReading);
        ctl.StopPlayback();
        await task;
    }

    [Fact]
    public async Task Stop_ends_reading_and_stops_the_player()
    {
        var (ctl, _, player) = Make();
        using var _ = ctl;
        player.HoldMs = 5_000;
        var task = ctl.ReadAloudAsync("Hello");
        await WaitFor(() => ctl.IsReading && player.PlayCount == 1);
        ctl.StopPlayback();
        Assert.False(ctl.IsReading);
        Assert.True(player.IsIdle);
        await task;
    }

    [Fact]
    public async Task Stop_while_the_gateway_is_still_speaking_plays_nothing()
    {
        var (ctl, transport, player) = Make();
        using var _ = ctl;
        transport.SpeakDelayMs = 400;
        var task = ctl.ReadAloudAsync("Hello");
        await WaitFor(() => ctl.IsReading);
        ctl.StopPlayback();
        var r = await task;
        Assert.False(r.Ok);
        Assert.False(ctl.IsReading);
        Assert.Equal(0, player.PlayCount);
    }

    [Fact]
    public async Task Switching_chat_discards_a_late_answer()
    {
        var (ctl, transport, player) = Make();
        using var _ = ctl;
        transport.SpeakDelayMs = 300;
        var task = ctl.ReadAloudAsync("Hello");
        await WaitFor(() => ctl.IsReading);
        ctl.BindScope(Scope("chat-2"));
        var r = await task;
        Assert.False(r.Ok);
        Assert.False(ctl.IsReading);
        Assert.Equal(0, player.PlayCount);
    }

    [Fact]
    public async Task A_second_request_replaces_the_first()
    {
        var (ctl, transport, player) = Make();
        using var _ = ctl;
        transport.SpeakDelayMs = 200;
        var first = ctl.ReadAloudAsync("one");
        await WaitFor(() => transport.SpeakCalls == 1);
        var second = ctl.ReadAloudAsync("two");
        var r1 = await first;
        var r2 = await second;
        Assert.False(r1.Ok);
        Assert.True(r2.Ok, r2.Error);
        Assert.Equal(1, player.PlayCount);
    }

    [Fact]
    public async Task Gateway_without_text_to_speech_says_so()
    {
        var (ctl, transport, player) = Make();
        using var _ = ctl;
        transport.ThrowUnsupportedOnSpeak = true;
        var r = await ctl.ReadAloudAsync("Hello");
        Assert.False(r.Ok);
        Assert.Equal(VoiceCaptureController.ReadUnsupportedCopy, r.Error);
        Assert.False(ctl.IsReading);
        Assert.Equal(0, player.PlayCount);
    }

    [Fact]
    public async Task Unplayable_mime_empty_audio_and_failures_are_reported_and_clear_reading()
    {
        var (ctl, transport, player) = Make();
        using var _ = ctl;
        transport.SpeakMime = "audio/ogg";
        var bad = await ctl.ReadAloudAsync("Hello");
        Assert.False(bad.Ok);
        Assert.Contains("format", bad.Error);
        Assert.False(ctl.IsReading);

        transport.SpeakMime = "audio/wav";
        transport.SpeakBytes = [];
        var empty = await ctl.ReadAloudAsync("Hello");
        Assert.False(empty.Ok);
        Assert.False(ctl.IsReading);

        transport.SpeakBytes = WavBuilder.FromPcm16Mono(new byte[64], 16_000);
        transport.SpeakFailure = new InvalidOperationException("boom");
        var failed = await ctl.ReadAloudAsync("Hello");
        Assert.False(failed.Ok);
        Assert.Contains("boom", failed.Error);
        Assert.False(ctl.IsReading);

        transport.SpeakFailure = null;
        player.FailNext = true;
        var corrupt = await ctl.ReadAloudAsync("Hello");
        Assert.False(corrupt.Ok);
        Assert.False(ctl.IsReading);
        Assert.Equal(0, player.PlayCount);
    }

    [Fact]
    public async Task Nothing_is_sent_for_empty_too_long_or_unbound()
    {
        var (ctl, transport, _) = Make();
        using var _ = ctl;
        Assert.False((await ctl.ReadAloudAsync("   ")).Ok);
        Assert.False((await ctl.ReadAloudAsync(new string('a', SpeechText.MaxChars + 1))).Ok);
        ctl.BindScope(null);
        Assert.False((await ctl.ReadAloudAsync("Hello")).Ok);
        Assert.Equal(0, transport.SpeakCalls);
    }

    private static async Task WaitFor(Func<bool> cond)
    {
        for (var i = 0; i < 200 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond(), "condition not reached");
    }
}
