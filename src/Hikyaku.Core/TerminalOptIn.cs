using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hikyaku.Core;

public enum TerminalHostKind
{
    LocalWindows = 0,
    Remote = 1
}

public sealed record TerminalOptInRecord(
    string GatewayId,
    string ProfileId,
    TerminalHostKind Host,
    bool Enabled,
    DateTimeOffset? WarningAcknowledgedUtc,
    string? PreferredShellPath,
    string? PreferredWorkingDirectory);

public sealed record TerminalOptInResult(bool Ok, string? Error = null);

/// <summary>UI visibility and folder grants never authorise a shell.</summary>
public static class TerminalAuthorisation
{
    public const bool FolderGrantAuthorisesShell = false;
    public const bool YoloAuthorisesShell = false;
    public const bool AdvancedVisibilityAuthorisesShell = false;
    public const bool OptInCreatesGatewayShellBridge = false;
    public const bool WorkingDirectoryIsSandbox = false;
}

public static class TerminalLaunchPolicy
{
    /// <summary>Saved preference never auto-starts a PTY at app launch/demo/onboarding.</summary>
    public const bool MayAutoStartFromSavedPreference = false;

    public static bool MayAutoStart(TerminalOptInRecord? record) => false;
}

/// <summary>Per gateway+profile+host opt-in. Default off. Not a sandbox claim.</summary>
public sealed class TerminalOptInStore(IPreferenceStore store)
{
    public const string WarningCopy =
        "A terminal can run any command as your Windows user. It is NOT restricted by folder grants. " +
        "Grants, YOLO, and Advanced visibility do not authorise it. Only turn this on if you understand that.";

    public const string NoGatewayBridgeCopy =
        "Local terminal opt-in does NOT give gateway agents a local-shell bridge or automatic command route.";

    public const string RemoteUnavailableCopy =
        "Remote terminal is unavailable — no verified remote PTY protocol in this build. " +
        "Hikyaku will not silently substitute local execution.";

    public const string NotSandboxCopy =
        "The working directory is where the shell starts — it is not an OS sandbox or folder-grant jail.";

    public async Task<TerminalOptInRecord?> GetAsync(ProfileKey key, TerminalHostKind host, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct).ConfigureAwait(false);
        return all.TryGetValue(KeyOf(key, host), out var r) ? r : null;
    }

    public async Task<bool> IsEnabledAsync(ProfileKey key, TerminalHostKind host, CancellationToken ct = default)
    {
        var r = await GetAsync(key, host, ct).ConfigureAwait(false);
        return r is { Enabled: true };
    }

    public async Task<TerminalOptInResult> EnableAsync(
        ProfileKey key,
        TerminalHostKind host,
        bool warningAcknowledged,
        string? preferredShellPath = null,
        string? preferredWorkingDirectory = null,
        CancellationToken ct = default)
    {
        if (host == TerminalHostKind.Remote)
            return new TerminalOptInResult(false, RemoteUnavailableCopy);
        if (!warningAcknowledged)
            return new TerminalOptInResult(false, "Acknowledge the terminal warning before enabling.");

        var all = await LoadAsync(ct).ConfigureAwait(false);
        all[KeyOf(key, host)] = new TerminalOptInRecord(
            key.GatewayId,
            key.ProfileId,
            host,
            Enabled: true,
            WarningAcknowledgedUtc: DateTimeOffset.UtcNow,
            PreferredShellPath: preferredShellPath,
            PreferredWorkingDirectory: preferredWorkingDirectory);
        await SaveAsync(all, ct).ConfigureAwait(false);
        return new TerminalOptInResult(true);
    }

    public async Task RevokeAsync(ProfileKey key, TerminalHostKind host, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct).ConfigureAwait(false);
        if (all.Remove(KeyOf(key, host)))
            await SaveAsync(all, ct).ConfigureAwait(false);
    }

    public async Task SetPreferredShellAsync(ProfileKey key, TerminalHostKind host, string? shellPath, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct).ConfigureAwait(false);
        if (!all.TryGetValue(KeyOf(key, host), out var existing) || !existing.Enabled)
            return;
        all[KeyOf(key, host)] = existing with { PreferredShellPath = shellPath };
        await SaveAsync(all, ct).ConfigureAwait(false);
    }

    private static string KeyOf(ProfileKey key, TerminalHostKind host) =>
        key.GatewayId + "\u001f" + key.ProfileId + "\u001f" + (int)host;

    private async Task<Dictionary<string, TerminalOptInRecord>> LoadAsync(CancellationToken ct)
    {
        var raw = await store.LoadAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
            return new Dictionary<string, TerminalOptInRecord>(StringComparer.Ordinal);
        try
        {
            var dto = JsonSerializer.Deserialize<StoreDto>(raw) ?? new StoreDto();
            var map = new Dictionary<string, TerminalOptInRecord>(StringComparer.Ordinal);
            foreach (var r in dto.Records)
                map[KeyOf(new ProfileKey(r.GatewayId, r.ProfileId), r.Host)] = r;
            return map;
        }
        catch
        {
            return new Dictionary<string, TerminalOptInRecord>(StringComparer.Ordinal);
        }
    }

    private Task SaveAsync(Dictionary<string, TerminalOptInRecord> all, CancellationToken ct)
    {
        var dto = new StoreDto { Records = all.Values.ToList() };
        return store.SaveAsync(JsonSerializer.Serialize(dto), ct);
    }

    private sealed class StoreDto
    {
        [JsonPropertyName("records")]
        public List<TerminalOptInRecord> Records { get; set; } = new();
    }
}
