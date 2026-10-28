using System.Text.Json.Nodes;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 32: hosted rooms (groups.*) — parsing, transcript, who a message asks, and the guarded writes. Fixtures only.</summary>
public class Checkpoint32HostedRoomsTests
{
    private sealed class Peer : IJsonRpcPeer
    {
        public readonly List<(string Method, JsonObject Args, TimeSpan? Timeout)> Calls = new();
        public Func<string, JsonNode?> Reply = _ => new JsonObject();
        public Exception? Throw;
        public Task<JsonNode?> RequestAsync(string method, JsonObject? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            Calls.Add((method, parameters ?? new JsonObject(), timeout));
            if (Throw is not null) throw Throw;
            return Task.FromResult(Reply(method));
        }
    }

    private static HostedRoomsApi Api(Peer p) => new(() => p);

    private static readonly HostedRoomMember Ops = new("ops", "ops-bot", "ops", "Ops Bot");
    private static readonly HostedRoomMember Scribe = new("scribe", "scribe", "scribe", null);
    private static readonly HostedRoomMember NoHandle = new("legacy", "legacy", null, null);
    private static readonly IReadOnlyList<HostedRoomMember> Members = [Ops, Scribe];

    private static RoomEvent Ev(int seq, string kind, string payloadJson, string id = "") =>
        new("r1", seq, id == "" ? "e" + seq : id, kind, "member", "x", JsonNode.Parse(payloadJson)!.AsObject(), 10);

    // ---- reading ----

    [Fact]
    public async Task List_parses_rooms_hides_tombstones_and_orders_newest_first()
    {
        var p = new Peer { Reply = _ => JsonNode.Parse("""
            {"rooms":[
              {"room_id":"old","name":"Old","members":[],"updated_at":1.0,"latest_seq":2},
              {"room_id":"r1","name":"Release room","members":[{"member_id":"ops","profile":"ops-bot","handle":"ops","display_name":"Ops Bot"}],"latest_seq":7,"updated_at":50.5},
              {"room_id":"gone","name":"Gone","members":[],"updated_at":99.0,"disbanded_at":100.0},
              {"name":"no id"},
              "junk"
            ]}
            """) };
        var rooms = await Api(p).ListAsync();
        Assert.Equal(["r1", "old"], rooms.Select(r => r.RoomId));
        Assert.Equal("Ops Bot", rooms[0].Members[0].Label);
        Assert.Equal(7, rooms[0].LatestSeq);
    }

    [Fact]
    public async Task Log_parses_events_keeps_going_past_an_odd_one_and_sends_the_cursor()
    {
        var p = new Peer { Reply = _ => JsonNode.Parse("""
            {"events":[
              {"room_id":"r1","seq":6,"event_id":"user:6","kind":"message.user","actor":{"kind":"user","id":"desktop"},"payload":{"text":"hello","thread_id":"t"},"created_at":10.0},
              {"seq":"bad"},
              {"room_id":"r1","seq":7,"event_id":"gw:7","kind":"message.member","actor":{"kind":"member","id":"ops"},"payload":{"text":"on it","member_id":"ops"},"created_at":11.0}
            ],"cursor":7,"latest_seq":9,"has_more":true}
            """) };
        var page = await Api(p).LogAsync("r1", 5, 50);
        Assert.Equal(2, page.Events.Count);
        Assert.Equal(7, page.Cursor);
        Assert.Equal(9, page.LatestSeq);
        Assert.True(page.HasMore);
        var call = Assert.Single(p.Calls);
        Assert.Equal("groups.log", call.Method);
        Assert.Equal(5, (int)call.Args["since_seq"]!);
        Assert.Equal(50, (int)call.Args["limit"]!);
    }

    [Fact]
    public async Task State_brings_pending_actions_with_their_coordinates()
    {
        var p = new Peer { Reply = _ => JsonNode.Parse("""
            {"room":{"room_id":"r1","name":"Room","members":[],"revision":1},
             "driver_status":{"running":true,"working":true,"blocked":true,"counts":{"queued":1},
               "pending_actions":[
                 {"kind":"approval","member_id":"ops","task_id":"t-9","execution_generation":2,"request_id":"a-3","approval":{"command":"git push origin main","choices":["once","deny"]}},
                 {"kind":"retry","member_id":"scribe","task_id":"t-4"}]}}
            """) };
        var state = await Api(p).StateAsync("r1");
        Assert.True(state.Driver!.Working);
        Assert.Equal(2, state.Driver.PendingActions.Count);
        var a = state.Driver.PendingActions[0];
        Assert.Equal(("t-9", 2, "a-3", "git push origin main"), (a.TaskId, a.ExecutionGeneration, a.RequestId, a.ApprovalSummary));
        Assert.Equal("retry", state.Driver.PendingActions[1].Kind);
    }

