using System.Text.Json.Nodes;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Checkpoint 8: bots/rooms/subagents — fixtures only, no live gateway/bot creation.</summary>
public class Checkpoint8BotChatsTests
{
    private sealed class FakeBackend : IBotChatBackend
    {
        public Dictionary<string, BotSession?> Chats { get; } = new(StringComparer.Ordinal);
        public bool LookupFails { get; set; }
        public BotSession? TakenBy { get; set; }
        public int Starts { get; private set; }
        public TaskCompletionSource? StartGate { get; set; }

        public Task<BotSession?> BotChatAsync(string profile, CancellationToken ct = default)
        {
            if (LookupFails) throw new InvalidOperationException("profile backend still waking up");
            return Task.FromResult(Chats.TryGetValue(profile, out var s) ? s : null);
        }

        public async Task<string> StartBotChatAsync(string profile, CancellationToken ct = default)
        {
            Starts++;
            if (StartGate is not null) await StartGate.Task.WaitAsync(ct).ConfigureAwait(false);
            if (TakenBy is not null)
            {
                Chats[profile] = TakenBy;
                throw new BotChatTakenException();
            }
            var id = "new-" + Starts;
            Chats[profile] = new BotSession(Id: id);
            return id;
        }
    }

    private static Bot Bot(string name, BotSession? chat = null) =>
        new(name, CanonicalSession: chat);

    [Fact]
    public async Task Opens_The_Live_End_Of_An_Existing_Chat()
    {
        var backend = new FakeBackend();
        backend.Chats["scribe"] = new BotSession(Id: "root", ResolvedId: "tip");
        var id = await new BotChats(backend).OpenAsync(Bot("scribe", new BotSession(Id: "root")));
        Assert.Equal("tip", id);
        Assert.Equal(0, backend.Starts);
    }

    [Fact]
    public async Task Starts_Only_When_Bot_Never_Had_A_Chat()
    {
        var backend = new FakeBackend();
        Assert.Equal("new-1", await new BotChats(backend).OpenAsync(Bot("scribe")));
        Assert.Equal(1, backend.Starts);
    }

    [Fact]
    public async Task Failed_Lookup_Never_Starts_A_Second_Chat()
    {
        var backend = new FakeBackend { LookupFails = true };
        await Assert.ThrowsAsync<BotChatUnavailableException>(() =>
            new BotChats(backend).OpenAsync(Bot("scribe")));
        Assert.Equal(0, backend.Starts);
    }

    [Fact]
    public async Task Empty_Lookup_For_Known_Chat_Is_Not_Trusted()
    {
        var backend = new FakeBackend();
        await Assert.ThrowsAsync<BotChatUnavailableException>(() =>
            new BotChats(backend).OpenAsync(Bot("scribe", new BotSession(Id: "root"))));

        var chats = new BotChats(backend);
        await chats.NoteAsync(new[] { Bot("helper", new BotSession(Id: "h")) });
        await Assert.ThrowsAsync<BotChatUnavailableException>(() => chats.OpenAsync(Bot("helper")));
        Assert.Equal(0, backend.Starts);
    }

    [Fact]
    public async Task Chat_Started_Elsewhere_Is_Adopted()
    {
        var backend = new FakeBackend { TakenBy = new BotSession(Id: "theirs") };
        Assert.Equal("theirs", await new BotChats(backend).OpenAsync(Bot("scribe")));
    }

    [Fact]
    public async Task Double_Tap_Starts_One_Chat()
    {
        var backend = new FakeBackend { StartGate = new TaskCompletionSource() };
        var chats = new BotChats(backend);
        var first = chats.OpenAsync(Bot("scribe"));
        var second = chats.OpenAsync(Bot("scribe"));
        await Task.Yield();
        backend.StartGate!.SetResult();
        Assert.Equal("new-1", await first);
        Assert.Equal("new-1", await second);
        Assert.Equal(1, backend.Starts);
    }
}

