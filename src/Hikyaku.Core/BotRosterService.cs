using System.Text.Json.Nodes;

namespace Hikyaku.Core;

/// <summary>
/// Selected-profile-gated bot roster / rooms. UI filtering is preference-only — not server authorization.
/// </summary>
public sealed class BotRosterService
{
    public const string GateDisclaimer =
        "Shown profiles are preference filtering on this installation — not server authorization.";

    private readonly NotificationGate _gate;
    private readonly string _gatewayId;
    private readonly Func<CancellationToken, Task<BotRoster>> _loadRoster;
    private readonly Func<CancellationToken, Task<IReadOnlyList<DesktopRoom>>>? _loadRooms;

    public BotRosterService(
        NotificationGate gate,
        string gatewayId,
        Func<CancellationToken, Task<BotRoster>> loadRoster,
        Func<CancellationToken, Task<IReadOnlyList<DesktopRoom>>>? loadRooms = null)
    {
        _gate = gate;
        _gatewayId = gatewayId;
        _loadRoster = loadRoster;
        _loadRooms = loadRooms;
    }

    public async Task<VisibleBotRoster> LoadVisibleRosterAsync(CancellationToken ct = default)
    {
        var full = await _loadRoster(ct).ConfigureAwait(false);
        var visible = BotRosterParser.ForRoster(
            full.Bots.Where(b => _gate.Allow(Surface.Navigation, new ProfileKey(_gatewayId, b.Name))
                                 && !b.Meta.Hidden));
        return new VisibleBotRoster(visible, full.TeammateProtocol, GateDisclaimer);
    }

    public async Task<FilteredRooms> LoadRoomsAsync(CancellationToken ct = default)
    {
        if (_loadRooms is null) return new FilteredRooms(Array.Empty<DesktopRoom>(), Array.Empty<RestrictedRoomView>());
        var rooms = await _loadRooms(ct).ConfigureAwait(false);
        return FilterRooms(rooms, _gatewayId, _gate);
    }

    /// <summary>
    /// A room is visible only when every named member profile is selected.
    /// Otherwise explain restriction without leaking other bots' line content.
    /// </summary>
    public static FilteredRooms FilterRooms(IEnumerable<DesktopRoom> rooms, string gatewayId, NotificationGate gate)
    {
        var visible = new List<DesktopRoom>();
        var restricted = new List<RestrictedRoomView>();
        foreach (var room in rooms)
        {
            var members = room.MembersList;
            if (members.Count == 0)
            {
                // No members listed: treat as unavailable rather than leaking lines.
                restricted.Add(new RestrictedRoomView(room.Key, room.Name,
                    "This room is unavailable — member list missing. Preference filtering only; not server authorization."));
                continue;
            }
            var denied = members
                .Where(m => !gate.Allow(Surface.Navigation, new ProfileKey(gatewayId, m.Name)))
                .Select(m => m.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (denied.Count == 0)
            {
                visible.Add(room);
                continue;
            }
            restricted.Add(new RestrictedRoomView(room.Key, room.Name,
                $"Room “{room.Name}” includes profiles not selected on this installation ({string.Join(", ", denied)}). Content hidden — preference filtering, not server authorization."));
        }
        return new FilteredRooms(visible, restricted);
    }
}

public sealed record VisibleBotRoster(
    IReadOnlyList<Bot> Bots,
    bool TeammateProtocol,
    string GateDisclaimer);

/// <summary>RPC-backed Bot Mode API — BookahHermes BotsApi.kt (fixtures / connected client).</summary>
public sealed class BotsApi : IBotChatBackend
{
    public const string BotChatTitle = "Bot Chat";

    private readonly Func<IJsonRpcPeer?> _client;

    public BotsApi(Func<IJsonRpcPeer?> client) => _client = client;

    public async Task<BotRoster> RosterAsync(CancellationToken ct = default)
    {
        var client = Require();
        var result = await client.RequestAsync("profiles.list", new JsonObject(), ct: ct).ConfigureAwait(false);
        return BotRosterParser.Parse(result);
    }

    public async Task<BotSession?> BotChatAsync(string profile, CancellationToken ct = default)
    {
        var roster = await RosterAsync(ct).ConfigureAwait(false);
        return roster.Bots.FirstOrDefault(b => b.Name == profile)?.CanonicalSession is { OpenId: not null } s ? s : null;
    }

    public async Task<string> StartBotChatAsync(string profile, CancellationToken ct = default)
    {
        var client = Require();
        var created = await client.RequestAsync("session.create", new JsonObject
        {
            ["profile"] = profile,
            ["title"] = BotChatTitle,
            ["hidden"] = true,
            ["follow_profile_config"] = true,
            ["source"] = HermesDashboardTransport.ClientSource,
            ["cols"] = HermesDashboardTransport.TerminalColumns
        }, ct: ct).ConfigureAwait(false) as JsonObject
            ?? throw new RpcException(0, "Empty session.create reply");

        // Title already claimed elsewhere (4022) — adopt, never fork.
        // Callers surface BotChatTakenException when the wire says so.
        var stored = created["stored_session_id"]?.GetValue<string>()
            ?? created["session_id"]?.GetValue<string>()
            ?? throw new RpcException(0, "session.create returned no session id");

        try
        {
            await client.RequestAsync("session.title", new JsonObject
            {
                ["session_id"] = created["session_id"]?.GetValue<string>() ?? stored,
                ["title"] = BotChatTitle
            }, ct: ct).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.Code == 4022)
        {
            throw new BotChatTakenException();
        }

        return stored;
    }

    public async Task WatchAsync(string profile, CancellationToken ct = default)
    {
        var client = Require();
        await client.RequestAsync("session.list", new JsonObject
        {
            ["profile"] = profile,
            ["title"] = BotChatTitle,
            ["limit"] = 1
        }, ct: ct).ConfigureAwait(false);
    }

    private IJsonRpcPeer Require() =>
        _client() ?? throw new RpcException(0, "Not connected to the gateway.");
}

/// <summary>Synthetic Bot Mode fixtures for demo / UI acceptance — not a live gateway.</summary>
public static class DemoBotFixtures
{
    public static bool IsSynthetic => true;
    public static string Banner => "Synthetic Bot Mode fixtures — not a live Hermes roster.";

    public static BotRoster Roster { get; } = new(
        new[]
        {
            new Bot(
                Name: "default",
                IsDefault: true,
                DisplayName: "Shisha",
                CanonicalSession: new BotSession(
                    Id: "demo-bot-chat",
                    ResolvedId: "demo-bot-chat",
                    Title: BotsApi.BotChatTitle,
                    Preview: "Synthetic Bot Chat preview",
                    LastActive: 1_700_000_000),
                UiMeta: JsonNode.Parse("""{"hermes-bots":{"title":"Shisha","pinned":true},"hermes-bots-groups":{"rooms":{"id:demo":{"name":"Demo room","revision":1,"members":[{"name":"default"}],"log":[{"id":"1","text":"hello @user — synthetic","from":{"kind":"bot","name":"default"},"at":50}]}},"deleted":{}}}""") as JsonObject),
            new Bot(
                Name: "other",
                CanonicalSession: new BotSession(Id: "other-chat", Preview: "should stay hidden when deselected"),
                UiMeta: JsonNode.Parse("""{"hermes-bots":{"title":"Other"}}""") as JsonObject),
        },
        TeammateProtocol: true);

    public static IReadOnlyList<DesktopRoom> RoomsFromDefaultMeta()
    {
        var bot = Roster.Bots.First(b => b.Name == "default");
        return DesktopRooms.Parse(bot.UiMeta);
    }
}