    [Fact]
    public async Task Capabilities_say_whether_the_gateway_hosts_rooms()
    {
        var p = new Peer { Reply = _ => JsonNode.Parse("""{"protocol_version":2,"driver":true,"methods":["groups.send"],"max_log_limit":200}""") };
        var caps = await Api(p).CapabilitiesAsync();
        Assert.True(caps.Driver);
        Assert.Equal(200, caps.MaxLogLimit);
        Assert.Equal(["groups.send"], caps.Methods);
    }

    // ---- transcript ----

    [Fact]
    public void The_transcript_shows_messages_and_plain_system_lines_and_hides_machinery()
    {
        var lines = RoomTranscript.Lines(
        [
            Ev(1, "message.user", """{"text":"hello"}"""),
            Ev(2, "turn.started", """{"member_id":"ops"}"""),
            Ev(3, "message.member", """{"text":"on it","member_id":"ops"}"""),
            Ev(4, "turn.failed", """{"member_id":"scribe","error":"rate limited"}"""),
            Ev(5, "turn.settled", """{"member_id":"scribe","passed":true}"""),
            Ev(6, "turn.settled", """{"member_id":"ops"}"""),
            Ev(7, "turn.cancelled", """{"reason":"you asked"}"""),
            Ev(8, "room.renamed", """{"name":"Release"}"""),
            Ev(9, "room.activity", """{"status":"settled"}"""),
            Ev(10, "message.user", """{"text":"  "}"""),
        ], Members);
        Assert.Equal(6, lines.Count);
        Assert.True(((RoomLine.Message)lines[0]).FromUser);
        var m = (RoomLine.Message)lines[1];
        Assert.Equal(("Ops Bot", "ops-bot"), (m.Speaker, m.Profile));
        Assert.Equal("scribe couldn't take their turn: rate limited", ((RoomLine.System)lines[2]).Text);
        Assert.Equal("scribe had nothing to add.", ((RoomLine.System)lines[3]).Text);
        Assert.Equal("Turn stopped: you asked", ((RoomLine.System)lines[4]).Text);
        Assert.Equal("Renamed to “Release”.", ((RoomLine.System)lines[5]).Text);
    }

    [Fact]
    public void Merging_pages_never_draws_a_line_twice_and_stays_in_order()
    {
        var a = new[] { Ev(1, "message.user", """{"text":"a"}"""), Ev(2, "message.user", """{"text":"b"}""") };
        var b = new[] { Ev(2, "message.user", """{"text":"b"}"""), Ev(3, "message.user", """{"text":"c"}""") };
        Assert.Equal([1, 2, 3], RoomTranscript.Merge(a, b).Select(e => e.Seq));
        Assert.Equal([1, 2, 3], RoomTranscript.Merge(b.Concat([Ev(1, "message.user", """{"text":"a"}""")]).ToList(), a).Select(e => e.Seq).Order());
        Assert.Same(a, RoomTranscript.Merge(a, []));
    }

    // ---- who a message asks ----

    [Fact]
    public void A_message_asks_the_members_it_names_everyone_for_all_and_everyone_when_it_names_no_one()
    {
        Assert.Equal(["ops"], RoomMentions.Recipients("hi @ops please", Members).Select(m => m.MemberId));
        Assert.Equal(["ops"], RoomMentions.Recipients("hi @OPS", Members).Select(m => m.MemberId));          // handles match without case
        Assert.Equal(2, RoomMentions.Recipients("@all status?", Members).Count);
        Assert.Equal(2, RoomMentions.Recipients("@everyone status?", Members).Count);
        Assert.Equal(2, RoomMentions.Recipients("anyone there? @nobody", Members).Count);                     // unknown handle: asks everyone
        Assert.Equal(["scribe"], RoomMentions.Recipients("@scribe and @nobody", Members).Select(m => m.MemberId));
        Assert.Equal(2, RoomMentions.Recipients("no mention", Members).Count);
    }

