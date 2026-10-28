using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hikyaku.Core;

/// <summary>
/// Local Inbox job groups (BookahHermes InboxGroups) — phone-local on Android; here keyed by gateway URL.
/// Filtering is local; run data still comes from herald-push. Not a server API.
/// </summary>
public sealed class InboxGroupsStore(IPreferenceStore store)
{
    private readonly object _gate = new();

    public async Task<InboxGroups> LoadAsync(string gatewayUrl, CancellationToken ct = default)
    {
        var all = await LoadAllAsync(ct).ConfigureAwait(false);
        return all.TryGetValue(gatewayUrl, out var g) ? g : InboxGroups.Empty;
    }

    public async Task SaveAsync(string gatewayUrl, InboxGroups groups, CancellationToken ct = default)
    {
        var all = await LoadAllAsync(ct).ConfigureAwait(false);
        all[gatewayUrl] = groups;
        await store.SaveAsync(JsonSerializer.Serialize(new RootDto
        {
            Gateways = all.ToDictionary(kv => kv.Key, kv => GroupDto.From(kv.Value))
        }, InboxGroupJson.Options), ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, InboxGroups>> LoadAllAsync(CancellationToken ct)
    {
        var raw = await store.LoadAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(raw)) return new Dictionary<string, InboxGroups>(StringComparer.Ordinal);
        try
        {
            var dto = JsonSerializer.Deserialize<RootDto>(raw, InboxGroupJson.Options);
            if (dto?.Gateways is null) return new Dictionary<string, InboxGroups>(StringComparer.Ordinal);
            return dto.Gateways.ToDictionary(kv => kv.Key, kv => kv.Value.ToModel(), StringComparer.Ordinal);
        }
        catch (JsonException) { return new Dictionary<string, InboxGroups>(StringComparer.Ordinal); }
    }

    private sealed class RootDto
    {
        public Dictionary<string, GroupDto> Gateways { get; set; } = new();
    }

    private sealed class GroupDto
    {
        public List<string> Names { get; set; } = new();
        public Dictionary<string, string> JobGroup { get; set; } = new();

        public static GroupDto From(InboxGroups g) => new()
        {
            Names = g.Names.ToList(),
            JobGroup = g.JobGroup.ToDictionary(kv => kv.Key, kv => kv.Value)
        };

        public InboxGroups ToModel() => new(Names, JobGroup);
    }
}

public sealed record InboxGroups(IReadOnlyList<string> Names, IReadOnlyDictionary<string, string> JobGroup)
{
    public static InboxGroups Empty { get; } = new(Array.Empty<string>(), new Dictionary<string, string>());

    public string? GroupOf(string jobId) =>
        JobGroup.TryGetValue(jobId, out var g) ? g : null;

    public InboxGroups WithJob(string jobId, string? groupName)
    {
        var map = new Dictionary<string, string>(JobGroup, StringComparer.Ordinal);
        var names = Names.ToList();
        if (string.IsNullOrWhiteSpace(groupName))
            map.Remove(jobId);
        else
        {
            var g = groupName.Trim();
            map[jobId] = g;
            if (!names.Contains(g, StringComparer.Ordinal)) names.Add(g);
        }
        return new InboxGroups(names, map);
    }
}

internal static class InboxGroupJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
