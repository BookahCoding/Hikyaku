using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hikyaku.Core;

public sealed record BridgeFolder(string CanonicalRoot, bool Network, DateTimeOffset GrantedAtUtc);

public sealed record BridgeKeyFile(string CanonicalPath, DateTimeOffset GrantedAtUtc);

public sealed record BridgeProfile(
    IReadOnlyList<BridgeFolder> Folders,
    IReadOnlyList<BridgeKeyFile> KeyFiles,
    bool KeyFilesOpen);

/// <summary>Folders and key files the agent may use on this PC, per gateway and profile. Not the Projects git grant.</summary>
public sealed class FolderBridgeStore(IPreferenceStore store)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<BridgeProfile> GetAsync(ProfileKey key, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct).ConfigureAwait(false);
        return all.TryGetValue(KeyOf(key), out var row) ? row.ToProfile() : Empty;
    }

    public async Task AddFolderAsync(ProfileKey key, string canonicalRoot, bool network, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct).ConfigureAwait(false);
        var row = Take(all, key);
        if (!row.Folders.Any(f => Same(f.CanonicalRoot, canonicalRoot)))
            row.Folders.Add(new BridgeFolder(canonicalRoot, network, DateTimeOffset.UtcNow));
        await SaveAsync(all, ct).ConfigureAwait(false);
    }

    public async Task RemoveFolderAsync(ProfileKey key, string canonicalRoot, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct).ConfigureAwait(false);
        if (!all.TryGetValue(KeyOf(key), out var row)) return;
        row.Folders.RemoveAll(f => Same(f.CanonicalRoot, canonicalRoot));
        await SaveAsync(all, ct).ConfigureAwait(false);
    }

    public async Task AllowKeyFileAsync(ProfileKey key, string canonicalPath, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct).ConfigureAwait(false);
        var row = Take(all, key);
        if (!row.KeyFiles.Any(f => Same(f.CanonicalPath, canonicalPath)))
            row.KeyFiles.Add(new BridgeKeyFile(canonicalPath, DateTimeOffset.UtcNow));
        await SaveAsync(all, ct).ConfigureAwait(false);
    }

    public async Task RevokeKeyFileAsync(ProfileKey key, string canonicalPath, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct).ConfigureAwait(false);
        if (!all.TryGetValue(KeyOf(key), out var row)) return;
        row.KeyFiles.RemoveAll(f => Same(f.CanonicalPath, canonicalPath));
        await SaveAsync(all, ct).ConfigureAwait(false);
    }

    public async Task SetKeyFilesOpenAsync(ProfileKey key, bool open, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct).ConfigureAwait(false);
        Take(all, key).KeyFilesOpen = open;
        await SaveAsync(all, ct).ConfigureAwait(false);
    }

    private static BridgeProfile Empty => new([], [], false);

    private static string KeyOf(ProfileKey key) => key.GatewayId + "\u001f" + key.ProfileId;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static Row Take(Dictionary<string, Row> all, ProfileKey key)
    {
        var id = KeyOf(key);
        if (!all.TryGetValue(id, out var row))
            all[id] = row = new Row();
        return row;
    }

    private async Task<Dictionary<string, Row>> LoadAsync(CancellationToken ct)
    {
        var json = await store.LoadAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.Ordinal);
        try
        {
            var file = JsonSerializer.Deserialize<FileDto>(json, JsonOpts);
            var map = new Dictionary<string, Row>(StringComparer.Ordinal);
            if (file?.Profiles is null) return map;
            foreach (var p in file.Profiles)
            {
                if (string.IsNullOrWhiteSpace(p.GatewayId) || string.IsNullOrWhiteSpace(p.ProfileId)) continue;
                map[p.GatewayId + "\u001f" + p.ProfileId] = new Row
                {
                    Folders = p.Folders ?? [],
                    KeyFiles = p.KeyFiles ?? [],
                    KeyFilesOpen = p.KeyFilesOpen
                };
            }
            return map;
        }
        catch (JsonException) { return new(StringComparer.Ordinal); }
    }

    private Task SaveAsync(Dictionary<string, Row> all, CancellationToken ct)
    {
        var file = new FileDto
        {
            Profiles = all.Select(kv =>
            {
                var cut = kv.Key.IndexOf('\u001f');
                return new ProfileDto
                {
                    GatewayId = kv.Key[..cut],
                    ProfileId = kv.Key[(cut + 1)..],
                    Folders = kv.Value.Folders,
                    KeyFiles = kv.Value.KeyFiles,
                    KeyFilesOpen = kv.Value.KeyFilesOpen
                };
            }).ToList()
        };
        return store.SaveAsync(JsonSerializer.Serialize(file, JsonOpts), ct);
    }

    private sealed class Row
    {
        public List<BridgeFolder> Folders { get; set; } = [];
        public List<BridgeKeyFile> KeyFiles { get; set; } = [];
        public bool KeyFilesOpen { get; set; }
        public BridgeProfile ToProfile() => new(Folders, KeyFiles, KeyFilesOpen);
    }

    private sealed class FileDto
    {
        public List<ProfileDto> Profiles { get; set; } = [];
    }

    private sealed class ProfileDto
    {
        public string GatewayId { get; set; } = "";
        public string ProfileId { get; set; } = "";
        public List<BridgeFolder> Folders { get; set; } = [];
        public List<BridgeKeyFile> KeyFiles { get; set; } = [];
        public bool KeyFilesOpen { get; set; }
    }
}