    [Fact]
    public void The_picker_matches_handles_and_name_words_best_first_and_skips_members_without_a_handle()
    {
        IReadOnlyList<HostedRoomMember> all = [Ops, Scribe, NoHandle];
        Assert.Equal(["ops", "scribe"], RoomMentions.Mentionable(all, "").Select(m => m.Handle));
        Assert.Equal(["ops"], RoomMentions.Mentionable(all, "o").Select(m => m.Handle));
        Assert.Equal(["ops"], RoomMentions.Mentionable(all, "bot").Select(m => m.Handle));                    // a word of "Ops Bot"
        Assert.Equal(["scribe"], RoomMentions.Mentionable(all, "crib").Select(m => m.Handle));               // contained in the handle
        Assert.True(RoomMentions.MentionsEveryone("ever"));
        Assert.False(RoomMentions.MentionsEveryone("x"));
    }

    [Theory]
    [InlineData("hi @op", 6, true, "op", 3, 6)]
    [InlineData("@ops", 4, true, "ops", 0, 4)]
    [InlineData("hi @op", 5, true, "o", 3, 6)]                       // cursor mid-word: the whole word is replaced
    [InlineData("mail me@exam", 12, false, "", 0, 0)]              // an e-mail address is not a mention (only letters, so only the word-start rule stops it)
    [InlineData("hi there", 8, false, "", 0, 0)]
    [InlineData("hi @op ", 7, false, "", 0, 0)]                      // the word has ended
    public void The_mention_being_typed_is_found_only_at_a_word_start(string text, int cursor, bool found, string query, int start, int end)
    {
        var q = RoomMentions.QueryAt(text, cursor);
        Assert.Equal(found, q is not null);
        if (q is null) return;
        Assert.Equal((query, start, end), (q.Query, q.Start, q.End));
    }

    [Fact]
    public void Picking_a_member_replaces_the_whole_typed_word_and_leaves_one_space()
    {
        var q = RoomMentions.QueryAt("ask @op now", 7)!;
        var (text, cursor) = RoomMentions.Apply("ask @op now", q, "ops");
        Assert.Equal("ask @ops now", text);
        Assert.Equal(9, cursor);
        var q2 = RoomMentions.QueryAt("@o", 2)!;
        Assert.Equal(("@ops ", 5), RoomMentions.Apply("@o", q2, "ops"));
    }

    // ---- who is still replying ----

    [Fact]
    public void Waiting_lists_the_named_members_who_have_not_answered_yet_in_roster_order()
    {
        var events = new List<RoomEvent>
        {
            Ev(1, "message.user", """{"text":"@all status?"}""", "ask"),
            Ev(2, "message.member", """{"member_id":"ops","text":"fine","discussion_event_id":"ask","round_index":0}"""),
        };
        var waiting = RoomTranscript.WaitingFor(events, Members);
        Assert.Equal(["scribe"], waiting.Select(m => m.MemberId));
        Assert.Equal("scribe is replying…", RoomTranscript.WaitingLine(waiting));
        Assert.Equal("Ops Bot is replying · then scribe", RoomTranscript.WaitingLine(Members));
        Assert.Null(RoomTranscript.WaitingLine([]));
    }

    [Fact]
    public void Waiting_ends_when_the_discussion_settles_and_a_deferred_turn_goes_last()
    {
        var settled = new List<RoomEvent>
        {
            Ev(1, "message.user", """{"text":"hi"}""", "ask"),
            Ev(2, "room.activity", """{"status":"settled","discussion_event_id":"ask"}"""),
        };
        Assert.Empty(RoomTranscript.WaitingFor(settled, Members));
        Assert.Empty(RoomTranscript.WaitingFor([], Members));

        var deferred = new List<RoomEvent>
        {
            Ev(1, "message.user", """{"text":"hi"}""", "ask"),
            Ev(2, "turn.deferred", """{"member_id":"ops","discussion_event_id":"ask","round_index":0}"""),
        };
        Assert.Equal(["scribe", "ops"], RoomTranscript.WaitingFor(deferred, Members).Select(m => m.MemberId));
    }

    // ---- writes ----

