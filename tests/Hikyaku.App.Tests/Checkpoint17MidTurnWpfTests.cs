using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 17a — Steer / Queue / Stop &amp; send / /btw / /bg through the real MainViewModel with a scripted transport that can
/// hold a turn open. Covers outcomes, ordering, refusal and failure paths, chat switching, and "never resent automatically".
/// </summary>
[Collection("StaMath")]
public class Checkpoint17MidTurnWpfTests
{
    /// <summary>Holds a turn open without honouring cancellation (a turn that will not settle).</summary>
    private sealed record Stubborn(TaskCompletionSource Gate);

    private sealed class Script : IGatewayTransport, IMidTurnTransport
    {
        public readonly List<ChatRequest> Requests = new();
        public readonly List<string> Steers = new();
        public readonly List<(SideTaskKind Kind, string Text)> Sides = new();
        /// <summary>Per request: ChatEvent items are yielded; a TaskCompletionSource holds the turn open; an Exception is thrown.</summary>
        public Func<ChatRequest, IEnumerable<object>> Turn = _ => [new StreamCompleted()];
        public SteerOutcome Outcome = SteerOutcome.Accepted;
        public Exception? SteerThrow;
        public Task? SteerGate;                                                       // holds the gateway's answer to a steer
        public Func<SideTaskKind, string, Task<string?>> Side = (_, _) => Task.FromResult<string?>("t1");

        public async IAsyncEnumerable<ChatEvent> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            foreach (var step in Turn(request))
            {
                ct.ThrowIfCancellationRequested();
                switch (step)
                {
                    case ChatEvent e: yield return e; break;
                    case TaskCompletionSource hold: await hold.Task.WaitAsync(ct); break;
                    case Stubborn s: await s.Gate.Task; break;
                    case Exception ex: throw ex;
                }
            }
        }

        public Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProfileInfo>>([]);

        public async Task<SteerOutcome> SteerAsync(ProfileKey profile, string runtimeSessionId, string text, CancellationToken ct)
        {
            Steers.Add(text);
            if (SteerGate is not null) await SteerGate;
            if (SteerThrow is not null) throw SteerThrow;
            return Outcome;
        }

