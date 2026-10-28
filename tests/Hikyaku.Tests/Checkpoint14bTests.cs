using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Checkpoint 14b — reproduce SOURCE voice lifecycle defects with deterministic fakes
/// (no real microphone / live gateway).
/// </summary>
public class Checkpoint14bTests
{
    private static VoiceScope Scope(string chat = "chat-1") =>
        new(new ProfileKey("gw", "profile-a"), chat);

    private static byte[] TinyWav(int pcmBytes = 320) =>
        WavBuilder.FromPcm16Mono(new byte[pcmBytes], 16_000);

    [Fact]
    public async Task Oversized_capture_completed_does_not_raise_changed_under_controller_lock()
    {
        var held = new FakeMicrophoneCapture { HoldUntilFinish = true, NextPcm = new byte[16] };
        using var ctl = new VoiceCaptureController(
            held, new FixtureAudioTransport(), new FakeSpeechPlayer(),
            limits: VoiceCaptureLimits.Default with { MaxBytes = 64 });
        ctl.BindScope(Scope());
        Assert.True((await ctl.StartCaptureAsync(true)).Ok);

        var sawOversizedChanged = new ManualResetEventSlim(false);
        var timedOut = false;
        ctl.Changed += () =>
        {
            // Only assert on the oversized-discard Changed (phase leaves Recording).
            if (ctl.Phase == VoicePhase.Recording) return;
            var phaseReadOk = new ManualResetEventSlim(false);
            var reader = Task.Run(() =>
            {
                _ = ctl.Phase;
                _ = ctl.Status;
                phaseReadOk.Set();
            });
            if (!phaseReadOk.Wait(TimeSpan.FromMilliseconds(400)))
                timedOut = true;
            sawOversizedChanged.Set();
            _ = reader;
        };

        var huge = TinyWav(pcmBytes: 256); // wav payload > 64 max bytes
        Assert.True(huge.LongLength > 64);
        held.ReleaseHeldCapture(huge);

        Assert.True(sawOversizedChanged.Wait(TimeSpan.FromSeconds(2)), "Changed should fire for oversized discard");
        Assert.False(timedOut, "Changed must not run under _gate (Phase reader timed out)");
        Assert.Null(ctl.PendingAudio);
        Assert.Equal(VoicePhase.Off, ctl.Phase);
    }

