namespace Hikyaku.Core;

public enum VoicePhase
{
    Off,
    Recording,
    Preview,
    Transcribing,
    PermissionDenied,
    Unsupported,
    Error
}

public enum VoiceTransportCapability
{
    Unknown,
    Supported,
    Unsupported,
    SetupRequired
}

/// <summary>GPT-Live (WebRTC) readiness — independent of chained STT/TTS dictation.</summary>
public enum VoiceLiveAvailability
{
    Unknown,
    Unavailable,
    Available
}

/// <summary>
/// Probe result split by capability. <see cref="Dictation"/> gates STT/TTS only; <see cref="VoiceLive"/>
/// reflects <c>voice-live/status</c> and must never gate dictation.
/// </summary>
public sealed record AudioProbe(VoiceTransportCapability Dictation, VoiceLiveAvailability VoiceLive);

public enum MicrophoneCaptureError
{
    None,
    PermissionDenied,
    NoDevice,
    DeviceBusy,
    Disconnected,
    Failed
}

public sealed record VoiceScope(ProfileKey Profile, string ChatId);

public sealed record VoiceAudioClip(byte[] Bytes, string MimeType);

public sealed record SpokenAudio(byte[] Bytes, string MimeType);

public sealed record VoiceLiveStatus(string Mode, bool Available, string? Reason = null, string? Model = null, string? Voice = null);

public sealed record VoiceActionResult(bool Ok, string? Error = null);

/// <summary>Android VoiceActivity.maxMs = 60s; WAV capture only for preview/transcribe upload.</summary>
public sealed record VoiceCaptureLimits(int MaxDurationMs, int MaxBytes, int SampleRateHz, string CaptureMime)
{
    public static VoiceCaptureLimits Default { get; } = new(
        MaxDurationMs: 60_000,
        MaxBytes: 3 * 1024 * 1024,
        SampleRateHz: 16_000,
        CaptureMime: "audio/wav");
}

/// <summary>Local playback only — no remote URL fetch, no shell, no silent private persist.</summary>
public static class SpeechPlaybackPolicy
{
    public const bool AllowsShellExecution = false;
    /// <summary>
    /// MPEG may require an ephemeral temp file for MediaPlayer; callers must delete on stop/cancel
    /// and must not silently retain private audio. WAV prefers in-memory streams.
    /// </summary>
    public const bool AllowsSilentPrivatePersistence = false;

    public static bool IsSupportedMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime)) return false;
        var m = mime.Trim().ToLowerInvariant();
        return m is "audio/wav" or "audio/wave" or "audio/x-wav" or "audio/mpeg" or "audio/mp3";
    }

    public static bool AllowsRemoteUrl(string? url) => false;
}

/// <summary>PCM16 mono → WAV (BookahHermes AndroidVoice.wav).</summary>
public static class WavBuilder
{
    public static byte[] FromPcm16Mono(byte[] pcm, int sampleRateHz)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        var dataLen = pcm.Length;
        var wav = new byte[44 + dataLen];
        WriteAscii(wav, 0, "RIFF");
        WriteInt32(wav, 4, 36 + dataLen);
        WriteAscii(wav, 8, "WAVE");
        WriteAscii(wav, 12, "fmt ");
        WriteInt32(wav, 16, 16);
        WriteInt16(wav, 20, 1); // PCM
        WriteInt16(wav, 22, 1); // mono
        WriteInt32(wav, 24, sampleRateHz);
        WriteInt32(wav, 28, sampleRateHz * 2);
        WriteInt16(wav, 32, 2);
        WriteInt16(wav, 34, 16);
        WriteAscii(wav, 36, "data");
        WriteInt32(wav, 40, dataLen);
        Buffer.BlockCopy(pcm, 0, wav, 44, dataLen);
        return wav;
    }

    private static void WriteAscii(byte[] buf, int offset, string s)
    {
        for (var i = 0; i < s.Length; i++) buf[offset + i] = (byte)s[i];
    }

    private static void WriteInt16(byte[] buf, int offset, short value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteInt32(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }
}
