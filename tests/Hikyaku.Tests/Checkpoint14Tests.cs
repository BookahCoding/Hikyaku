using System.Net;
using System.Text;
using System.Text.Json;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Checkpoint 14: explicit mic opt-in, bounded capture/preview/cancel/scope/playback,
/// source-backed AudioApi fixtures — no real microphone or live Hermes.
/// </summary>
public class Checkpoint14Tests
{
    private static VoiceScope Scope(string chat = "chat-1") =>
        new(new ProfileKey("gw", "profile-a"), chat);

    private static byte[] TinyWav(int pcmBytes = 320) =>
        WavBuilder.FromPcm16Mono(new byte[pcmBytes], 16_000);

    [Fact]
    public void Default_phase_is_off_and_not_recording()
    {
        using var ctl = new VoiceCaptureController(new FakeMicrophoneCapture(), new FixtureAudioTransport(), new FakeSpeechPlayer());
        Assert.Equal(VoicePhase.Off, ctl.Phase);
        Assert.False(ctl.IsRecording);
        Assert.False(ctl.HasPreview);
        Assert.Null(ctl.PendingAudio);
    }

    [Fact]
    public async Task Gesture_start_records_then_stop_enters_preview_without_autosend()
    {
        var mic = new FakeMicrophoneCapture { NextPcm = new byte[640] };
        using var ctl = new VoiceCaptureController(mic, new FixtureAudioTransport(), new FakeSpeechPlayer());
        ctl.BindScope(Scope());
        var start = await ctl.StartCaptureAsync(userGesture: true);
        Assert.True(start.Ok, start.Error);
        Assert.Equal(VoicePhase.Recording, ctl.Phase);
        Assert.True(ctl.IsRecording);
        Assert.True(mic.IsCapturing);

        var stop = ctl.StopCapture();
        Assert.True(stop.Ok, stop.Error);
        Assert.Equal(VoicePhase.Preview, ctl.Phase);
        Assert.True(ctl.HasPreview);
        Assert.NotNull(ctl.PendingAudio);
        Assert.Equal("audio/wav", ctl.PendingAudio!.MimeType);
        Assert.Null(ctl.Transcript);
        Assert.False(ctl.DidAutoSend);
    }