    [Fact]
    public void WaveIn_ABI_WaveFormatEx_matches_winmm_layout()
    {
        // WAVEFORMATEX: WORD+WORD+DWORD+DWORD+WORD+WORD+WORD = 18 bytes
        Assert.Equal(18, Marshal.SizeOf<WaveFormatEx>());
        Assert.Equal(0, Marshal.OffsetOf<WaveFormatEx>(nameof(WaveFormatEx.wFormatTag)).ToInt32());
        Assert.Equal(2, Marshal.OffsetOf<WaveFormatEx>(nameof(WaveFormatEx.nChannels)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<WaveFormatEx>(nameof(WaveFormatEx.nSamplesPerSec)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<WaveFormatEx>(nameof(WaveFormatEx.nAvgBytesPerSec)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<WaveFormatEx>(nameof(WaveFormatEx.nBlockAlign)).ToInt32());
        Assert.Equal(14, Marshal.OffsetOf<WaveFormatEx>(nameof(WaveFormatEx.wBitsPerSample)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<WaveFormatEx>(nameof(WaveFormatEx.cbSize)).ToInt32());
    }

    [Fact]
    public async Task WaveIn_seam_cancel_then_restart_rejects_stale_session_callbacks()
    {
        var clock = new FakeCaptureClock();
        var backend = new FakeWaveInBackend();
        using var cap = new WindowsWaveInCapture(backend, clock);
        var completed = new List<VoiceAudioClip>();
        var failed = new List<string>();
        cap.CaptureCompleted += c => completed.Add(c);
        cap.CaptureFailed += (_, m) => failed.Add(m);

        var limits = VoiceCaptureLimits.Default with { MaxDurationMs = 5_000 };
        Assert.True((await cap.StartAsync(limits)).Ok);
        var sessionA = backend.LastOpenSessionId;
        Assert.True(sessionA > 0);

        cap.Cancel();
        clock.Advance(60);
        backend.PumpWatchdogOpportunity(); // allow cancel to settle
        await WaitUntil(() => !cap.IsCapturing, 1000);

        Assert.True((await cap.StartAsync(limits)).Ok);
        var sessionB = backend.LastOpenSessionId;
        Assert.NotEqual(sessionA, sessionB);

        // Stale session A data must not be accepted while B is active.
        backend.EmitData(sessionA, new byte[320]);
        clock.Advance(100);
        backend.EmitData(sessionB, new byte[640]);
        cap.Finish();
        clock.Advance(100);
        backend.PumpWatchdogOpportunity();
        await WaitUntil(() => !cap.IsCapturing, 1000);

        Assert.Single(completed);
        Assert.True(completed[0].Bytes.Length > 44);
        Assert.Empty(failed);
    }

    [Fact]
    public async Task WaveIn_seam_wallclock_bounds_capture_with_no_data()
    {
        var clock = new FakeCaptureClock();
        var backend = new FakeWaveInBackend { EmitDataAutomatically = false };
        using var cap = new WindowsWaveInCapture(backend, clock);
        var completed = 0;
        var failed = 0;
        cap.CaptureCompleted += _ => completed++;
        cap.CaptureFailed += (_, _) => failed++;

        var limits = VoiceCaptureLimits.Default with { MaxDurationMs = 200 };
        Assert.True((await cap.StartAsync(limits)).Ok);
        // No bytes received — wall clock must still stop (not wait on received-byte elapsed).
        for (var i = 0; i < 10; i++)
        {
            clock.Advance(50);
            backend.PumpWatchdogOpportunity();
            if (!cap.IsCapturing) break;
        }
        Assert.False(cap.IsCapturing);
        Assert.True(failed + completed >= 1, "wall-clock stop must complete keep or fail honestly");
    }

    [Fact]
    public async Task WaveIn_seam_partial_startup_failure_releases_native_handles()
    {
        var backend = new FakeWaveInBackend { FailOnStart = true };
        using var cap = new WindowsWaveInCapture(backend, new FakeCaptureClock());
        var result = await cap.StartAsync(VoiceCaptureLimits.Default);
        Assert.False(result.Ok);
        Assert.Equal(0, backend.OpenHandlesAlive);
        Assert.Equal(0, backend.PreparedBuffersAlive);
        Assert.False(cap.IsCapturing);
    }

    [Fact]
    public async Task WaveIn_seam_exception_after_open_cleans_partial_buffers()
    {
        var backend = new FakeWaveInBackend { FailOnPrepareSecondBuffer = true };
        using var cap = new WindowsWaveInCapture(backend, new FakeCaptureClock());
        var result = await cap.StartAsync(VoiceCaptureLimits.Default);
        Assert.False(result.Ok);
        Assert.Equal(0, backend.OpenHandlesAlive);
        Assert.Equal(0, backend.PreparedBuffersAlive);
    }

    [Fact]
    public async Task AudioApi_probe_distinguishes_auth_rate_limit_and_unavailable()
    {
        async Task<VoiceTransportCapability> Probe(HttpStatusCode code, string body)
        {
            var handler = new StubHandler14b((_, _) =>
                Task.FromResult(HttpJson14b.JsonResp(code, body)));
            using var auth = new DashboardAuth(new Uri("https://hermes.example/"), handler);
            return await new AudioApi(auth).ProbeCapabilityAsync("p", CancellationToken.None);
        }

        // 15a: status-route failures/available:false say nothing about STT; only auth gates here.
        Assert.Equal(VoiceTransportCapability.Unknown,
            await Probe(HttpStatusCode.NotFound, """{"detail":"missing"}"""));
        Assert.Equal(VoiceTransportCapability.SetupRequired,
            await Probe(HttpStatusCode.Unauthorized, """{"detail":"auth"}"""));
        Assert.Equal(VoiceTransportCapability.SetupRequired,
            await Probe(HttpStatusCode.Forbidden, """{"detail":"forbidden"}"""));
        Assert.Equal(VoiceTransportCapability.Unknown,
            await Probe((HttpStatusCode)429, """{"detail":"rate"}"""));
        Assert.Equal(VoiceTransportCapability.Unknown,
            await Probe(HttpStatusCode.OK, """{"mode":"chained","available":false,"reason":"no live"}"""));
        Assert.Equal(VoiceTransportCapability.Supported,
            await Probe(HttpStatusCode.OK, """{"mode":"chained","available":true}"""));
    }

    [Fact]
    public async Task AudioApi_rejects_oversized_audio_before_base64_alloc()
    {
        var handler = new StubHandler14b((_, _) =>
            Task.FromResult(HttpJson14b.JsonResp(HttpStatusCode.OK, """{"transcript":"x"}""")));
        using var auth = new DashboardAuth(new Uri("https://hermes.example/"), handler);
        var api = new AudioApi(auth);
        var huge = new byte[AudioApi.MaxUploadAudioBytes + 1];
        await Assert.ThrowsAsync<ArgumentException>(() =>
            api.TranscribeAsync(huge, "audio/wav", "p", CancellationToken.None));
    }

    [Fact]
    public async Task AudioApi_bounds_speak_data_url_and_unsupported_mime()
    {
        var hugeB64 = new string('A', AudioApi.MaxDataUrlChars + 8);
        var handler = new StubHandler14b((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/api/audio/speak", StringComparison.Ordinal))
                return Task.FromResult(HttpJson14b.JsonResp(HttpStatusCode.OK,
                    $"{{\"data_url\":\"data:audio/mpeg;base64,{hugeB64}\",\"mime_type\":\"audio/mpeg\"}}"));
            return Task.FromResult(HttpJson14b.JsonResp(HttpStatusCode.NotFound, "{}"));
        });
        using var auth = new DashboardAuth(new Uri("https://hermes.example/"), handler);
        var api = new AudioApi(auth);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            api.SpeakAsync("hi", "p", CancellationToken.None));
    }

    [Fact]
    public async Task AudioApi_rejects_non_audio_data_url_mime()
    {
        var b64 = Convert.ToBase64String(Encoding.ASCII.GetBytes("xx"));
        var handler = new StubHandler14b((_, _) =>
            Task.FromResult(HttpJson14b.JsonResp(HttpStatusCode.OK,
                $"{{\"data_url\":\"data:text/html;base64,{b64}\",\"mime_type\":\"text/html\"}}")));
        using var auth = new DashboardAuth(new Uri("https://hermes.example/"), handler);
        var api = new AudioApi(auth);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            api.SpeakAsync("hi", "p", CancellationToken.None));
    }

    [Fact]
    public async Task Speech_player_honors_cancellation_and_session_isolation()
    {
        var player = new FakeSpeechPlayer { HoldMs = 300 };
        using var cts = new CancellationTokenSource();
        var play = player.PlayAsync(new SpokenAudio(TinyWav(), "audio/wav"), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => play);
        Assert.True(player.StopCount >= 1);

        // Old "media ended" must not stop a newer session.
        player.HoldMs = 5_000;
        var play2 = player.PlayAsync(new SpokenAudio(TinyWav(), "audio/wav"), CancellationToken.None);
        var session = player.CurrentSessionId;
        Assert.False(player.IsIdle);
        player.SimulateMediaEnded(session - 1);
        Assert.Equal(session, player.CurrentSessionId);
        Assert.False(player.IsIdle);
        player.Stop();
        Assert.True(player.IsIdle);
        try { await play2; } catch (OperationCanceledException) { /* stop cancelled hold */ }
    }

    [Fact]
    public void Speech_playback_policy_forbids_silent_private_persist_claim()
    {
        Assert.False(SpeechPlaybackPolicy.AllowsSilentPrivatePersistence);
        Assert.False(SpeechPlaybackPolicy.AllowsShellExecution);
    }

    private static async Task WaitUntil(Func<bool> pred, int timeoutMs)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            if (pred()) return;
            await Task.Delay(10);
        }
        Assert.True(pred(), "condition not met in time");
    }
}

file sealed class StubHandler14b(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => impl(request, cancellationToken);
}

file static class HttpJson14b
{
    public static HttpResponseMessage JsonResp(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
