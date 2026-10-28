using System.Text.Json;

namespace Hikyaku.Core;

/// <summary>
/// Per-installation allowlist of (gateway, profile) pairs. Default deny: nothing is allowed until
/// preferences have loaded, newly discovered profiles are never auto-selected, and an empty
/// selection means onboarding, not "show everything". This is preference filtering only,
/// not server-side authorisation.
/// </summary>
public sealed class ProfileSelectionService
{
    private sealed record Persisted(int Version, List<Entry> Selected);
    private sealed record Entry(string GatewayId, string ProfileId);

    private readonly IPreferenceStore _store;
    private readonly object _gate = new();
    private HashSet<ProfileKey> _selected = new();
    private volatile bool _loaded;

    public ProfileSelectionService(IPreferenceStore store) => _store = store;

    public bool IsLoaded => _loaded;
    public bool NeedsOnboarding { get { lock (_gate) return _loaded && _selected.Count == 0; } }
    public event Action? Changed;

    public IReadOnlyCollection<ProfileKey> Selected { get { lock (_gate) return _selected.ToArray(); } }

    /// <summary>Loads preferences. Missing or corrupt data yields an empty (deny-all) selection.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        HashSet<ProfileKey> loaded = new();
        try
        {
            var raw = await _store.LoadAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var p = JsonSerializer.Deserialize<Persisted>(raw, JsonOpts);
                foreach (var e in p?.Selected ?? new())
                {
                    if (!string.IsNullOrWhiteSpace(e.GatewayId) && !string.IsNullOrWhiteSpace(e.ProfileId))
                        loaded.Add(new ProfileKey(e.GatewayId, e.ProfileId));
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            loaded.Clear();
        }
        lock (_gate) { _selected = loaded; _loaded = true; }
        Changed?.Invoke();
    }

    /// <summary>True only when preferences have loaded and the profile is explicitly selected.</summary>
    public bool IsAllowed(ProfileKey key)
    {
        if (!_loaded) return false;
        lock (_gate) return _selected.Contains(key);
    }

    public IReadOnlyList<T> Filter<T>(IEnumerable<T> items, Func<T, ProfileKey> keyOf)
        => _loaded ? items.Where(i => IsAllowed(keyOf(i))).ToList() : Array.Empty<T>();

    /// <summary>Returns discovered profiles that are not selected. They are surfaced for opt-in, never auto-added.</summary>
    public IReadOnlyList<ProfileKey> UnselectedAmong(IEnumerable<ProfileKey> discovered)
        => discovered.Where(k => !IsAllowed(k)).Distinct().ToList();

    public async Task SetSelectedAsync(IEnumerable<ProfileKey> keys, CancellationToken ct = default)
    {
        var next = new HashSet<ProfileKey>(keys);
        lock (_gate) { _selected = next; _loaded = true; }
        var payload = new Persisted(1, next.OrderBy(k => k.GatewayId, StringComparer.Ordinal)
            .ThenBy(k => k.ProfileId, StringComparer.Ordinal)
            .Select(k => new Entry(k.GatewayId, k.ProfileId)).ToList());
        await _store.SaveAsync(JsonSerializer.Serialize(payload, JsonOpts), ct).ConfigureAwait(false);
        Changed?.Invoke();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
}
