using System.Net;
using System.Text;
using System.Text.Json;

namespace Hikyaku.Core;

/// <summary>
/// Hermes dashboard audio relay — BookahHermes <c>AudioApi.kt</c> @ 5f00986b.
/// Paths: POST api/audio/transcribe|speak, GET api/audio/voice-live/status.
/// Does not invent OpenAI-compatible client endpoints.
/// </summary>
public sealed class AudioApi(DashboardAuth auth)
{
    public const int SlowTimeoutMs = 120_000;
    /// <summary>Max raw audio accepted for upload before base64 allocation.</summary>
    public const int MaxUploadAudioBytes = 3 * 1024 * 1024;
    /// <summary>Max data URL character length (speak / transcribe responses).</summary>
    public const int MaxDataUrlChars = 4 * 1024 * 1024;
    /// <summary>Max JSON response body bytes read into memory.</summary>
    public const int MaxResponseBodyBytes = 5 * 1024 * 1024;
    /// <summary>Max transcript characters retained.</summary>
    public const int MaxTranscriptChars = 100_000;
    /// <summary>Max decoded spoken audio bytes.</summary>
    public const int MaxDecodedAudioBytes = 3 * 1024 * 1024;

    public async Task<string> TranscribeAsync(byte[] audio, string mimeType, string? profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (string.IsNullOrWhiteSpace(mimeType)) throw new ArgumentException("mime required", nameof(mimeType));
        if (!SpeechPlaybackPolicy.IsSupportedMime(mimeType) &&
            !mimeType.Equals("audio/wav", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("unsupported audio mime", nameof(mimeType));
        if (audio.LongLength > MaxUploadAudioBytes)
            throw new ArgumentException($"audio exceeds {MaxUploadAudioBytes} byte upload bound", nameof(audio));
        var dataUrl = "data:" + mimeType + ";base64," + Convert.ToBase64String(audio);
        if (dataUrl.Length > MaxDataUrlChars)
            throw new ArgumentException("data URL exceeds size bound", nameof(audio));
        var body = JsonSerializer.Serialize(new { data_url = dataUrl, mime_type = mimeType });
        using var req = new HttpRequestMessage(HttpMethod.Post, BuildUri("api/audio/transcribe", profile))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SlowTimeoutMs);
        using var resp = await auth.SendAsync(req, cts.Token).ConfigureAwait(false);
        EnsureOk(resp);
        using var doc = await ReadJsonBoundedAsync(resp, cts.Token).ConfigureAwait(false);
        var transcript = doc.RootElement.TryGetProperty("transcript", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? ""
            : "";
        if (transcript.Length > MaxTranscriptChars)
            transcript = transcript[..MaxTranscriptChars];
        return transcript.Trim();
    }

    public async Task<SpokenAudio> SpeakAsync(string text, string? profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxTranscriptChars)
            throw new ArgumentException("speak text exceeds bound", nameof(text));
        var body = JsonSerializer.Serialize(new { text });
        using var req = new HttpRequestMessage(HttpMethod.Post, BuildUri("api/audio/speak", profile))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SlowTimeoutMs);
        using var resp = await auth.SendAsync(req, cts.Token).ConfigureAwait(false);
        EnsureOk(resp);
        using var doc = await ReadJsonBoundedAsync(resp, cts.Token).ConfigureAwait(false);
        var dataUrl = doc.RootElement.TryGetProperty("data_url", out var du) ? du.GetString() ?? "" : "";
        if (dataUrl.Length > MaxDataUrlChars)
            throw new InvalidOperationException("Speak data_url exceeds size bound.");
        var mime = doc.RootElement.TryGetProperty("mime_type", out var mt) && mt.ValueKind == JsonValueKind.String
            ? mt.GetString()
            : null;
        mime ??= ExtractMimeFromDataUrl(dataUrl) ?? "audio/mpeg";
        if (!SpeechPlaybackPolicy.IsSupportedMime(mime))
            throw new InvalidOperationException("Speak response mime is not a supported audio type.");
        var dataMime = ExtractMimeFromDataUrl(dataUrl);
        if (dataMime is not null && !SpeechPlaybackPolicy.IsSupportedMime(dataMime))
            throw new InvalidOperationException("Speak data_url mime is not a supported audio type.");
        var b64 = dataUrl.Contains("base64,", StringComparison.Ordinal)
            ? dataUrl[(dataUrl.IndexOf("base64,", StringComparison.Ordinal) + "base64,".Length)..]
            : "";
        if (b64.Length > MaxDataUrlChars)
            throw new InvalidOperationException("Speak payload exceeds size bound.");
        var bytes = string.IsNullOrEmpty(b64) ? Array.Empty<byte>() : Convert.FromBase64String(b64);
        if (bytes.LongLength > MaxDecodedAudioBytes)
            throw new InvalidOperationException("Decoded speak audio exceeds size bound.");
        return new SpokenAudio(bytes, mime!);
    }

