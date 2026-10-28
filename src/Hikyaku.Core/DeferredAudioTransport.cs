namespace Hikyaku.Core;

/// <summary>
/// Production audio transport: uses dashboard <see cref="HttpAudioTransport"/> when auth exists;
/// otherwise reports setup-required (no invented endpoints, no silent fallback to live providers).
/// </summary>
public sealed class DeferredAudioTransport(Func<DashboardAuth?> authFactory) : IAudioTransport
{
    public Task<VoiceTransportCapability> ProbeAsync(string? profile, CancellationToken ct)
    {
        var auth = authFactory();
        if (auth is null) return Task.FromResult(VoiceTransportCapability.SetupRequired);
        return new HttpAudioTransport(auth).ProbeAsync(profile, ct);
    }

    public Task<string> TranscribeAsync(byte[] audio, string mimeType, string? profile, CancellationToken ct)
    {
        var auth = authFactory() ?? throw new InvalidOperationException(VoiceCaptureController.SetupRequiredCopy);
        return new HttpAudioTransport(auth).TranscribeAsync(audio, mimeType, profile, ct);
    }

    public Task<SpokenAudio> SpeakAsync(string text, string? profile, CancellationToken ct)
    {
        var auth = authFactory() ?? throw new InvalidOperationException(VoiceCaptureController.SetupRequiredCopy);
        return new HttpAudioTransport(auth).SpeakAsync(text, profile, ct);
    }
}