    [Fact]
    public async Task Send_carries_trimmed_text_the_thread_and_the_idempotency_id_and_waits_longer()
    {
        var p = new Peer { Reply = _ => JsonNode.Parse("""
            {"event":{"room_id":"r1","seq":8,"event_id":"user:8","kind":"message.user","actor":{"kind":"user","id":"desktop"},"payload":{"text":"hi there"},"created_at":12.0},"accepted":true}
            """) };
        var ev = await Api(p).SendAsync("r1", "  hi there \n", "c-1");
        Assert.Equal(8, ev.Seq);
        var call = Assert.Single(p.Calls);
        Assert.Equal("groups.send", call.Method);
        Assert.Equal("c-1", (string)call.Args["event_id"]!);
        Assert.Equal("hi there", (string)call.Args["payload"]!["text"]!);
        Assert.Equal("main", (string)call.Args["payload"]!["thread_id"]!);
        Assert.True(call.Timeout >= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Nothing_is_sent_for_empty_over_long_or_id_less_requests()
    {
        var p = new Peer();
        var api = Api(p);
        await Assert.ThrowsAsync<RoomException>(() => api.SendAsync("r1", "   ", "c"));
        await Assert.ThrowsAsync<RoomException>(() => api.SendAsync("r1", new string('a', RoomMentions.MaxText + 1), "c"));
        await Assert.ThrowsAsync<RoomException>(() => api.SendAsync("", "hi", "c"));
        await Assert.ThrowsAsync<RoomException>(() => api.SendAsync("r1", "hi", " "));
        await Assert.ThrowsAsync<RoomException>(() => api.RenameAsync("r1", "  ", "e"));
        Assert.Empty(p.Calls);
    }

    [Fact]
    public async Task Stop_disband_rename_and_retry_send_their_documented_params()
    {
        var p = new Peer { Reply = m => m == "groups.rename" ? JsonNode.Parse("""{"room":{"room_id":"r1","name":"New"}}""") : new JsonObject() };
        var api = Api(p);
        await api.StopAsync("r1", "cancel-1");
        await api.DisbandAsync("r1", "cancel-2");
        var room = await api.RenameAsync("r1", " New ", "ev-1");
        await api.RetryAsync("r1", "t-4");
        Assert.Equal("New", room.Name);
        Assert.Equal(["groups.stop", "groups.disband", "groups.rename", "groups.retry"], p.Calls.Select(c => c.Method));
        Assert.Equal("cancel-1", (string)p.Calls[0].Args["cancel_id"]!);
        Assert.Equal("cancel-2", (string)p.Calls[1].Args["cancel_id"]!);
        Assert.Equal(("ev-1", "New"), (((string)p.Calls[2].Args["event_id"]!), (string)p.Calls[2].Args["name"]!));
        Assert.Equal("t-4", (string)p.Calls[3].Args["task_id"]!);
    }

    [Fact]
    public async Task Approve_answers_only_once_or_deny_with_the_whole_coordinate_tuple()
    {
        var p = new Peer();
        var api = Api(p);
        var action = new RoomPendingAction("approval", "ops", "t-9", 2, "a-3", "git push");
        await api.ApproveAsync("r1", action, "once", "req-1");
        var args = Assert.Single(p.Calls).Args;
        Assert.Equal(("ops", "t-9", 2, "once", "req-1"),
            ((string)args["member_id"]!, (string)args["task_id"]!, (int)args["execution_generation"]!, (string)args["choice"]!, (string)args["request_id"]!));

        await Assert.ThrowsAsync<RoomException>(() => api.ApproveAsync("r1", action, "always", "req-2"));
        await Assert.ThrowsAsync<RoomException>(() => api.ApproveAsync("r1", action, "session", "req-2"));
        await Assert.ThrowsAsync<RoomException>(() => api.ApproveAsync("r1", action with { ExecutionGeneration = null }, "once", "req-2"));
        await Assert.ThrowsAsync<RoomException>(() => api.ApproveAsync("r1", action with { Kind = "retry" }, "deny", "req-2"));
        Assert.Single(p.Calls);                                                             // the refusals never reached the gateway
    }

    // ---- failures in words ----

    [Fact]
    public async Task Failures_are_typed_and_in_words()
    {
        var api = new HostedRoomsApi(() => null);
        Assert.Equal(RoomFailureKind.NotConnected, (await Assert.ThrowsAsync<RoomException>(() => api.ListAsync())).Kind);

        var p = new Peer { Throw = new RpcException(-32601, "method not found") };
        var unsupported = await Assert.ThrowsAsync<RoomException>(() => Api(p).ListAsync());
        Assert.Equal(RoomFailureKind.Unsupported, unsupported.Kind);
        Assert.Contains("does not host rooms", unsupported.Message);

        p.Throw = new RpcException(5000, "room is busy");
        var refused = await Assert.ThrowsAsync<RoomException>(() => Api(p).SendAsync("r1", "hi", "c"));
        Assert.Equal(("room is busy", RoomFailureKind.Failed), (refused.Message, refused.Kind));

        p.Throw = new RpcTimeoutException("groups.send");
        var slow = await Assert.ThrowsAsync<RoomException>(() => Api(p).SendAsync("r1", "hi", "c"));
        Assert.Contains("before sending again", slow.Message);
    }
}