        public Task<string?> StartSideTaskAsync(ProfileKey profile, string runtimeSessionId, SideTaskKind kind, string text, CancellationToken ct)
        {
            Sides.Add((kind, text));
            return Side(kind, text);
        }
    }

    private sealed class EventPeer : IJsonRpcPeer, IGatewayEventSource
    {
        public event Action<GatewayEvent>? EventReceived;
        public Task<JsonNode?> RequestAsync(string method, JsonObject? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default) =>
            Task.FromResult<JsonNode?>(null);
        public void Raise(string type, string json, string? session = "rt1") =>
            EventReceived?.Invoke(new GatewayEvent(type, JsonNode.Parse(json), session, null));
    }

    private sealed record Fixture(MainViewModel Vm, Script T, EventPeer Peer, ConversationVM Convo)
    {
        public IEnumerable<string> Texts => Vm.Messages.Select(m => m.Text);
    }

    private static Fixture Make()
    {
        var t = new Script();
        var peer = new EventPeer();
        var selection = new ProfileSelectionService(new MemPrefs());
        selection.SetSelectedAsync(new[] { new ProfileKey("demo", "default") }).GetAwaiter().GetResult();
        var vm = new MainViewModel(selection, new NotificationGate(selection), () => t, () => "t",
            () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null, rpc: () => peer);
        vm.NewChat();
        var convo = vm.SelectedConversation!;
        convo.SessionId = "rt1";
        return new Fixture(vm, t, peer, convo);
    }

    private static TaskCompletionSource Hold() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>First turn: streams "A", holds, streams "B". Anything later completes at once (or per <paramref name="later"/>).</summary>
    private static void FirstTurnHolds(Fixture f, TaskCompletionSource hold, Func<ChatRequest, IEnumerable<object>>? later = null) =>
        f.T.Turn = req => f.T.Requests.Count == 1
            ? [new TokenDelta("A"), hold, new TokenDelta("B"), new StreamCompleted()]
            : (later?.Invoke(req) ?? [new TokenDelta("next reply"), new StreamCompleted()]);

    private static void StartFirst(Fixture f, string text = "first")
    {
        f.Vm.InputText = text;
        f.Vm.SendCommand.Execute(null);
        Pump(150);
    }

    // ---- state -----------------------------------------------------------------------------------

    [Fact]
    public void While_a_reply_runs_send_becomes_steer_and_the_running_actions_appear()
    {
        RunSta(() =>
        {
            var f = Make();
            var hold = Hold();
            FirstTurnHolds(f, hold);
            Assert.Equal("Send", f.Vm.SendLabel);
            Assert.Equal(Visibility.Collapsed, f.Vm.RunningActionsVisibility);
            StartFirst(f);
            Assert.Equal("Steer", f.Vm.SendLabel);
            Assert.Equal(Visibility.Visible, f.Vm.RunningActionsVisibility);

            Assert.False(f.Vm.QueueCommand!.CanExecute(null));          // nothing typed yet
            Assert.False(f.Vm.StopAndSendCommand!.CanExecute(null));
            Assert.False(f.Vm.SendCommand.CanExecute(null));
            f.Vm.InputText = "more";
            Assert.True(f.Vm.QueueCommand.CanExecute(null));
            Assert.True(f.Vm.StopAndSendCommand.CanExecute(null));
            Assert.True(f.Vm.SendCommand.CanExecute(null));

            hold.SetResult();
            Pump(200);
            Assert.Equal("Send", f.Vm.SendLabel);
            Assert.False(f.Vm.QueueCommand.CanExecute(null));           // idle again
        });
    }

    // ---- steer -----------------------------------------------------------------------------------

    [Fact]
    public void An_accepted_steer_shows_the_steer_text_and_the_rest_of_the_reply_continues_below_it()
    {
        RunSta(() =>
        {
            var f = Make();
            var hold = Hold();
            FirstTurnHolds(f, hold);
            StartFirst(f);
            f.Vm.InputText = "use the other file";
            f.Vm.SendCommand.Execute(null);
            Pump(120);

            Assert.Equal(new[] { "use the other file" }, f.T.Steers);
            Assert.Equal("", f.Vm.InputText);
            hold.SetResult();
            Pump(250);
            Assert.Equal(new[] { "first", "A", "use the other file", "B" }, f.Texts);
            Assert.Equal(new[] { "You", "Assistant", "You", "Assistant" }, f.Vm.Messages.Select(m => m.Role));
            Assert.Single(f.T.Requests);                                  // a steer is not a new prompt
        });
    }

    [Theory]
    [InlineData(SteerOutcome.Rejected, "couldn't take that now")]
    [InlineData(SteerOutcome.Unsupported, "can't steer")]
    public void A_refused_steer_is_queued_instead_and_sent_after_a_clean_finish(SteerOutcome outcome, string note)
    {
        RunSta(() =>
        {
            var f = Make();
            var hold = Hold();
            FirstTurnHolds(f, hold);
            f.T.Outcome = outcome;
            StartFirst(f);
            f.Vm.InputText = "more";
            f.Vm.SendCommand.Execute(null);
            Pump(120);

            Assert.Contains(note, f.Vm.RewindNotice, StringComparison.OrdinalIgnoreCase);
            var queued = Assert.Single(f.Vm.Messages, m => m.IsQueued);
            Assert.Equal("more", queued.Text);
            Assert.Equal(queued, f.Vm.Messages.Last());                    // queued bubbles sit last
            Assert.Single(f.T.Requests);

            hold.SetResult();
            Pump(300);
            Assert.Equal(2, f.T.Requests.Count);
            Assert.Equal("more", f.T.Requests[1].Messages.Last().Content);
            Assert.DoesNotContain(f.Vm.Messages, m => m.IsQueued);
            Assert.Equal(new[] { "first", "AB", "more", "next reply" }, f.Texts);
        });
    }

    [Fact]
    public void A_gateway_error_on_steer_gives_the_text_back_and_shows_no_bubble()
    {
        RunSta(() =>
        {
            var f = Make();
            var hold = Hold();
            FirstTurnHolds(f, hold);
            f.T.SteerThrow = new RpcException(5000, "kaput");
            StartFirst(f);
            f.Vm.InputText = "more";
            f.Vm.SendCommand.Execute(null);
            Pump(120);
            Assert.Contains("kaput", f.Vm.RewindNotice);
            Assert.Equal("more", f.Vm.InputText);
            Assert.DoesNotContain(f.Vm.Messages, m => m.Text == "more");
            hold.SetResult();
            Pump(200);
            Assert.Single(f.T.Requests);
        });
    }

    [Fact]
    public void A_steer_with_no_answer_is_marked_unconfirmed_and_never_resent()
    {
        RunSta(() =>
        {
            var f = Make();
            var hold = Hold();
            FirstTurnHolds(f, hold);
            f.T.SteerThrow = new HttpRequestException("connection dropped");
            StartFirst(f);
            f.Vm.InputText = "more";
            f.Vm.SendCommand.Execute(null);
            Pump(120);
            var bubble = f.Vm.Messages.Single(m => m.Text == "more");
            Assert.Contains("NOT resent", bubble.Activity);
            Assert.Equal("", f.Vm.InputText);                              // not handed back to be sent twice
            hold.SetResult();
            Pump(250);
            Assert.Single(f.T.Requests);
            Assert.Single(f.T.Steers);
        });
    }

    // ---- queue -----------------------------------------------------------------------------------

    [Fact]
    public void Queue_holds_the_prompt_below_the_running_reply_then_sends_it_as_a_normal_turn()
    {
        RunSta(() =>
        {
            var f = Make();
            var hold = Hold();
            FirstTurnHolds(f, hold);
            StartFirst(f);
            f.Vm.InputText = "q1";
            f.Vm.QueueCommand!.Execute(null);
            Pump(60);
            Assert.Equal("", f.Vm.InputText);
            var bubble = Assert.Single(f.Vm.Messages, m => m.IsQueued);
            Assert.Contains("queued", bubble.Activity, StringComparison.OrdinalIgnoreCase);
            Assert.Single(f.T.Requests);
            Assert.Empty(f.T.Steers);

            hold.SetResult();
            Pump(300);
            Assert.Equal(2, f.T.Requests.Count);
            Assert.Equal("q1", f.T.Requests[1].Messages.Last().Content);
            Assert.Equal(new[] { "first", "AB", "q1", "next reply" }, f.Texts);
            Assert.Equal(2, f.T.Requests[1].Messages.Count(m => m.Role == "user"));   // history carried, queue marker excluded
        });
    }

    [Fact]
    public void Several_queued_prompts_go_one_at_a_time_in_order()
    {
        RunSta(() =>
        {
            var f = Make();
            var hold1 = Hold();
            var hold2 = Hold();
            f.T.Turn = req => f.T.Requests.Count switch
            {
                1 => [hold1, new StreamCompleted()],
                2 => [hold2, new StreamCompleted()],
                _ => [new StreamCompleted()]
            };
            StartFirst(f);
            foreach (var q in new[] { "q1", "q2" })
            {
                f.Vm.InputText = q;
                f.Vm.QueueCommand!.Execute(null);
            }
            Pump(60);
            Assert.Equal(new[] { "first", "", "q1", "q2" }, f.Texts);

            hold1.SetResult();
            Pump(200);
            Assert.Equal(2, f.T.Requests.Count);                          // q1 only; q2 waits its turn
            Assert.Equal("q1", f.T.Requests[1].Messages.Last().Content);
            Assert.Single(f.Vm.Messages, m => m.IsQueued);
            Assert.Equal("q2", f.Vm.Messages.Last().Text);                // still below the running reply

            hold2.SetResult();
            Pump(250);
            Assert.Equal(new[] { "q1", "q2" }, f.T.Requests.Skip(1).Select(r => r.Messages.Last().Content));
        });
    }

    [Fact]
    public void Stop_puts_queued_text_back_in_the_composer_and_sends_nothing()
    {
        RunSta(() =>
        {
            var f = Make();
            var hold = Hold();
            FirstTurnHolds(f, hold);
            StartFirst(f);
            foreach (var q in new[] { "q1", "q2" })
            {
                f.Vm.InputText = q;
                f.Vm.QueueCommand!.Execute(null);
            }
            f.Vm.CancelCommand.Execute(null);
            Pump(250);

            Assert.Single(f.T.Requests);
            Assert.Equal("q1\n\nq2", f.Vm.InputText);
            Assert.DoesNotContain(f.Vm.Messages, m => m.IsQueued);
            Assert.Contains("Nothing was sent", f.Vm.RewindNotice);
            Assert.False(f.Vm.IsEditing);
        });
    }

    [Fact]
    public void A_failed_turn_never_releases_the_queue()
    {
        RunSta(() =>
        {
            var f = Make();
            var hold = Hold();
            f.T.Turn = _ => [hold, new ProtocolError("boom")];
            StartFirst(f);
            f.Vm.InputText = "q1";
            f.Vm.QueueCommand!.Execute(null);
            hold.SetResult();
            Pump(250);
            Assert.Single(f.T.Requests);
            Assert.Equal("q1", f.Vm.InputText);
            Assert.DoesNotContain(f.Vm.Messages, m => m.IsQueued);
        });
    }

    [Fact]
    public void A_dropped_connection_never_releases_the_queue()
    {
        RunSta(() =>
        {
            var f = Make();
            var hold = Hold();
            f.T.Turn = _ => [hold, new IOException("socket closed")];
            StartFirst(f);
            f.Vm.InputText = "q1";
            f.Vm.QueueCommand!.Execute(null);
            hold.SetResult();
            Pump(300);
            Assert.Single(f.T.Requests);
            Assert.Equal("q1", f.Vm.InputText);
        });
    }

    [Fact]
    public void If_the_user_switched_chats_queued_text_is_never_sent_there_and_comes_back_on_return()
    {
        RunSta(() =>
        {
            var f = Make();
            var hold = Hold();
            FirstTurnHolds(f, hold);
            StartFirst(f);
            f.Vm.InputText = "q1";
            f.Vm.QueueCommand!.Execute(null);

            f.Vm.NewChat();                                               // user moves to another chat
            hold.SetResult();
            Pump(300);
            Assert.Single(f.T.Requests);                                  // not sent into either chat
            Assert.Equal("", f.Vm.InputText);

            f.Vm.SelectedConversation = f.Convo;
            Assert.Equal("q1", f.Vm.InputText);
            Assert.Contains("not sent", f.Vm.RewindNotice);
            Assert.Empty(f.Convo.UnsentFromQueue);
            Assert.DoesNotContain(f.Vm.Messages, m => m.IsQueued);
        });
    }

    [Fact]
    public void Unqueue_returns_that_prompt_to_the_composer_and_it_is_not_sent()
    {
        RunSta(() =>
        {
            var f = Make();
            var hold = Hold();
            FirstTurnHolds(f, hold);
            StartFirst(f);
            f.Vm.InputText = "q1";
            f.Vm.QueueCommand!.Execute(null);
            var bubble = f.Vm.Messages.Single(m => m.IsQueued);
            Assert.True(f.Vm.UnqueueCommand!.CanExecute(bubble));
            f.Vm.UnqueueCommand.Execute(bubble);
            Assert.Equal("q1", f.Vm.InputText);
            Assert.DoesNotContain(f.Vm.Messages, m => m.IsQueued);
            hold.SetResult();
            Pump(250);
            Assert.Single(f.T.Requests);
        });
    }

    // ---- stop & send -----------------------------------------------------------------------------

    [Fact]
    public void Stop_and_send_replaces_the_reply_with_a_fresh_turn_and_returns_older_queued_text()
    {
        RunSta(() =>
        {
            var f = Make();
            var hold = Hold();
            FirstTurnHolds(f, hold);
            StartFirst(f);
            f.Vm.InputText = "older queued";
            f.Vm.QueueCommand!.Execute(null);
            f.Vm.InputText = "new instead";
            f.Vm.StopAndSendCommand!.Execute(null);
            Pump(400);

            Assert.Equal(2, f.T.Requests.Count);
            Assert.Equal("new instead", f.T.Requests[1].Messages.Last().Content);
            Assert.Contains("Stopped", f.Vm.Messages.First(m => m.Role == "Assistant").Activity);
            Assert.Equal("older queued", f.Vm.InputText);                   // given back, not sent
            Assert.DoesNotContain(f.Vm.Messages, m => m.IsQueued);
        });
    }

    [Fact]
    public void A_reply_that_will_not_stop_gets_the_new_prompt_queued_behind_it_and_it_still_runs()
    {
        RunSta(() =>
        {
            var f = Make();
            var gate = Hold();
            f.T.Turn = _ => f.T.Requests.Count == 1
                ? [new Stubborn(gate), new StreamCompleted()]
                : [new TokenDelta("after"), new StreamCompleted()];
            f.Vm.StopSettleTimeout = TimeSpan.FromMilliseconds(100);
            StartFirst(f);
            f.Vm.InputText = "new instead";
            f.Vm.StopAndSendCommand!.Execute(null);
            Pump(400);
            Assert.Single(f.T.Requests);                                   // not folded into the turn it should replace
            Assert.Contains("taking a while", f.Vm.RewindNotice);
            Assert.Single(f.Vm.Messages, m => m.IsQueued);

            gate.SetResult();                                              // the stopped turn finally ends
            Pump(400);
            Assert.Equal(2, f.T.Requests.Count);
            Assert.Equal("new instead", f.T.Requests[1].Messages.Last().Content);
        });
    }

    [Fact]
    public void Stop_and_send_never_delivers_the_text_into_a_chat_the_user_moved_to_meanwhile()
    {
        RunSta(() =>
        {
            var f = Make();
            var gate = Hold();
            f.T.Turn = _ => f.T.Requests.Count == 1
                ? [new Stubborn(gate), new StreamCompleted()]
                : [new TokenDelta("after"), new StreamCompleted()];
            StartFirst(f);
            f.Vm.InputText = "meant for the first chat";
            f.Vm.StopAndSendCommand!.Execute(null);
            Pump(100);
            f.Vm.NewChat();                                               // the person moves on while the old reply is still stopping
            gate.SetResult();
            Pump(400);
            Assert.Single(f.T.Requests);                                  // nothing was sent, in either chat
            f.Vm.SelectedConversation = f.Convo;
            Assert.Equal("meant for the first chat", f.Vm.InputText);     // it is waiting where it was written
        });
    }

    [Fact]
    public void A_refused_steer_whose_reply_ended_while_the_user_moved_to_another_chat_is_never_sent_there()
    {
        RunSta(() =>
        {
            var f = Make();
            var hold = Hold();
            FirstTurnHolds(f, hold);
            var gate = new TaskCompletionSource();
            f.T.SteerGate = gate.Task;
            f.T.Outcome = SteerOutcome.Rejected;
            StartFirst(f);
            f.Vm.InputText = "more";
            f.Vm.SendCommand.Execute(null);                               // a steer, waiting for the gateway's answer
            Pump(60);
            hold.SetResult();                                             // the reply ends meanwhile
            Pump(150);
            f.Vm.NewChat();                                               // and the person moves to another chat
            gate.SetResult();                                             // now the gateway says "refused"
            Pump(300);
            Assert.Single(f.T.Requests);                                  // not sent into the other chat
            f.Vm.SelectedConversation = f.Convo;
            Assert.Equal("more", f.Vm.InputText);                         // waiting where it was written
        });
    }

    // ---- /btw and /bg ----------------------------------------------------------------------------

    [Fact]
    public void Btw_starts_a_side_question_without_a_turn_and_its_answer_fills_the_card()
    {
        RunSta(() =>
        {
            var f = Make();
            f.Vm.InputText = "/btw what is 2+2?";
            f.Vm.SendCommand.Execute(null);
            Pump(80);

            Assert.Equal(new[] { (SideTaskKind.Btw, "what is 2+2?") }, f.T.Sides);
            Assert.Empty(f.T.Requests);                                    // no turn, no interruption
            Assert.Equal("", f.Vm.InputText);
            var card = f.Vm.Messages.Single();
            Assert.True(card.IsSideCard);
            Assert.Equal("/btw", card.Role);

            f.Peer.Raise("btw.complete", """{"task_id":"t1","text":"4","question":"what is 2+2?"}""");
            Pump(80);
            Assert.Equal("Q: what is 2+2?\n\nA: 4", card.Text);
            Assert.Equal("", card.Activity);
            Assert.False(card.IsFailed);

            // A later ordinary prompt never carries the card.
            f.Vm.InputText = "thanks";
            f.Vm.SendCommand.Execute(null);
            Pump(120);
            Assert.DoesNotContain(f.T.Requests[0].Messages, m => m.Content.Contains("A: 4"));
        });
    }

    [Fact]
    public void Bg_runs_a_background_task_and_shows_just_its_answer()
    {
        RunSta(() =>
        {
            var f = Make();
            f.Vm.InputText = "/BG summarise the repo";
            f.Vm.SendCommand.Execute(null);
            Pump(80);
            Assert.Equal(new[] { (SideTaskKind.Background, "summarise the repo") }, f.T.Sides);
            var card = f.Vm.Messages.Single();
            Assert.Equal("/bg", card.Role);
            f.Peer.Raise("background.complete", """{"task_id":"t1","text":"It is a WPF app."}""");
            Pump(80);
            Assert.Equal("It is a WPF app.", card.Text);
        });
    }

    [Theory]
    [InlineData("/btw", "Usage: /btw")]
    [InlineData("/bg   ", "Usage: /bg")]
    public void A_side_command_with_no_text_explains_itself_and_calls_nothing(string typed, string usage)
    {
        RunSta(() =>
        {
            var f = Make();
            f.Vm.InputText = typed;
            f.Vm.SendCommand.Execute(null);
            Pump(60);
            Assert.Contains(usage, f.Vm.RewindNotice);
            Assert.Empty(f.T.Sides);
            Assert.Empty(f.Vm.Messages);
        });
    }

    [Theory]
    [InlineData("/btwfoo")]
    [InlineData("please /btw this")]
    [InlineData("/bgx do it")]
    public void Only_a_leading_slash_command_is_special(string typed)
    {
        RunSta(() =>
        {
            var f = Make();
            f.Vm.InputText = typed;
            f.Vm.SendCommand.Execute(null);
            Pump(120);
            Assert.Empty(f.T.Sides);
            Assert.Single(f.T.Requests);
            Assert.Equal(typed, f.T.Requests[0].Messages.Last().Content);
        });
    }

    [Fact]
    public void An_answer_that_arrives_before_the_start_call_returns_is_still_matched()
    {
        RunSta(() =>
        {
            var f = Make();
            var release = new TaskCompletionSource<string?>();
            f.T.Side = (_, _) => release.Task;
            f.Vm.InputText = "/btw quick";
            f.Vm.SendCommand.Execute(null);
            Pump(60);
            f.Peer.Raise("btw.complete", """{"task_id":"fast","text":"done early"}""");
            Pump(60);
            release.SetResult("fast");
            Pump(100);
            Assert.EndsWith("done early", f.Vm.Messages.Single().Text);
        });
    }

    [Fact]
    public void Side_task_failures_and_crash_reports_are_shown_as_failed()
    {
        RunSta(() =>
        {
            var f = Make();
            f.T.Side = (_, _) => throw new RpcException(-32601, "method not found");
            f.Vm.InputText = "/bg nope";
            f.Vm.SendCommand.Execute(null);
            Pump(80);
            var card = f.Vm.Messages.Single();
            Assert.True(card.IsFailed);
            Assert.Contains("Couldn't start", card.Activity);

            f.T.Side = (_, _) => Task.FromResult<string?>("t2");
            f.Vm.InputText = "/btw boom?";
            f.Vm.SendCommand.Execute(null);
            Pump(80);
            f.Peer.Raise("btw.complete", """{"task_id":"t2","text":"error: boom"}""");
            Pump(80);
            var second = f.Vm.Messages.Last();
            Assert.True(second.IsFailed);
            Assert.EndsWith("boom", second.Text);
            Assert.DoesNotContain("error:", second.Text);
        });
    }

    [Fact]
    public void Other_clients_side_tasks_do_not_create_cards_here()
    {
        RunSta(() =>
        {
            var f = Make();
            f.Vm.InputText = "/btw mine";
            f.Vm.SendCommand.Execute(null);
            Pump(60);
            f.Peer.Raise("btw.complete", """{"task_id":"someone-elses","text":"not for this chat"}""");
            f.Peer.Raise("btw.complete", """{"text":"no id"}""");
            Pump(80);
            Assert.Single(f.Vm.Messages);
        });
    }

    [Fact]
    public void A_side_question_can_be_asked_while_a_reply_is_running_without_interrupting_it()
    {
        RunSta(() =>
        {
            var f = Make();
            var hold = Hold();
            FirstTurnHolds(f, hold);
            StartFirst(f);
            f.Vm.InputText = "/btw how long?";
            Assert.True(f.Vm.SendCommand.CanExecute(null));
            f.Vm.SendCommand.Execute(null);
            Pump(80);
            Assert.Single(f.T.Sides);
            Assert.Empty(f.T.Steers);                                       // a side question is not a steer
            hold.SetResult();
            Pump(250);
            Assert.Single(f.T.Requests);
            Assert.Equal(new[] { "first", "AB", "how long?" }, f.Texts);
        });
    }

    // ---- STA helpers ---------------------------------------------------------------------------

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}

file sealed class MemPrefs : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