public class Checkpoint8RosterGateTests
{
    [Fact]
    public void ParseBotRoster_Reads_Canonical_And_Protocol_Flag()
    {
        var json = JsonNode.Parse("""
            {"bot_mode_protocol":true,"profiles":[
              {"name":"default","is_default":true,"canonical_session":{"id":"bc1","resolved_id":"bc1-tip","title":"Bot Chat","preview":"hi","last_active":100},
               "ui_meta":{"hermes-bots":{"title":"Shisha","pinned":true}}},
              {"name":"other","canonical_session":{"id":"o1","preview":"secret"}}
            ]}
            """)!;
        var roster = BotRosterParser.Parse(json);
        Assert.True(roster.TeammateProtocol);
        Assert.Equal(2, roster.Bots.Count);
        Assert.Equal("bc1-tip", roster.Bots[0].CanonicalSession!.OpenId);
        Assert.Equal("Shisha", roster.Bots[0].Label);
        Assert.True(roster.Bots[0].Meta.Pinned);
    }

    [Fact]
    public async Task RosterService_Excludes_Deselected_Profiles()
    {
        var store = new MemoryStore();
        var selection = new ProfileSelectionService(store);
        await selection.LoadAsync();
        await selection.SetSelectedAsync(new[] { new ProfileKey("gw", "default") });
        var gate = new NotificationGate(selection);

        var full = new BotRoster(new[]
        {
            new Bot("default", CanonicalSession: new BotSession(Id: "a")),
            new Bot("other", CanonicalSession: new BotSession(Id: "b", Preview: "leak")),
        }, TeammateProtocol: true);

        var svc = new BotRosterService(gate, "gw", _ => Task.FromResult(full));
        var visible = await svc.LoadVisibleRosterAsync();
        Assert.Single(visible.Bots);
        Assert.Equal("default", visible.Bots[0].Name);
        Assert.DoesNotContain(visible.Bots, b => b.Name == "other");
        Assert.Contains("preference filtering", visible.GateDisclaimer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restricted_Room_Explains_Instead_Of_Leaking()
    {
        var store = new MemoryStore();
        var selection = new ProfileSelectionService(store);
        await selection.LoadAsync();
        await selection.SetSelectedAsync(new[] { new ProfileKey("gw", "default") });
        var gate = new NotificationGate(selection);

        var rooms = new[]
        {
            new DesktopRoom("id:r1", "Team", Members: new[] { new DesktopMember("default"), new DesktopMember("other") },
                Lines: new[] { new DesktopLine(FromUser: false, Speaker: "other", Text: "secret @user") }),
        };
        var view = BotRosterService.FilterRooms(rooms, "gw", gate);
        Assert.Empty(view.Visible);
        Assert.Single(view.Restricted);
        Assert.Contains("not selected", view.Restricted[0].Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", view.Restricted[0].Explanation, StringComparison.Ordinal);
    }
}

public class Checkpoint8RoomsTests
{
    [Fact]
    public void ParseDesktopRooms_Applies_Tombstones_And_NeedsYou()
    {
        var meta = JsonNode.Parse("""
            {"hermes-bots-groups":{"rooms":{
              "id:alive":{"name":"Alive","revision":1,"members":[{"name":"default"}],"log":[
                {"id":"1","text":"hello @user","from":{"kind":"bot","name":"default"},"at":50}
              ]},
              "id:gone":{"name":"Gone","revision":1,"log":[]}
            },"deleted":{"id:gone":2}}}
            """)!.AsObject();
        var rooms = DesktopRooms.Parse(meta);
        Assert.Single(rooms);
        Assert.Equal("Alive", rooms[0].Name);
        Assert.True(rooms[0].NeedsYou);
    }
}

public class Checkpoint8SubagentTests
{
    [Fact]
    public void Steer_Queued_Is_Not_Child_Read_Proof()
    {
        var outcome = SubagentSteerOutcome.FromWireStatus("queued");
        Assert.True(outcome.AcknowledgedQueued);
        Assert.False(outcome.ChildConfirmedRead);
        Assert.Contains("not proof", outcome.HonestyLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Subagent_Events_Order_And_Terminal_Unknown_Is_Failure()
    {
        var mon = new SubagentMonitor(new ProfileKey("gw", "default"), "rt-1");
        mon.ApplyEvent("subagent.start", JsonNode.Parse("""{"subagent_id":"s1","goal":"Read docs","task_index":0}""")!.AsObject());
        mon.ApplyEvent("subagent.tool", JsonNode.Parse("""{"subagent_id":"s1","tool_name":"terminal","tool_preview":"ls"}""")!.AsObject());
        mon.ApplyEvent("subagent.complete", JsonNode.Parse("""{"subagent_id":"s1","status":"weird","summary":"x"}""")!.AsObject());
        var row = Assert.Single(mon.Rows);
        Assert.Equal(SubagentStatus.Failed, row.Status);
        Assert.Equal(new[] { "terminal: ls" }, row.Activity);
        Assert.Null(row.StoppableId);
    }

    [Fact]
    public void Reconnect_Snapshots_Keep_Prior_Activity()
    {
        var mon = new SubagentMonitor(new ProfileKey("gw", "default"), "rt-1");
        mon.ApplyEvent("subagent.start", JsonNode.Parse("""{"subagent_id":"s1","goal":"G"}""")!.AsObject());
        mon.ApplyEvent("subagent.tool", JsonNode.Parse("""{"subagent_id":"s1","tool_name":"read"}""")!.AsObject());
        mon.ApplySnapshots(new[]
        {
            JsonNode.Parse("""{"subagent_id":"s1","goal":"G","status":"running","last_tool":"other"}""")!.AsObject()!
        });
        Assert.Equal(new[] { "read" }, mon.Rows[0].Activity);
    }

    [Fact]
    public void Cross_Profile_Events_Are_Dropped()
    {
        var mon = new SubagentMonitor(new ProfileKey("gw", "default"), "rt-1");
        Assert.False(mon.Accepts(new ProfileKey("gw", "other"), "rt-1"));
        Assert.False(mon.Accepts(new ProfileKey("gw", "default"), "rt-other"));
        Assert.True(mon.Accepts(new ProfileKey("gw", "default"), "rt-1"));
    }

    [Fact]
    public async Task Stop_Contract_Uses_Subagent_Interrupt()
    {
        string? method = null;
        JsonObject? args = null;
        var client = new RecordingRpc((m, p) =>
        {
            method = m;
            args = p;
            return JsonNode.Parse("""{"found":true}""")!;
        });
        var mon = new SubagentMonitor(new ProfileKey("gw", "default"), "rt-9");
        mon.ApplyEvent("subagent.start", JsonNode.Parse("""{"subagent_id":"child-1","goal":"x"}""")!.AsObject());
        await mon.StopAsync(client, "child-1");
        Assert.Equal("subagent.interrupt", method);
        Assert.Equal("rt-9", args!["session_id"]!.GetValue<string>());
        Assert.Equal("child-1", args["subagent_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Steer_Contract_Uses_Session_Steer()
    {
        string? method = null;
        JsonObject? args = null;
        var client = new RecordingRpc((m, p) =>
        {
            method = m;
            args = p;
            return JsonNode.Parse("""{"status":"queued"}""")!;
        });
        var mon = new SubagentMonitor(new ProfileKey("gw", "default"), "rt-9");
        var outcome = await mon.SteerAsync(client, "please focus");
        Assert.Equal("session.steer", method);
        Assert.Equal("please focus", args!["text"]!.GetValue<string>());
        Assert.True(outcome.AcknowledgedQueued);
        Assert.False(outcome.ChildConfirmedRead);
    }

    private sealed class RecordingRpc : IJsonRpcPeer
    {
        private readonly Func<string, JsonObject?, JsonNode?> _handler;
        public RecordingRpc(Func<string, JsonObject?, JsonNode?> handler) => _handler = handler;
        public Task<JsonNode?> RequestAsync(string method, JsonObject? parameters = null,
            TimeSpan? timeout = null, CancellationToken ct = default)
            => Task.FromResult(_handler(method, parameters));
    }
}

public class Checkpoint8DemoFixtureTests
{
    [Fact]
    public void Demo_Bot_Fixture_Is_Synthetic_Not_Live()
    {
        var roster = DemoBotFixtures.Roster;
        Assert.True(roster.Bots.Count >= 1);
        Assert.Contains(roster.Bots, b => b.CanonicalSession?.OpenId is not null);
        Assert.True(DemoBotFixtures.IsSynthetic);
        Assert.Contains("synthetic", DemoBotFixtures.Banner, StringComparison.OrdinalIgnoreCase);
    }
}