    [Fact]
    public async Task Start_without_user_gesture_is_rejected()
    {
        using var ctl = new VoiceCaptureController(new FakeMicrophoneCapture(), new FixtureAudioTransport(), new FakeSpeechPlayer());
        ctl.BindScope(Scope());
        var start = await ctl.StartCaptureAsync(userGesture: false);
        Assert.False(start.Ok);
        Assert.Contains("gesture", start.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(VoicePhase.Off, ctl.Phase);
    }

    [Fact]
    public async Task Permission_denied_stops_and_discards()
    {
        var mic = new FakeMicrophoneCapture { NextError = MicrophoneCaptureError.PermissionDenied };
        using var ctl = new VoiceCaptureController(mic, new FixtureAudioTransport(), new FakeSpeechPlayer());
        ctl.BindScope(Scope());
        var start = await ctl.StartCaptureAsync(userGesture: true);
        Assert.False(start.Ok);
        Assert.Equal(VoicePhase.PermissionDenied, ctl.Phase);
        Assert.Null(ctl.PendingAudio);
        Assert.False(mic.IsCapturing);
    }

    [Fact]
    public async Task Device_busy_and_no_device_are_honest()
    {
        using var busyCtl = new VoiceCaptureController(
            new FakeMicrophoneCapture { NextError = MicrophoneCaptureError.DeviceBusy },
            new FixtureAudioTransport(), new FakeSpeechPlayer());
        busyCtl.BindScope(Scope());
        var busy = await busyCtl.StartCaptureAsync(userGesture: true);
        Assert.False(busy.Ok);
        Assert.Contains("busy", busy.Error!, StringComparison.OrdinalIgnoreCase);

        using var noneCtl = new VoiceCaptureController(
            new FakeMicrophoneCapture { NextError = MicrophoneCaptureError.NoDevice },
            new FixtureAudioTransport(), new FakeSpeechPlayer());
        noneCtl.BindScope(Scope());
        var none = await noneCtl.StartCaptureAsync(userGesture: true);
        Assert.False(none.Ok);
        Assert.Contains("device", none.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancel_preview_discards_audio()
    {
        var mic = new FakeMicrophoneCapture { NextPcm = new byte[640] };
        using var ctl = new VoiceCaptureController(mic, new FixtureAudioTransport(), new FakeSpeechPlayer());
        ctl.BindScope(Scope());
        Assert.True((await ctl.StartCaptureAsync(true)).Ok);
        Assert.True(ctl.StopCapture().Ok);
        Assert.True(ctl.HasPreview);
        ctl.CancelPreview();
        Assert.Equal(VoicePhase.Off, ctl.Phase);
        Assert.False(ctl.HasPreview);
        Assert.Null(ctl.PendingAudio);
        Assert.Null(ctl.Transcript);
    }

    [Fact]
    public async Task Profile_or_chat_switch_discards_inflight_and_preview()
    {
        var mic = new FakeMicrophoneCapture { NextPcm = new byte[640], HoldUntilFinish = true };
        using var ctl = new VoiceCaptureController(mic, new FixtureAudioTransport(), new FakeSpeechPlayer());
        ctl.BindScope(Scope("chat-a"));
        Assert.True((await ctl.StartCaptureAsync(true)).Ok);
        Assert.True(ctl.IsRecording);

        ctl.BindScope(Scope("chat-b"));
        Assert.Equal(VoicePhase.Off, ctl.Phase);
        Assert.False(ctl.IsRecording);
        Assert.False(mic.IsCapturing);
        Assert.Null(ctl.PendingAudio);

        // Late finish after switch must not resurrect preview.
        mic.ReleaseHeldCapture(TinyWav());
        await Task.Delay(20);
        Assert.Null(ctl.PendingAudio);
        Assert.Equal(VoicePhase.Off, ctl.Phase);
    }

    [Fact]
    public async Task Max_duration_auto_stops_into_preview()
    {
        var mic = new FakeMicrophoneCapture { NextPcm = new byte[640], EmitDurationMs = 61_000 };
        using var ctl = new VoiceCaptureController(
            mic, new FixtureAudioTransport(), new FakeSpeechPlayer(),
            limits: VoiceCaptureLimits.Default with { MaxDurationMs = 60_000 });
        ctl.BindScope(Scope());
        Assert.True((await ctl.StartCaptureAsync(true)).Ok);
        // Fake reports duration over limit → controller stops.
        await ctl.PumpCaptureAsync();
        Assert.Equal(VoicePhase.Preview, ctl.Phase);
        Assert.True(ctl.HasPreview);
    }

    [Fact]
    public async Task Max_bytes_rejects_oversized_capture()
    {
        var huge = new byte[VoiceCaptureLimits.Default.MaxBytes + 100];
        var mic = new FakeMicrophoneCapture { NextPcm = huge };
        using var ctl = new VoiceCaptureController(
            mic, new FixtureAudioTransport(), new FakeSpeechPlayer(),
            limits: VoiceCaptureLimits.Default with { MaxBytes = 1024 });
        ctl.BindScope(Scope());
        Assert.True((await ctl.StartCaptureAsync(true)).Ok);
        var stop = ctl.StopCapture();
        Assert.False(stop.Ok);
        Assert.Contains("size", stop.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ctl.PendingAudio);
        Assert.Equal(VoicePhase.Off, ctl.Phase);
    }

    [Fact]
    public async Task Unsupported_transport_is_gated_without_capture()
    {
        var transport = new FixtureAudioTransport { Capability = VoiceTransportCapability.Unsupported };
        var mic = new FakeMicrophoneCapture();
        using var ctl = new VoiceCaptureController(mic, transport, new FakeSpeechPlayer());
        ctl.BindScope(Scope());
        await ctl.RefreshCapabilityAsync();
        Assert.Equal(VoicePhase.Unsupported, ctl.Phase);
        Assert.Contains("unavailable", ctl.Status!, StringComparison.OrdinalIgnoreCase);
        var start = await ctl.StartCaptureAsync(true);
        Assert.False(start.Ok);
        Assert.False(mic.IsCapturing);
    }

    [Fact]
    public async Task Transcribe_uses_source_backed_fixture_and_fills_editable_transcript()
    {
        var transport = new FixtureAudioTransport { Transcript = "  hello from fixture  " };
        var mic = new FakeMicrophoneCapture { NextPcm = new byte[640] };
        using var ctl = new VoiceCaptureController(mic, transport, new FakeSpeechPlayer());
        ctl.BindScope(Scope());
        Assert.True((await ctl.StartCaptureAsync(true)).Ok);
        Assert.True(ctl.StopCapture().Ok);
        var tr = await ctl.TranscribePreviewAsync();
        Assert.True(tr.Ok, tr.Error);
        Assert.Equal("hello from fixture", ctl.Transcript);
        Assert.Equal(1, transport.TranscribeCalls);
        Assert.Equal("audio/wav", transport.LastMime);
        Assert.False(ctl.DidAutoSend);

        ctl.EditTranscript("edited hello");
        Assert.Equal("edited hello", ctl.Transcript);
        var text = ctl.TakeTranscriptForComposer();
        Assert.Equal("edited hello", text);
        Assert.Null(ctl.Transcript);
        Assert.False(ctl.HasPreview); // consented move to composer discards pending audio
        Assert.False(ctl.DidAutoSend);
    }

    [Fact]
    public async Task Late_transcribe_after_cancel_is_ignored()
    {
        var transport = new FixtureAudioTransport { DelayMs = 80, Transcript = "late" };
        var mic = new FakeMicrophoneCapture { NextPcm = new byte[640] };
        using var ctl = new VoiceCaptureController(mic, transport, new FakeSpeechPlayer());
        ctl.BindScope(Scope());
        Assert.True((await ctl.StartCaptureAsync(true)).Ok);
        Assert.True(ctl.StopCapture().Ok);
        var task = ctl.TranscribePreviewAsync();
        ctl.CancelPreview();
        var result = await task;
        Assert.False(result.Ok);
        Assert.Null(ctl.Transcript);
        Assert.Equal(VoicePhase.Off, ctl.Phase);
    }

    [Fact]
    public async Task Playback_preview_then_stop_uses_player_not_shell()
    {
        var player = new FakeSpeechPlayer();
        var mic = new FakeMicrophoneCapture { NextPcm = new byte[640] };
        using var ctl = new VoiceCaptureController(mic, new FixtureAudioTransport(), player);
        ctl.BindScope(Scope());
        Assert.True((await ctl.StartCaptureAsync(true)).Ok);
        Assert.True(ctl.StopCapture().Ok);
        Assert.True((await ctl.PlayPreviewAsync()).Ok);
        Assert.Equal(1, player.PlayCount);
        Assert.Equal("audio/wav", player.LastMime);
        var stopsBefore = player.StopCount;
        ctl.StopPlayback();
        Assert.True(player.StopCount > stopsBefore);
    }

    [Fact]
    public async Task Playback_rejects_unsupported_or_corrupt_payload()
    {
        var player = new FakeSpeechPlayer { FailNext = true };
        var mic = new FakeMicrophoneCapture { NextPcm = new byte[640] };
        using var ctl = new VoiceCaptureController(mic, new FixtureAudioTransport(), player);
        ctl.BindScope(Scope());
        Assert.True((await ctl.StartCaptureAsync(true)).Ok);
        Assert.True(ctl.StopCapture().Ok);
        var bad = await ctl.PlayPreviewAsync();
        Assert.False(bad.Ok);

        var unsupported = await ctl.PlayReplyAsync(new SpokenAudio(new byte[] { 1, 2, 3 }, "application/octet-stream"));
        Assert.False(unsupported.Ok);
        Assert.Contains("unsupported", unsupported.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Disconnect_or_close_disposes_capture_and_discards()
    {
        var mic = new FakeMicrophoneCapture { NextPcm = new byte[640], HoldUntilFinish = true };
        var ctl = new VoiceCaptureController(mic, new FixtureAudioTransport(), new FakeSpeechPlayer());
        ctl.BindScope(Scope());
        Assert.True((await ctl.StartCaptureAsync(true)).Ok);
        ctl.Dispose();
        Assert.False(mic.IsCapturing);
        Assert.Null(ctl.PendingAudio);
    }

    [Fact]
    public async Task AudioApi_transcribe_and_speak_match_android_wire_shape()
    {
        string? capturedPath = null;
        string? capturedBody = null;
        string? capturedQuery = null;
        var handler = new StubHandler(async (req, ct) =>
        {
            capturedPath = req.RequestUri!.AbsolutePath;
            capturedQuery = req.RequestUri.Query;
            capturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            if (capturedPath.EndsWith("/api/audio/transcribe", StringComparison.Ordinal))
            {
                return Checkpoint14Http.JsonResp(HttpStatusCode.OK, """{"transcript":"wire ok"}""");
            }
            if (capturedPath.EndsWith("/api/audio/speak", StringComparison.Ordinal))
            {
                var b64 = Convert.ToBase64String(Encoding.ASCII.GetBytes("ID3"));
                return Checkpoint14Http.JsonResp(HttpStatusCode.OK, $"{{\"data_url\":\"data:audio/mpeg;base64,{b64}\",\"mime_type\":\"audio/mpeg\"}}");
            }
            if (capturedPath.EndsWith("/api/audio/voice-live/status", StringComparison.Ordinal))
            {
                return Checkpoint14Http.JsonResp(HttpStatusCode.OK, """{"mode":"chained","available":true}""");
            }
            return Checkpoint14Http.JsonResp(HttpStatusCode.NotFound, """{"error":"missing"}""");
        });
        using var auth = new DashboardAuth(new Uri("https://hermes.example/"), handler);
        var api = new AudioApi(auth);

        var text = await api.TranscribeAsync(TinyWav(), "audio/wav", "profile-a", CancellationToken.None);
        Assert.Equal("wire ok", text);
        Assert.Equal("/api/audio/transcribe", capturedPath);
        Assert.Contains("profile=profile-a", capturedQuery, StringComparison.Ordinal);
        using (var doc = JsonDocument.Parse(capturedBody!))
        {
            Assert.True(doc.RootElement.TryGetProperty("data_url", out var du));
            Assert.StartsWith("data:audio/wav;base64,", du.GetString());
            Assert.Equal("audio/wav", doc.RootElement.GetProperty("mime_type").GetString());
        }

        var spoken = await api.SpeakAsync("hi", "profile-a", CancellationToken.None);
        Assert.Equal("audio/mpeg", spoken.MimeType);
        Assert.True(spoken.Bytes.Length > 0);

        var status = await api.GetVoiceLiveStatusAsync("profile-a", CancellationToken.None);
        Assert.Equal("chained", status.Mode);
        Assert.True(status.Available);
    }

    [Fact]
    public async Task AudioApi_status_404_is_not_dictation_unsupported()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(Checkpoint14Http.JsonResp(HttpStatusCode.NotFound, """{"detail":"not found"}""")));
        using var auth = new DashboardAuth(new Uri("https://hermes.example/"), handler);
        var api = new AudioApi(auth);
        var cap = await api.ProbeCapabilityAsync("p", CancellationToken.None);
        Assert.Equal(VoiceTransportCapability.Unknown, cap); // 15a: status 404 ≠ STT absent
    }

    [Fact]
    public async Task Http_audio_transport_never_autosends_and_scopes_profile()
    {
        var handler = new StubHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/api/audio/voice-live/status", StringComparison.Ordinal))
                return Task.FromResult(Checkpoint14Http.JsonResp(HttpStatusCode.OK, """{"mode":"chained","available":true}"""));
            if (req.RequestUri.AbsolutePath.EndsWith("/api/audio/transcribe", StringComparison.Ordinal))
            {
                Assert.Contains("profile=profile-a", req.RequestUri.Query, StringComparison.Ordinal);
                return Task.FromResult(Checkpoint14Http.JsonResp(HttpStatusCode.OK, """{"transcript":"scoped"}"""));
            }
            return Task.FromResult(Checkpoint14Http.JsonResp(HttpStatusCode.NotFound, "{}"));
        });
        using var auth = new DashboardAuth(new Uri("https://hermes.example/"), handler);
        var transport = new HttpAudioTransport(auth);
        Assert.Equal(VoiceTransportCapability.Supported, await transport.ProbeAsync("profile-a", default));
        var text = await transport.TranscribeAsync(TinyWav(), "audio/wav", "profile-a", default);
        Assert.Equal("scoped", text);
    }

    [Fact]
    public void Wav_builder_produces_riff_header()
    {
        var wav = WavBuilder.FromPcm16Mono(new byte[32], 16_000);
        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'I', wav[1]);
        Assert.Equal((byte)'F', wav[2]);
        Assert.Equal((byte)'F', wav[3]);
        Assert.True(wav.Length > 44);
    }

