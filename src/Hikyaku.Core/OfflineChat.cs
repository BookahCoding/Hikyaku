using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hikyaku.Core;

/// <summary>Encrypts offline blobs. App uses DPAPI; tests use a fixed key.</summary>
public interface IByteSealer
{
    byte[] Seal(byte[] plain);
    byte[]? Unseal(byte[] sealedBytes);
}

/// <summary>AES-256-GCM sealer with an in-memory or provided 32-byte key (BookahHermes Sealer.kt shape).</summary>
public sealed class AesGcmSealer : IByteSealer
{
    private readonly byte[] _key;
    public AesGcmSealer(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("AES-256 key must be 32 bytes.", nameof(key));
        _key = key;
    }

    public static AesGcmSealer CreateRandom() => new(RandomNumberGenerator.GetBytes(32));

    public byte[] Seal(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(_key, 16);
        gcm.Encrypt(nonce, plain, cipher, tag);
        var sealedBytes = new byte[12 + cipher.Length + 16];
        Buffer.BlockCopy(nonce, 0, sealedBytes, 0, 12);
        Buffer.BlockCopy(cipher, 0, sealedBytes, 12, cipher.Length);
        Buffer.BlockCopy(tag, 0, sealedBytes, 12 + cipher.Length, 16);
        return sealedBytes;
    }

    public byte[]? Unseal(byte[] sealedBytes)
    {
        if (sealedBytes.Length < 12 + 16) return null;
        try
        {
            var nonce = sealedBytes.AsSpan(0, 12);
            var tag = sealedBytes.AsSpan(sealedBytes.Length - 16, 16);
            var cipher = sealedBytes.AsSpan(12, sealedBytes.Length - 12 - 16);
            var plain = new byte[cipher.Length];
            using var gcm = new AesGcm(_key, 16);
            gcm.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
        catch (CryptographicException) { return null; }
    }
}

/// <summary>
/// Composer drafts keyed by gateway+profile (BookahHermes DraftStore).
/// The composer writes on every keystroke and again when the chat changes, so two saves are often in flight.
/// Each save loads the whole file, changes one entry and writes it back; they run one at a time so a slower
/// save cannot replace a newer draft or drop another profile's draft.
/// </summary>
public sealed class DraftStore(IPreferenceStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> GetAsync(ProfileKey key, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map = await LoadAsync(ct).ConfigureAwait(false);
            return map.TryGetValue(KeyOf(key), out var d) ? d : "";
        }
        finally { _gate.Release(); }
    }

    public async Task SetAsync(ProfileKey key, string draft, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map = await LoadAsync(ct).ConfigureAwait(false);
            var k = KeyOf(key);
            if (string.IsNullOrEmpty(draft)) map.Remove(k);
            else map[k] = draft;
            await SaveAsync(map, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static string KeyOf(ProfileKey k) => k.GatewayId + "\0" + k.ProfileId;

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken ct)
    {
        var raw = await store.LoadAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(raw)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var dto = JsonSerializer.Deserialize<DraftDto>(raw, OfflineJson.Options);
            return dto?.Drafts?.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException) { return new Dictionary<string, string>(StringComparer.Ordinal); }
    }

    private Task SaveAsync(Dictionary<string, string> map, CancellationToken ct)
        => store.SaveAsync(JsonSerializer.Serialize(new DraftDto { Drafts = map }, OfflineJson.Options), ct);

    private sealed class DraftDto
    {
        public Dictionary<string, string> Drafts { get; set; } = new();
    }
}

public sealed record OfflineTranscriptMessage(string Role, string Content);

public sealed record OfflineTranscript(
    string SessionId,
    ProfileKey Profile,
    IReadOnlyList<OfflineTranscriptMessage> Messages,
    long SavedCopyAtUnix);

/// <summary>
/// Encrypted offline chat transcripts keyed by gateway+profile+session (BookahHermes OfflineCache).
/// Never uploaded. <see cref="OfflineTranscript.SavedCopyAtUnix"/> marks stale offline UI vs live.
/// </summary>
public sealed class OfflineChatCache
{
    public const int MaxTranscripts = 50;
    public const int MaxRowsPerTranscript = 200;
    public const int MaxSealedBytes = 1_000_000;

    private readonly IPreferenceStore _store;
    private readonly IByteSealer _sealer;
    private readonly Func<ProfileKey, bool> _allows;