    public async Task<VoiceLiveStatus> GetVoiceLiveStatusAsync(string? profile, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, BuildUri("api/audio/voice-live/status", profile));
        using var resp = await auth.SendAsync(req, ct).ConfigureAwait(false);
        EnsureOk(resp);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        return new VoiceLiveStatus(
            root.TryGetProperty("mode", out var mode) ? mode.GetString() ?? "chained" : "chained",
            root.TryGetProperty("available", out var avail) && avail.ValueKind == JsonValueKind.True,
            root.TryGetProperty("reason", out var reason) ? reason.GetString() : null,
            root.TryGetProperty("model", out var model) ? model.GetString() : null,
            root.TryGetProperty("voice", out var voice) ? voice.GetString() : null);
    }

    /// <summary>Dictation (STT/TTS) capability only; see <see cref="ProbeAsync"/>.</summary>
    public async Task<VoiceTransportCapability> ProbeCapabilityAsync(string? profile, CancellationToken ct)
        => (await ProbeAsync(profile, ct).ConfigureAwait(false)).Dictation;

    /// <summary>
    /// Splits dictation from GPT-Live. Android treats <c>voice-live/status</c> as "which voice-chat mode, and can
    /// GPT-Live start"; a failing call "means chained". Only 401/403 (auth applies to all of /api/audio) gates
    /// dictation here. There is no side-effect-free STT probe, so otherwise dictation stays <c>Unknown</c>
    /// (attemptable) and the transcribe route is the authority: its 404 flips to Unsupported.
    /// <c>Supported</c> requires positive evidence (<c>available:true</c> with a chained-or-live body).
    /// </summary>
    public async Task<AudioProbe> ProbeAsync(string? profile, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BuildUri("api/audio/voice-live/status", profile));
            using var resp = await auth.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new AudioProbe(VoiceTransportCapability.SetupRequired, VoiceLiveAvailability.Unknown);
            if (!resp.IsSuccessStatusCode)
                // 404 = gateway without GPT-Live (chained); 429/5xx = transient. Neither proves STT absent.
                return new AudioProbe(VoiceTransportCapability.Unknown,
                    resp.StatusCode == HttpStatusCode.NotFound ? VoiceLiveAvailability.Unavailable : VoiceLiveAvailability.Unknown);

            using var doc = await ReadJsonBoundedAsync(resp, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var available = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True;
            var live = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String
                && m.GetString() == "gpt-live" && available;
            return new AudioProbe(
                // available:true only proves the profile's voice stack resolves; false/absent says nothing about STT.
                available ? VoiceTransportCapability.Supported : VoiceTransportCapability.Unknown,
                live ? VoiceLiveAvailability.Available : VoiceLiveAvailability.Unavailable);
        }
        catch (AuthFailedException)
        {
            return new AudioProbe(VoiceTransportCapability.SetupRequired, VoiceLiveAvailability.Unknown);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // Unreachable, oversize or malformed status body: unknown, never "unsupported".
            return new AudioProbe(VoiceTransportCapability.Unknown, VoiceLiveAvailability.Unknown);
        }
    }

    private Uri BuildUri(string relative, string? profile)
    {
        var path = relative + (string.IsNullOrWhiteSpace(profile) ? "" : "?profile=" + Uri.EscapeDataString(profile));
        return new Uri(auth.BaseUri, path);
    }

    private static string? ExtractMimeFromDataUrl(string dataUrl)
    {
        if (!dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        var semi = dataUrl.IndexOf(';');
        if (semi <= 5) return null;
        return dataUrl[5..semi];
    }

    private static void EnsureOk(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new AuthFailedException("session_expired", "Dashboard session expired — sign in again for voice.");
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new AudioTransportUnsupportedException("Voice audio API unavailable on this gateway.");
        throw new AuthFailedException("failed", $"Audio API HTTP {(int)resp.StatusCode}");
    }

    private static async Task<JsonDocument> ReadJsonBoundedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength is { } len && len > MaxResponseBodyBytes)
            throw new InvalidOperationException("Audio API response exceeds size bound.");
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var limited = new BoundedReadStream(stream, MaxResponseBodyBytes);
        return await JsonDocument.ParseAsync(limited, cancellationToken: ct).ConfigureAwait(false);
    }

    private sealed class BoundedReadStream(Stream inner, long maxBytes) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= maxBytes) throw new InvalidOperationException("Audio API response exceeds size bound.");
            var allowed = (int)Math.Min(count, maxBytes - _read);
            var n = inner.Read(buffer, offset, allowed);
            _read += n;
            return n;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_read >= maxBytes) throw new InvalidOperationException("Audio API response exceeds size bound.");
            var allowed = (int)Math.Min(count, maxBytes - _read);
            var n = await inner.ReadAsync(buffer.AsMemory(offset, allowed), cancellationToken).ConfigureAwait(false);
            _read += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class AudioTransportUnsupportedException(string message) : Exception(message);

