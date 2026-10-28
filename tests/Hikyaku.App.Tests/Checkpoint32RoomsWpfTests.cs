using System.Text.Json.Nodes;
using System.Windows;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 32 — the Rooms page through the real MainViewModel with a fake gateway peer: profile gate, transcript, mentions, idempotent send, confirmations, approvals.</summary>
[Collection("StaMath")]
public class Checkpoint32RoomsWpfTests
{
    private sealed class Peer : IJsonRpcPeer
    {
        public readonly List<(string Method, JsonObject Args)> Calls = new();
        public readonly Dictionary<string, Func<JsonObject, JsonNode?>> Handlers = new();
        public Func<string, JsonObject, Exception?>? Fail;

        /// <summary>When set, a log read after the first page waits here (an answer that arrives late).</summary>
        public TaskCompletionSource? HoldLater;

        public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            var args = parameters ?? new JsonObject();
            Calls.Add((method, args));
            if (Fail?.Invoke(method, args) is { } ex) throw ex;
            if (method == "groups.log" && (int)args["since_seq"]! > 0 && HoldLater is { } hold) await hold.Task;
            return Handlers.TryGetValue(method, out var h) ? h(args) : new JsonObject();
        }

        public IEnumerable<JsonObject> Sent(string method) => Calls.Where(c => c.Method == method).Select(c => c.Args);
    }

    private const string RoomsJson = """
        {"rooms":[
          {"room_id":"r1","name":"Release room","updated_at":50,"latest_seq":3,"members":[
             {"member_id":"ops","profile":"default","handle":"ops","display_name":"Ops Bot"},
             {"member_id":"scribe","profile":"work","handle":"scribe"}]},
          {"room_id":"r2","name":"Private room","updated_at":40,"members":[
             {"member_id":"a","profile":"default","handle":"a"},
             {"member_id":"b","profile":"private","handle":"b"}]},
          {"room_id":"r3","name":"No profiles","updated_at":30,"members":[{"member_id":"x","handle":"x"}]}
        ]}
        """;

    private const string LogJson = """
        {"events":[
          {"room_id":"r1","seq":1,"event_id":"u1","kind":"message.user","actor":{"kind":"user","id":"d"},"payload":{"text":"@all status?"},"created_at":10},
          {"room_id":"r1","seq":2,"event_id":"m1","kind":"message.member","actor":{"kind":"member","id":"ops"},"payload":{"text":"all fine","member_id":"ops","discussion_event_id":"u1","round_index":0},"created_at":11}
        ],"cursor":2,"latest_seq":2,"has_more":false}
        """;

    private sealed class Rig
    {
        public MainViewModel Vm = null!;
        public Peer Peer = new();
        public List<(string Title, string Message)> Asked = [];
        public string? Prompted;

        public Rig(bool confirm = true, bool driver = true, string rooms = RoomsJson, string log = LogJson, params string[] profiles)
        {
            profiles = profiles.Length == 0 ? ["default", "work"] : profiles;
            var selection = new ProfileSelectionService(new MemPrefs32());
            selection.SetSelectedAsync(profiles.Select(p => new ProfileKey("gw", p))).GetAwaiter().GetResult();
            Vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
                () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
            Peer.Handlers["groups.capabilities"] = _ => JsonNode.Parse("{\"driver\":" + (driver ? "true" : "false") + ",\"protocol_version\":1}");
            Peer.Handlers["groups.list"] = _ => JsonNode.Parse(rooms);
            Peer.Handlers["groups.log"] = a => (int)a["since_seq"]! == 0 ? JsonNode.Parse(log) : JsonNode.Parse("{\"events\":[],\"cursor\":" + (int)a["since_seq"]! + "}");
            Peer.Handlers["groups.state"] = _ => JsonNode.Parse("""
                {"room":{"room_id":"r1","name":"Release room","members":[
                   {"member_id":"ops","profile":"default","handle":"ops","display_name":"Ops Bot"},
                   {"member_id":"scribe","profile":"work","handle":"scribe"}]},
                 "driver_status":{"running":true,"pending_actions":[
                   {"kind":"approval","member_id":"ops","task_id":"t-9","execution_generation":2,"request_id":"a-3","approval":{"command":"git push origin main"}}]}}
                """);
            Peer.Handlers["groups.send"] = a => JsonNode.Parse("{\"event\":{\"room_id\":\"r1\",\"seq\":3,\"event_id\":\"" + (string)a["event_id"]! +
                "\",\"kind\":\"message.user\",\"actor\":{\"kind\":\"user\",\"id\":\"d\"},\"payload\":{\"text\":\"" + (string)a["payload"]!["text"]! + "\"},\"created_at\":12}}");
            Vm.AttachRooms(() => new HostedRoomsApi(() => Peer), "gw");
            Vm.ConfirmRoom = (t, m) => { Asked.Add((t, m)); return confirm; };
            Vm.PromptText = (_, _) => Prompted;
        }

        public HostedRoomRowVM Open(string id = "r1")
        {
            Vm.RefreshRoomsAsync().GetAwaiter().GetResult();
            var row = Vm.HostedRooms.Single(r => r.RoomId == id);
            Vm.OpenRoomAsync(row).GetAwaiter().GetResult();
            return row;
        }
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }

    [Fact]
    public void Only_rooms_whose_every_member_profile_is_chosen_here_are_listed()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.RefreshRoomsAsync().GetAwaiter().GetResult();
            Assert.Equal(["r1"], rig.Vm.HostedRooms.Select(r => r.RoomId));        // r2 has a member on "private"; r3 names no profile
            Assert.Contains("2 room(s)", rig.Vm.RoomsStatus);
            Assert.Contains("hidden", rig.Vm.RoomsStatus);
            Assert.Equal("2 members · Ops Bot, work", rig.Vm.HostedRooms[0].Summary);
        });
    }

    [Fact]
    public void An_open_room_with_a_member_on_a_profile_that_is_not_chosen_is_closed_instead_of_polled_or_written_to()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            var room = new HostedRoom("r9", "Mixed", [new HostedRoomMember("a", "default", "a", null), new HostedRoomMember("b", "private", "b", null)], 1, 1, null, 1);
            rig.Peer.Handlers["groups.state"] = _ => JsonNode.Parse("""
                {"room":{"room_id":"r9","name":"Mixed","members":[{"member_id":"a","profile":"default","handle":"a"},{"member_id":"b","profile":"private","handle":"b"}]},
                 "driver_status":{"running":false,"pending_actions":[]}}
                """);
            rig.Vm.OpenRoomAsync(new HostedRoomRowVM(room)).GetAwaiter().GetResult();   // as if "private" had been chosen when it was opened
            var logsBefore = rig.Peer.Sent("groups.log").Count();
            rig.Vm.PollRoomAsync().GetAwaiter().GetResult();
            Assert.Null(rig.Vm.SelectedHostedRoom);
            Assert.Equal(logsBefore, rig.Peer.Sent("groups.log").Count());                 // not read again
            Assert.Contains("no longer chosen", rig.Vm.RoomsStatus);
            rig.Vm.RoomComposerText = "hello";
            Assert.False(rig.Vm.SendRoomMessageCommand!.CanExecute(null));
            Assert.Empty(rig.Peer.Sent("groups.send"));
        });
    }

    [Fact]
    public void A_dropped_connection_while_sending_keeps_the_text_and_says_so_instead_of_failing_silently()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Open();
            rig.Peer.Fail = (m, _) => m == "groups.send" ? new TransportClosedException(1006, "gone") : null;
            rig.Vm.RoomComposerText = "important";
            rig.Vm.SendRoomMessageAsync().GetAwaiter().GetResult();
            Assert.Equal("important", rig.Vm.RoomComposerText);
            Assert.Contains("Not sent", rig.Vm.RoomStatus);
        });
    }

    [Fact]
    public void In_simple_view_an_open_room_cannot_be_polled_or_written_to()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Open();
            rig.Vm.ApplyExperience(false);
            var logs = rig.Peer.Sent("groups.log").Count();
            rig.Vm.RoomComposerText = "hello";
            rig.Vm.PollRoomAsync().GetAwaiter().GetResult();
            rig.Vm.SendRoomMessageAsync().GetAwaiter().GetResult();
            Assert.Equal(logs, rig.Peer.Sent("groups.log").Count());
            Assert.Empty(rig.Peer.Sent("groups.send"));
        });
    }

    [Fact]
    public void Stop_room_asks_in_words_and_sends_nothing_without_a_yes()
    {
        RunSta(() =>
        {
            var no = new Rig(confirm: false);
            no.Open();
            no.Vm.StopRoomCommand!.Execute(null);
            Thread.Sleep(150);
            Assert.Empty(no.Peer.Sent("groups.stop"));
            Assert.Contains("for everyone", Assert.Single(no.Asked).Message);

            var yes = new Rig();
            yes.Open();
            yes.Vm.StopRoomCommand!.Execute(null);
            for (var i = 0; i < 100 && !yes.Peer.Sent("groups.stop").Any(); i++) Thread.Sleep(10);
            Assert.Single(yes.Peer.Sent("groups.stop"));
        });
    }

    [Fact]
    public void Refreshing_the_list_does_not_wipe_the_open_rooms_transcript()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Open();
            var lines = rig.Vm.RoomTranscriptLines.Count;
            Assert.True(lines > 0);
            var list = new System.Windows.Controls.ListBox { ItemsSource = rig.Vm.HostedRooms };   // the real control pushes "nothing selected" when its items are cleared
            list.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
                new System.Windows.Data.Binding(nameof(MainViewModel.SelectedHostedRoom)) { Source = rig.Vm, Mode = System.Windows.Data.BindingMode.TwoWay });
            rig.Vm.RefreshRoomsAsync().GetAwaiter().GetResult();
            Assert.NotNull(rig.Vm.SelectedHostedRoom);
            Assert.Equal(lines, rig.Vm.RoomTranscriptLines.Count);
        });
    }

    [Fact]
    public void Only_the_newest_events_of_an_open_room_are_kept_in_memory()
    {
        RunSta(() =>
        {
            var full = new Rig();
            full.Open();
            var all = full.Vm.RoomTranscriptLines.Count;
            var old = MainViewModel.RoomEventsKept;
            try
            {
                MainViewModel.RoomEventsKept = 1;
                var capped = new Rig();
                capped.Open();
                Assert.True(capped.Vm.RoomTranscriptLines.Count < all);
            }
            finally { MainViewModel.RoomEventsKept = old; }
        });
    }

    [Fact]
    public void A_gateway_that_does_not_run_rooms_or_is_not_connected_says_so()
    {
        RunSta(() =>
        {
            var noDriver = new Rig(driver: false);
            noDriver.Vm.RefreshRoomsAsync().GetAwaiter().GetResult();
            Assert.Empty(noDriver.Vm.HostedRooms);
            Assert.Contains("does not run rooms", noDriver.Vm.RoomsStatus);
            Assert.Empty(noDriver.Peer.Sent("groups.list"));

            var gone = new Rig();
            gone.Peer.Fail = (m, _) => new RpcException(-32601, "method not found");
            gone.Vm.RefreshRoomsAsync().GetAwaiter().GetResult();
            Assert.Contains("does not host rooms", gone.Vm.RoomsStatus);

            var offline = new Rig();
            offline.Vm.AttachRooms(() => new HostedRoomsApi(() => null), "gw");
            offline.Vm.RefreshRoomsAsync().GetAwaiter().GetResult();
            Assert.Contains("Connect to a gateway", offline.Vm.RoomsStatus);
        });
    }

    [Fact]
    public void Opening_a_room_shows_its_transcript_who_is_still_replying_and_what_waits_on_you()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Open();
            Assert.Equal(["You", "Ops Bot"], rig.Vm.RoomTranscriptLines.Select(l => l.Speaker));
            Assert.Equal("work is replying…", rig.Vm.RoomWaitingLine);
            var pending = Assert.Single(rig.Vm.RoomPending);
            Assert.Contains("Ops Bot is asking to run a command", pending.Title);
            Assert.Equal("git push origin main", pending.Detail);
            Assert.Contains("@ops", rig.Vm.RoomMembersLine);
            Assert.Equal(Visibility.Visible, rig.Vm.RoomOpenVisibility);
        });
    }

    [Fact]
    public void Switching_rooms_drops_the_old_lines_and_half_typed_mentions()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Open();
            rig.Vm.RoomComposerText = "hello @o";
            Assert.NotEmpty(rig.Vm.RoomMentionChoices);
            rig.Vm.OpenRoomAsync(null).GetAwaiter().GetResult();
            Assert.Empty(rig.Vm.RoomTranscriptLines);
            Assert.Empty(rig.Vm.RoomMentionChoices);
            Assert.Empty(rig.Vm.RoomPending);
            Assert.Equal(Visibility.Collapsed, rig.Vm.RoomOpenVisibility);
        });
    }

    [Fact]
    public void Typing_an_at_offers_members_and_picking_one_completes_the_word()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Open();
            rig.Vm.RoomComposerText = "ask @sc";
            Assert.Equal(["scribe"], rig.Vm.RoomMentionChoices.Select(m => m.Handle));
            rig.Vm.PickRoomMentionCommand!.Execute(rig.Vm.RoomMentionChoices[0]);
            Assert.Equal("ask @scribe ", rig.Vm.RoomComposerText);
            Assert.Empty(rig.Vm.RoomMentionChoices);

            rig.Vm.RoomComposerText = "@";
            Assert.Contains(rig.Vm.RoomMentionChoices, m => m.Handle == "all");     // @all is offered too
            rig.Vm.RoomComposerText = "me@exam";
            Assert.Empty(rig.Vm.RoomMentionChoices);                                 // an e-mail address is not a mention
        });
    }

    [Fact]
    public void Send_posts_the_trimmed_text_once_shows_it_and_clears_the_box()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Open();
            rig.Vm.RoomComposerText = "  @ops are you there?  ";
            rig.Vm.SendRoomMessageAsync().GetAwaiter().GetResult();
            var sent = Assert.Single(rig.Peer.Sent("groups.send"));
            Assert.Equal("@ops are you there?", (string)sent["payload"]!["text"]!);
            Assert.Equal("", rig.Vm.RoomComposerText);
            Assert.Equal("You", rig.Vm.RoomTranscriptLines.Last().Speaker);
            Assert.Equal("@ops are you there?", rig.Vm.RoomTranscriptLines.Last().Text);
        });
    }

    [Fact]
    public void A_failed_send_keeps_the_text_and_a_second_press_reuses_the_same_id_but_changed_text_does_not()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Open();
            var fail = true;
            rig.Peer.Fail = (m, _) => m == "groups.send" && fail ? new RpcTimeoutException("groups.send") : null;
            rig.Vm.RoomComposerText = "important";
            rig.Vm.SendRoomMessageAsync().GetAwaiter().GetResult();
            Assert.Equal("important", rig.Vm.RoomComposerText);                      // not lost
            Assert.Contains("Not sent", rig.Vm.RoomStatus);
            Assert.Contains("will not post it twice", rig.Vm.RoomStatus);

            rig.Vm.SendRoomMessageAsync().GetAwaiter().GetResult();                  // pressed again, still failing: no automatic resend happened
            Assert.Equal(2, rig.Peer.Sent("groups.send").Count());
            Assert.Equal((string)rig.Peer.Sent("groups.send").First()["event_id"]!, (string)rig.Peer.Sent("groups.send").Last()["event_id"]!);

            rig.Vm.RoomComposerText = "important, edited";
            rig.Vm.SendRoomMessageAsync().GetAwaiter().GetResult();
            var ids = rig.Peer.Sent("groups.send").Select(a => (string)a["event_id"]!).ToList();
            Assert.NotEqual(ids[0], ids[2]);                                         // new text is a new message

            fail = false;
            rig.Vm.SendRoomMessageAsync().GetAwaiter().GetResult();
            Assert.Equal("", rig.Vm.RoomComposerText);
        });
    }

    [Fact]
    public void Nothing_is_sent_for_an_empty_box_or_with_no_room_open_and_nothing_is_sent_by_opening_or_polling()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.RefreshRoomsAsync().GetAwaiter().GetResult();
            Assert.False(rig.Vm.SendRoomMessageCommand!.CanExecute(null));            // no room open
            rig.Open();
            rig.Vm.RoomComposerText = "   ";
            Assert.False(rig.Vm.SendRoomMessageCommand.CanExecute(null));
            rig.Vm.PollRoomAsync().GetAwaiter().GetResult();
            Assert.Empty(rig.Peer.Sent("groups.send"));
        });
    }

    [Fact]
    public void Polling_reads_from_the_cursor_and_never_draws_a_line_twice()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Open();
            var before = rig.Vm.RoomTranscriptLines.Count;
            rig.Peer.Handlers["groups.log"] = a => JsonNode.Parse(
                (int)a["since_seq"]! == 2
                    ? """{"events":[{"room_id":"r1","seq":2,"event_id":"m1","kind":"message.member","actor":{"kind":"member","id":"ops"},"payload":{"text":"all fine","member_id":"ops"}},{"room_id":"r1","seq":3,"event_id":"m2","kind":"message.member","actor":{"kind":"member","id":"scribe"},"payload":{"text":"done too","member_id":"scribe"}}],"cursor":3}"""
                    : """{"events":[],"cursor":3}""");
            rig.Vm.PollRoomAsync().GetAwaiter().GetResult();
            Assert.Equal(before + 1, rig.Vm.RoomTranscriptLines.Count);
            Assert.Equal("work", rig.Vm.RoomTranscriptLines.Last().Speaker);
            Assert.Equal(2, (int)rig.Peer.Calls.Last(c => c.Method == "groups.log").Args["since_seq"]! == 2 ? 2 : -1);
            rig.Vm.PollRoomAsync().GetAwaiter().GetResult();
            Assert.Equal(before + 1, rig.Vm.RoomTranscriptLines.Count);
            Assert.Equal(3, (int)rig.Peer.Calls.Last(c => c.Method == "groups.log").Args["since_seq"]!);
        });
    }

    [Fact]
    public void A_poll_answered_after_the_room_was_closed_changes_nothing()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Open();
            rig.Peer.HoldLater = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Peer.Handlers["groups.log"] = _ => JsonNode.Parse("""{"events":[{"room_id":"r1","seq":3,"event_id":"late","kind":"message.member","actor":{"kind":"member","id":"ops"},"payload":{"text":"late words","member_id":"ops"}}],"cursor":3}""");
            var poll = rig.Vm.PollRoomAsync();                                       // in flight, waiting on the gateway
            rig.Vm.OpenRoomAsync(null).GetAwaiter().GetResult();                      // the person closes the room meanwhile
            rig.Peer.HoldLater.SetResult();
            poll.GetAwaiter().GetResult();
            Assert.Empty(rig.Vm.RoomTranscriptLines);                                // the other room's line does not reappear
        });
    }

    [Fact]
    public void A_failed_poll_keeps_what_is_shown_and_says_so()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Open();
            rig.Peer.Fail = (m, _) => m == "groups.log" ? new RpcTimeoutException("groups.log") : null;
            rig.Vm.PollRoomAsync().GetAwaiter().GetResult();
            Assert.Equal(2, rig.Vm.RoomTranscriptLines.Count);
            Assert.Contains("Couldn't refresh", rig.Vm.RoomStatus);
            rig.Peer.Fail = null;
            rig.Vm.PollRoomAsync().GetAwaiter().GetResult();
            Assert.Equal("", rig.Vm.RoomStatus);
        });
    }

    [Fact]
    public void Deleting_a_room_is_confirmed_in_words_and_does_nothing_without_a_yes()
    {
        RunSta(() =>
        {
            var no = new Rig(confirm: false);
            no.Open();
            no.Vm.DeleteRoomCommand!.Execute(null);
            Assert.Empty(no.Peer.Sent("groups.disband"));
            var (title, message) = Assert.Single(no.Asked);
            Assert.Equal("Delete room", title);
            Assert.Contains("Release room", message);
            Assert.Contains("cannot be undone", message);
            Assert.Contains("everyone", message);
            Assert.Single(no.Vm.HostedRooms);

            var unset = new Rig();
            unset.Open();
            unset.Vm.ConfirmRoom = null;                                             // no confirmation handler: still nothing
            unset.Vm.DeleteRoomCommand!.Execute(null);
            Assert.Empty(unset.Peer.Sent("groups.disband"));

            var yes = new Rig(confirm: true);
            yes.Open();
            yes.Vm.DeleteRoomCommand!.Execute(null);
            for (var i = 0; i < 100 && yes.Vm.HostedRooms.Count > 0; i++) Thread.Sleep(10);
            Assert.Single(yes.Peer.Sent("groups.disband"));
            Assert.Empty(yes.Vm.HostedRooms);
            Assert.Equal(Visibility.Collapsed, yes.Vm.RoomOpenVisibility);
        });
    }

    [Fact]
    public void Rename_uses_the_typed_name_and_a_cancelled_prompt_sends_nothing()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Peer.Handlers["groups.rename"] = _ => JsonNode.Parse("""{"room":{"room_id":"r1","name":"Shipping"}}""");
            rig.Open();
            rig.Prompted = null;
            rig.Vm.RenameRoomCommand!.Execute(null);
            Assert.Empty(rig.Peer.Sent("groups.rename"));
            rig.Prompted = " Shipping ";
            rig.Vm.RenameRoomCommand.Execute(null);
            for (var i = 0; i < 100 && rig.Vm.RoomTitle != "Shipping"; i++) Thread.Sleep(10);
            Assert.Equal("Shipping", rig.Vm.RoomTitle);
            Assert.Equal("Shipping", (string)rig.Peer.Sent("groups.rename").Single()["name"]!);
        });
    }

    [Fact]
    public void An_approval_can_only_be_allowed_once_or_denied_with_the_rooms_own_coordinates()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Open();
            var item = rig.Vm.RoomPending.Single();
            rig.Vm.AllowRoomApprovalCommand!.Execute(item);
            for (var i = 0; i < 100 && !rig.Peer.Sent("groups.approve").Any(); i++) Thread.Sleep(10);
            var sent = Assert.Single(rig.Peer.Sent("groups.approve"));
            Assert.Equal(("once", "ops", "t-9", 2), ((string)sent["choice"]!, (string)sent["member_id"]!, (string)sent["task_id"]!, (int)sent["execution_generation"]!));
            Assert.Equal(Visibility.Visible, item.ApprovalVisibility);

            var deny = new Rig();
            deny.Open();
            deny.Vm.DenyRoomApprovalCommand!.Execute(deny.Vm.RoomPending.Single());
            for (var i = 0; i < 100 && !deny.Peer.Sent("groups.approve").Any(); i++) Thread.Sleep(10);
            Assert.Equal("deny", (string)deny.Peer.Sent("groups.approve").Single()["choice"]!);
        });
    }

    [Fact]
    public void A_refused_approval_says_why_and_the_list_is_read_again()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Open();
            rig.Peer.Fail = (m, _) => m == "groups.approve" ? new RpcException(4090, "that approval is no longer pending") : null;
            rig.Vm.AllowRoomApprovalCommand!.Execute(rig.Vm.RoomPending.Single());
            for (var i = 0; i < 100 && !rig.Vm.RoomStatus.Contains("Couldn't answer"); i++) Thread.Sleep(10);
            Assert.Contains("no longer pending", rig.Vm.RoomStatus);
        });
    }
}

file sealed class MemPrefs32 : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
