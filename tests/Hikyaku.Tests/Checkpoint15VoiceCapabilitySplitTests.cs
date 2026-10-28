using System.Net;
using System.Text;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 15a — voice capability split. In BookahHermes AudioApi.kt @ 5f00986b,
/// <c>voice-live/status.available</c> means "GPT-Live can start (an OpenAI key resolves)";
/// <c>mode:chained</c> is the ordinary STT → send → TTS path, and a failing status call
/// "means chained". So status must never gate independent STT/TTS. Source-derived fixtures only.
/// </summary>
public class Checkpoint15VoiceCapabilitySplitTests
{
    private static VoiceScope Scope(string chat = "chat-1") =>
        new(new ProfileKey("gw", "profile-a"), chat);

    private static byte[] TinyWav() => WavBuilder.FromPcm16Mono(new byte[320], 16_000);

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static DashboardAuth Auth(Func<HttpRequestMessage, Task<HttpResponseMessage>> impl) =>
        new(new Uri("https://hermes.example/"), new Stub(impl));

    private sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> impl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => impl(request);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"mode":"chained","available":false,"reason":"no OpenAI key"}""")]
    [InlineData(HttpStatusCode.OK, """{"mode":"gpt-live","available":false}""")]
    [InlineData(HttpStatusCode.OK, """{"mode":"chained"}""")] // legacy body without the field
    [InlineData(HttpStatusCode.OK, """{}""")]
    [InlineData(HttpStatusCode.NotFound, """{"detail":"missing"}""")] // gateway without GPT-Live
    [InlineData((HttpStatusCode)429, """{"detail":"rate"}""")]
    [InlineData(HttpStatusCode.InternalServerError, """{"detail":"boom"}""")]
    public async Task Voice_live_status_does_not_disable_independent_dictation(HttpStatusCode code, string body)
    {
        using var auth = Auth(_ => Task.FromResult(Json(code, body)));
        var probe = await new AudioApi(auth).ProbeAsync("p", CancellationToken.None);
        Assert.NotEqual(VoiceTransportCapability.Unsupported, probe.Dictation);
        Assert.NotEqual(VoiceTransportCapability.SetupRequired, probe.Dictation);
        Assert.NotEqual(VoiceLiveAvailability.Available, probe.VoiceLive);
    }

    [Fact]
    public async Task Network_failure_on_status_does_not_claim_dictation_unsupported()
    {
        using var auth = Auth(_ => throw new HttpRequestException("down"));
        var probe = await new AudioApi(auth).ProbeAsync("p", CancellationToken.None);
        Assert.NotEqual(VoiceTransportCapability.Unsupported, probe.Dictation);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Auth_failure_still_fails_closed_as_setup_required(HttpStatusCode code)
    {
        using var auth = Auth(_ => Task.FromResult(Json(code, """{"detail":"auth"}""")));
        var probe = await new AudioApi(auth).ProbeAsync("p", CancellationToken.None);
        Assert.Equal(VoiceTransportCapability.SetupRequired, probe.Dictation);
    }

    [Fact]
    public async Task Voice_live_is_available_only_when_gpt_live_mode_and_available_true()
    {
        async Task<VoiceLiveAvailability> Live(string body)
        {
            using var auth = Auth(_ => Task.FromResult(Json(HttpStatusCode.OK, body)));
            return (await new AudioApi(auth).ProbeAsync("p", CancellationToken.None)).VoiceLive;
        }
        Assert.Equal(VoiceLiveAvailability.Available, await Live("""{"mode":"gpt-live","available":true}"""));
        // Chained profile: nothing to start live, but dictation unaffected.
        Assert.Equal(VoiceLiveAvailability.Unavailable, await Live("""{"mode":"chained","available":true}"""));
        Assert.Equal(VoiceLiveAvailability.Unavailable, await Live("""{"mode":"gpt-live","available":false}"""));
    }

    [Fact]
    public async Task Bounded_status_body_is_still_enforced()
    {
        var big = new string('x', AudioApi.MaxResponseBodyBytes + 10);
        using var auth = Auth(_ => Task.FromResult(Json(HttpStatusCode.OK, "{\"reason\":\"" + big + "\"}")));
        var probe = await new AudioApi(auth).ProbeAsync("p", CancellationToken.None);
        Assert.NotEqual(VoiceTransportCapability.Supported, probe.Dictation);
        Assert.Equal(VoiceLiveAvailability.Unknown, probe.VoiceLive);
    }

    [Fact]
    public async Task Controller_records_with_chained_unavailable_status_and_transcribes_through_real_http_path()
    {
        var transcribeCalls = 0;
        using var auth = Auth(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/audio/voice-live/status", StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK, """{"mode":"chained","available":false}"""));
            if (path.EndsWith("/api/audio/transcribe", StringComparison.Ordinal))
            {
                transcribeCalls++;
                Assert.Contains("profile=profile-a", req.RequestUri.Query, StringComparison.Ordinal);
                return Task.FromResult(Json(HttpStatusCode.OK, """{"transcript":"hello there"}"""));
            }
            return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
        });
        var mic = new FakeMicrophoneCapture { NextPcm = new byte[320] };
        using var ctl = new VoiceCaptureController(mic, new HttpAudioTransport(auth), new FakeSpeechPlayer());
        ctl.BindScope(Scope());
        await ctl.RefreshCapabilityAsync();

        Assert.NotEqual(VoicePhase.Unsupported, ctl.Phase);
        Assert.True((await ctl.StartCaptureAsync(true)).Ok);
        Assert.True(ctl.StopCapture().Ok);
        Assert.True((await ctl.TranscribePreviewAsync()).Ok);
        Assert.Equal("hello there", ctl.Transcript);
        Assert.Equal(1, transcribeCalls);
        Assert.False(ctl.DidAutoSend);
    }

    [Fact]
    public async Task Controller_learns_unsupported_when_transcribe_route_itself_is_404()
    {
        using var auth = Auth(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/api/audio/transcribe", StringComparison.Ordinal)
                ? Task.FromResult(Json(HttpStatusCode.NotFound, """{"detail":"nf"}"""))
                : Task.FromResult(Json(HttpStatusCode.NotFound, "{}")));
        var mic = new FakeMicrophoneCapture { NextPcm = new byte[320] };
        using var ctl = new VoiceCaptureController(mic, new HttpAudioTransport(auth), new FakeSpeechPlayer());
        ctl.BindScope(Scope());
        await ctl.RefreshCapabilityAsync();
        Assert.True((await ctl.StartCaptureAsync(true)).Ok);
        Assert.True(ctl.StopCapture().Ok);

        var result = await ctl.TranscribePreviewAsync();
        Assert.False(result.Ok);
        Assert.Equal(VoiceTransportCapability.Unsupported, ctl.Capability);
        Assert.Equal(VoicePhase.Unsupported, ctl.Phase);
        Assert.Null(ctl.PendingAudio); // raw audio discarded, not kept behind a dead route
        var again = await ctl.StartCaptureAsync(true);
        Assert.False(again.Ok);
        Assert.False(mic.IsCapturing);
    }

    [Fact]
    public async Task Cross_scope_late_unsupported_result_does_not_poison_new_scope()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        using var auth = Auth(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/api/audio/transcribe", StringComparison.Ordinal)
                ? gate.Task
                : Task.FromResult(Json(HttpStatusCode.NotFound, "{}")));
        var mic = new FakeMicrophoneCapture { NextPcm = new byte[320] };
        using var ctl = new VoiceCaptureController(mic, new HttpAudioTransport(auth), new FakeSpeechPlayer());
        ctl.BindScope(Scope("a"));
        await ctl.RefreshCapabilityAsync();
        await ctl.StartCaptureAsync(true);
        ctl.StopCapture();
        var pending = ctl.TranscribePreviewAsync();

        ctl.BindScope(Scope("b"));
        gate.SetResult(Json(HttpStatusCode.NotFound, """{"detail":"nf"}"""));
        await pending;

        Assert.NotEqual(VoiceTransportCapability.Unsupported, ctl.Capability);
        Assert.NotEqual(VoicePhase.Unsupported, ctl.Phase);
    }
}