public interface IAudioTransport
{
    Task<VoiceTransportCapability> ProbeAsync(string? profile, CancellationToken ct);
    Task<string> TranscribeAsync(byte[] audio, string mimeType, string? profile, CancellationToken ct);
    Task<SpokenAudio> SpeakAsync(string text, string? profile, CancellationToken ct);
}

/// <summary>Source-derived synthetic loopback — no live Hermes mutations.</summary>
public sealed class FixtureAudioTransport : IAudioTransport
{
    public VoiceTransportCapability Capability { get; set; } = VoiceTransportCapability.Supported;
    public string Transcript { get; set; } = "fixture transcript";
    public int DelayMs { get; set; }
    /// <summary>Simulates the transcribe route itself answering 404 (authoritative unsupported).</summary>
    public bool ThrowUnsupportedOnTranscribe { get; set; }
    public int TranscribeCalls { get; private set; }
    public string? LastMime { get; private set; }

    public Task<VoiceTransportCapability> ProbeAsync(string? profile, CancellationToken ct)
        => Task.FromResult(Capability);

    public async Task<string> TranscribeAsync(byte[] audio, string mimeType, string? profile, CancellationToken ct)
    {
        TranscribeCalls++;
        LastMime = mimeType;
        if (DelayMs > 0) await Task.Delay(DelayMs, ct).ConfigureAwait(false);
        if (Capability == VoiceTransportCapability.Unsupported || ThrowUnsupportedOnTranscribe)
            throw new AudioTransportUnsupportedException("Voice unavailable on this gateway.");
        return Transcript.Trim();
    }

    public int SpeakCalls { get; private set; }
    public string? LastSpokenText { get; private set; }
    public string? LastSpeakProfile { get; private set; }
    public int SpeakDelayMs { get; set; }
    /// <summary>Simulates the speak route answering 404 (no text-to-speech on this gateway).</summary>
    public bool ThrowUnsupportedOnSpeak { get; set; }
    public Exception? SpeakFailure { get; set; }
    public string SpeakMime { get; set; } = "audio/wav";
    public byte[] SpeakBytes { get; set; } = WavBuilder.FromPcm16Mono(new byte[64], 16_000);

    public async Task<SpokenAudio> SpeakAsync(string text, string? profile, CancellationToken ct)
    {
        SpeakCalls++;
        LastSpokenText = text;
        LastSpeakProfile = profile;
        if (SpeakDelayMs > 0) await Task.Delay(SpeakDelayMs, ct).ConfigureAwait(false);
        if (ThrowUnsupportedOnSpeak) throw new AudioTransportUnsupportedException("No speak route.");
        if (SpeakFailure is not null) throw SpeakFailure;
        return new SpokenAudio(SpeakBytes, SpeakMime);
    }
}

/// <summary>Live dashboard AudioApi wrapper (production). Tests use <see cref="FixtureAudioTransport"/>.</summary>
public sealed class HttpAudioTransport(DashboardAuth auth) : IAudioTransport
{
    private readonly AudioApi _api = new(auth);

    public Task<VoiceTransportCapability> ProbeAsync(string? profile, CancellationToken ct)
        => _api.ProbeCapabilityAsync(profile, ct);

    public Task<string> TranscribeAsync(byte[] audio, string mimeType, string? profile, CancellationToken ct)
        => _api.TranscribeAsync(audio, mimeType, profile, ct);

    public Task<SpokenAudio> SpeakAsync(string text, string? profile, CancellationToken ct)
        => _api.SpeakAsync(text, profile, ct);
}