    public OfflineChatCache(IPreferenceStore store, IByteSealer sealer, Func<ProfileKey, bool> allows)
    {
        _store = store;
        _sealer = sealer;
        _allows = allows;
    }

    public async Task SaveTranscriptAsync(OfflineTranscript transcript, CancellationToken ct = default)
    {
        if (!_allows(transcript.Profile)) return;
        var rows = transcript.Messages.TakeLast(MaxRowsPerTranscript).ToList();
        var payload = SerializeTranscript(transcript.SessionId, transcript.Profile, rows, transcript.SavedCopyAtUnix);
        if (payload.Length > MaxSealedBytes)
        {
            rows = [new OfflineTranscriptMessage("system", "[Offline copy too large; truncated]")];
            payload = SerializeTranscript(transcript.SessionId, transcript.Profile, rows, transcript.SavedCopyAtUnix);
        }
        var sealedBytes = _sealer.Seal(payload);
        var index = await LoadIndexAsync(ct).ConfigureAwait(false);
        var key = CacheKey(transcript.Profile, transcript.SessionId);
        index[key] = Convert.ToBase64String(sealedBytes);
        while (index.Count > MaxTranscripts)
        {
            var drop = index.Keys.First();
            index.Remove(drop);
        }
        await SaveIndexAsync(index, ct).ConfigureAwait(false);
    }

    public async Task<OfflineTranscript?> LoadTranscriptAsync(ProfileKey profile, string sessionId, CancellationToken ct = default)
    {
        if (!_allows(profile)) return null;
        var index = await LoadIndexAsync(ct).ConfigureAwait(false);
        if (!index.TryGetValue(CacheKey(profile, sessionId), out var b64)) return null;
        byte[] sealedBytes;
        try { sealedBytes = Convert.FromBase64String(b64); }
        catch (FormatException) { return null; }
        var plain = _sealer.Unseal(sealedBytes);
        if (plain is null) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<TranscriptDto>(plain, OfflineJson.Options);
            if (dto is null || string.IsNullOrEmpty(dto.SessionId)) return null;
            var key = new ProfileKey(dto.GatewayId!, dto.ProfileId!);
            if (key != profile || !_allows(key)) return null;
            return new OfflineTranscript(dto.SessionId, key,
                (dto.Messages ?? []).Select(m => new OfflineTranscriptMessage(m.Role ?? "", m.Content ?? "")).ToList(),
                dto.SavedCopyAtUnix);
        }
        catch (JsonException) { return null; }
    }

    private static byte[] SerializeTranscript(string sessionId, ProfileKey profile,
        List<OfflineTranscriptMessage> rows, long savedAt) =>
        JsonSerializer.SerializeToUtf8Bytes(new TranscriptDto
        {
            SessionId = sessionId,
            GatewayId = profile.GatewayId,
            ProfileId = profile.ProfileId,
            Messages = rows.Select(m => new MsgDto { Role = m.Role, Content = m.Content }).ToList(),
            SavedCopyAtUnix = savedAt
        }, OfflineJson.Options);

    public async Task DropProfileAsync(ProfileKey profile, CancellationToken ct = default)
    {
        var index = await LoadIndexAsync(ct).ConfigureAwait(false);
        var prefix = profile.GatewayId + "/" + profile.ProfileId + "/";
        foreach (var k in index.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            index.Remove(k);
        await SaveIndexAsync(index, ct).ConfigureAwait(false);
    }

    private static string CacheKey(ProfileKey p, string sessionId) =>
        p.GatewayId + "/" + p.ProfileId + "/" + sessionId;

    private async Task<Dictionary<string, string>> LoadIndexAsync(CancellationToken ct)
    {
        var raw = await _store.LoadAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(raw)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var dto = JsonSerializer.Deserialize<IndexDto>(raw, OfflineJson.Options);
            return dto?.Items ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException) { return new Dictionary<string, string>(StringComparer.Ordinal); }
    }

    private Task SaveIndexAsync(Dictionary<string, string> index, CancellationToken ct)
        => _store.SaveAsync(JsonSerializer.Serialize(new IndexDto { Items = index }, OfflineJson.Options), ct);

    private sealed class IndexDto
    {
        public Dictionary<string, string> Items { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class TranscriptDto
    {
        public string? SessionId { get; set; }
        public string? GatewayId { get; set; }
        public string? ProfileId { get; set; }
        public List<MsgDto>? Messages { get; set; }
        public long SavedCopyAtUnix { get; set; }
    }

    private sealed class MsgDto
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
    }
}

internal static class OfflineJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