    [Fact]
    public void Speech_player_accepts_only_safe_local_mimes()
    {
        Assert.True(SpeechPlaybackPolicy.IsSupportedMime("audio/wav"));
        Assert.True(SpeechPlaybackPolicy.IsSupportedMime("audio/mpeg"));
        Assert.False(SpeechPlaybackPolicy.IsSupportedMime("text/html"));
        Assert.False(SpeechPlaybackPolicy.IsSupportedMime("application/javascript"));
        Assert.False(SpeechPlaybackPolicy.AllowsRemoteUrl("https://evil.example/a.mp3"));
        Assert.False(SpeechPlaybackPolicy.AllowsShellExecution);
    }
}

public class Checkpoint14DocsTests
{
    [Fact]
    public void Handoff_and_parity_mark_voice_boundary()
    {
        var root = FindRepoRoot();
        var handoff = File.ReadAllText(Path.Combine(root, "docs", "HANDOFF.md"));
        Assert.Contains("Checkpoint 10", handoff);
        Assert.Contains("Checkpoint 12", handoff);
        Assert.Contains("Checkpoint 14", handoff);
        Assert.Contains("voice", handoff, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pending", handoff, StringComparison.OrdinalIgnoreCase);

        var android = File.ReadAllText(Path.Combine(root, "docs", "ANDROID-PARITY.md"));
        Assert.Contains("Voice", android);
        Assert.Contains("fixture", android, StringComparison.OrdinalIgnoreCase);

        var desktop = File.ReadAllText(Path.Combine(root, "docs", "DESKTOP-PARITY.md"));
        Assert.Contains("Voice", desktop);
        Assert.Contains("fixture", desktop, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hikyaku.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("repo root not found");
    }
}

file sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => impl(request, cancellationToken);
}

file static class Checkpoint14Http
{
    public static HttpResponseMessage JsonResp(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
