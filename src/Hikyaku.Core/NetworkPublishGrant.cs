using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hikyaku.Core;

/// <summary>Network / remote publication is a separate boundary from folder grants and terminal opt-in.</summary>
public static class NetworkPublishAuthorisation
{
    public const bool FolderGrantAuthorisesPush = false;
    public const bool TerminalOptInAuthorisesPush = false;
    public const bool SaveCheckpointAuthorisesPush = false;
    public const bool AdvancedVisibilityAuthorisesPush = false;
}

public sealed record NetworkPublishGrantRecord(
    string GatewayId,
    string ProfileId,
    bool Enabled,
    DateTimeOffset? WarningAcknowledgedUtc);

public sealed record NetworkPublishGrantResult(bool Ok, string? Error = null);

/// <summary>Per gateway+profile opt-in for guided push / PR network actions. Default off.</summary>
public sealed class NetworkPublishGrantStore(IPreferenceStore store)
{
    public const string WarningCopy =
        "Push publishes commits to a remote that other people or systems may see. " +
        "Folder grants, terminal opt-in, YOLO, and Advanced visibility do NOT authorise push. " +
        "Each push still requires its own explicit confirmation naming the exact remote destination.";

    public const string NotFromCheckpointCopy =
        "Save checkpoint is a local commit only. It never pushes or opens a pull request.";

    public async Task<NetworkPublishGrantRecord?> GetAsync(ProfileKey key, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct).ConfigureAwait(false);
        return all.TryGetValue(KeyOf(key), out var r) ? r : null;
    }

    public async Task<bool> IsEnabledAsync(ProfileKey key, CancellationToken ct = default)
    {
        var r = await GetAsync(key, ct).ConfigureAwait(false);
        return r is { Enabled: true };
    }

    public async Task<NetworkPublishGrantResult> EnableAsync(
        ProfileKey key,
        bool warningAcknowledged,
        CancellationToken ct = default)
    {
        if (!warningAcknowledged)
            return new NetworkPublishGrantResult(false, "Acknowledge the remote publication warning before enabling push.");

        var all = await LoadAsync(ct).ConfigureAwait(false);
        all[KeyOf(key)] = new NetworkPublishGrantRecord(
            key.GatewayId,
            key.ProfileId,
            Enabled: true,
            WarningAcknowledgedUtc: DateTimeOffset.UtcNow);
        await SaveAsync(all, ct).ConfigureAwait(false);
        return new NetworkPublishGrantResult(true);
    }

    public async Task RevokeAsync(ProfileKey key, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct).ConfigureAwait(false);
        if (all.Remove(KeyOf(key)))
            await SaveAsync(all, ct).ConfigureAwait(false);
    }

    private static string KeyOf(ProfileKey key) => key.GatewayId + "\u001f" + key.ProfileId;

    private async Task<Dictionary<string, NetworkPublishGrantRecord>> LoadAsync(CancellationToken ct)
    {
        var raw = await store.LoadAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
            return new Dictionary<string, NetworkPublishGrantRecord>(StringComparer.Ordinal);
        try
        {
            var dto = JsonSerializer.Deserialize<GrantFile>(raw, JsonOpts) ?? new();
            var map = new Dictionary<string, NetworkPublishGrantRecord>(StringComparer.Ordinal);
            foreach (var g in dto.Grants)
                map[g.GatewayId + "\u001f" + g.ProfileId] = g;
            return map;
        }
        catch
        {
            return new Dictionary<string, NetworkPublishGrantRecord>(StringComparer.Ordinal);
        }
    }

    private Task SaveAsync(Dictionary<string, NetworkPublishGrantRecord> all, CancellationToken ct)
    {
        var dto = new GrantFile { Grants = all.Values.ToList() };
        return store.SaveAsync(JsonSerializer.Serialize(dto, JsonOpts), ct);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private sealed class GrantFile
    {
        public List<NetworkPublishGrantRecord> Grants { get; set; } = new();
    }
}
