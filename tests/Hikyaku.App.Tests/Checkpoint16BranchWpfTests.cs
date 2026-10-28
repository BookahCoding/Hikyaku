using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 16b — Branch through the real MainViewModel: command → all stored rows read through a real SessionCatalog →
/// count → session.branch(_whole) → new chat opened from the gateway's rows. Scripted transport and stub REST only.
/// </summary>
[Collection("StaMath")]
public class Checkpoint16BranchWpfTests
{
    private sealed class BranchScript : IGatewayTransport, IBranchingTransport
    {
        public readonly List<(ProfileKey Profile, string Runtime, int? Count)> Calls = new();
        public Func<int?, BranchResult> Result = _ => new BranchResult("branch-1", "Branch of chat");
        public Exception? Throw;
        public TaskCompletionSource? Hold;

        public async Task<BranchResult> BranchSessionAsync(ProfileKey profile, string runtimeSessionId, int? count, CancellationToken ct)
        {
            Calls.Add((profile, runtimeSessionId, count));
            if (Hold is not null) await Hold.Task.WaitAsync(ct);
            if (Throw is not null) throw Throw;
            return Result(count);
        }

        public async IAsyncEnumerable<ChatEvent> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new StreamCompleted();
        }

        public Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProfileInfo>>([]);
    }

    /// <summary>Serves stored rows per session id; a "full" session always returns full pages (page-cap case).</summary>
    private sealed class Rest(Func<string, int, int, string> rows) : HttpMessageHandler
    {
        public readonly List<string> Paths = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Paths.Add(r.RequestUri!.PathAndQuery);
            var segs = r.RequestUri.AbsolutePath.Split('/');
            var id = segs[^2];
            var q = System.Web.HttpUtility.ParseQueryString(r.RequestUri.Query);
            var json = rows(id, int.Parse(q["limit"]!), int.Parse(q["offset"] ?? "0"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private static string RowsJson(params (long Id, string Role, string Text)[] rows) =>
        "{\"session_id\":\"x\",\"messages\":[" + string.Join(",",
            rows.Select(r => $"{{\"id\":{r.Id},\"role\":\"{r.Role}\",\"content\":\"{r.Text}\"}}")) + "]}";

    // u1(1) a1(2) tool(3) a1b(4) | u2(5) a2(6)
    private static readonly (long, string, string)[] Original =
    [
        (1, "user", "q1"), (2, "assistant", "step"), (3, "tool", "out"), (4, "assistant", "answer one"),
        (5, "user", "q2"), (6, "assistant", "answer two")
    ];

    private sealed record Fixture(MainViewModel Vm, BranchScript Transport, ConversationVM Convo, Rest Rest)
    {
        public MessageVM Row(long id) => Convo.History.First(m => m.RowId == id);
    }

    private static Fixture Make(Func<string, int, int, string>? rows = null, bool stored = true)
    {
        var transport = new BranchScript();
        var rest = new Rest(rows ?? ((id, _, _) => id == "orig"
            ? RowsJson(Original)
            : RowsJson((101, "user", "q1"), (102, "assistant", "copied answer"))));
        var api = new SessionsApi(new DashboardAuth(new Uri("https://gw.example/"), rest));
        var selection = new ProfileSelectionService(new MemPrefs());
        selection.SetSelectedAsync(new[] { new ProfileKey("demo", "default") }).GetAwaiter().GetResult();
        var gate = new NotificationGate(selection);
        var catalog = new SessionCatalog(gate, () => api);
        var vm = new MainViewModel(selection, gate, () => transport, () => "test",
            () => null, () => null, () => catalog, () => null, () => null, () => null, () => null, () => null);
        vm.NewChat();
        var convo = vm.SelectedConversation!;
        convo.SessionId = "rt1";
        if (stored) convo.StoredSessionId = "orig";
        foreach (var (id, role, text) in Original)
        {
            if (role == "tool") continue;
            var m = new MessageVM(role == "user" ? "You" : "Assistant", text) { RowId = id };
            convo.History.Add(m);
            vm.Messages.Add(m);
        }
        vm.SelectedConversation = null;                   // re-select so the command state refreshes
        vm.SelectedConversation = convo;
        return new Fixture(vm, transport, convo, rest);
    }

    private static void Settle() => Pump(TimeSpan.FromMilliseconds(250));

    [Fact]
    public void Branch_from_a_user_message_counts_stored_rows_and_opens_the_copy()
    {
        RunSta(() =>
        {
            var f = Make();
            f.Vm.BranchFromHereCommand!.Execute(f.Row(5));       // q2: u1 a1 a1b u2 = 4 counted rows
            Settle();

            var call = Assert.Single(f.Transport.Calls);
            Assert.Equal(4, call.Count);
            Assert.Equal("rt1", call.Runtime);
            Assert.Equal(new ProfileKey("demo", "default"), call.Profile);
            Assert.NotSame(f.Convo, f.Vm.SelectedConversation);
            var opened = f.Vm.SelectedConversation!;
            Assert.Equal("branch-1", opened.StoredSessionId);
            Assert.Equal("Branch of chat", opened.Title);
            Assert.Equal(new[] { "q1", "copied answer" }, f.Vm.Messages.Select(m => m.Text));   // from the gateway's rows
            Assert.Contains("Branched", f.Vm.RewindNotice);
            Assert.Equal(5, f.Convo.History.Count);                                              // original untouched
        });
    }

    [Fact]
    public void Branch_from_a_reply_keeps_all_its_steps_and_the_whole_chat_sends_no_count()
    {
        RunSta(() =>
        {
            var f = Make();
            f.Vm.BranchFromHereCommand!.Execute(f.Row(4));       // reply: u1 a1 a1b = 3 (tool row not counted)
            Settle();
            Assert.Equal(3, f.Transport.Calls[0].Count);

            f.Vm.SelectedConversation = f.Convo;
            f.Transport.Result = _ => new BranchResult("branch-2", null);
            f.Vm.BranchChatCommand!.Execute(null);
            Settle();
            Assert.Equal(2, f.Transport.Calls.Count);
            Assert.Null(f.Transport.Calls[1].Count);
            Assert.Equal("branch-2", f.Vm.SelectedConversation!.StoredSessionId);
            Assert.EndsWith("(branch)", f.Vm.SelectedConversation.Title);   // untitled branch gets a readable name
        });
    }

    [Fact]
    public void Only_chats_with_a_stored_session_and_messages_with_a_stored_row_can_branch()
    {
        RunSta(() =>
        {
            var f = Make(stored: false);
            Assert.False(f.Vm.BranchChatCommand!.CanExecute(null));
            f.Vm.BranchChatCommand.Execute(null);
            Settle();
            Assert.Contains("Nothing to branch", f.Vm.RewindNotice);
            Assert.Empty(f.Transport.Calls);

            var g = Make();
            Assert.True(g.Vm.BranchChatCommand!.CanExecute(null));
            Assert.False(g.Vm.BranchFromHereCommand!.CanExecute(new MessageVM("You", "no row")));
            Assert.False(g.Vm.BranchFromHereCommand.CanExecute(null));
            Assert.True(g.Vm.BranchFromHereCommand.CanExecute(g.Row(2)));    // assistant rows can be branched from
        });
    }

    [Fact]
    public void A_row_the_gateway_no_longer_has_is_refused_without_calling_branch()
    {
        RunSta(() =>
        {
            var f = Make();
            var ghost = new MessageVM("You", "ghost") { RowId = 999 };
            f.Convo.History.Add(ghost);
            f.Vm.BranchFromHereCommand!.Execute(ghost);
            Settle();
            Assert.Empty(f.Transport.Calls);
            Assert.Contains("can't be branched", f.Vm.RewindNotice);
            Assert.Same(f.Convo, f.Vm.SelectedConversation);
        });
    }

    [Fact]
    public void A_chat_too_long_to_read_completely_refuses_instead_of_guessing_a_count()
    {
        RunSta(() =>
        {
            var f = Make((id, limit, _) => RowsJson(Enumerable.Range(1, limit).Select(i => ((long)i, "user", "x")).ToArray()));
            f.Vm.BranchFromHereCommand!.Execute(f.Row(5));
            Settle();
            Assert.Empty(f.Transport.Calls);
            Assert.Contains("too long", f.Vm.RewindNotice);
        });
    }

    [Fact]
    public void Gateway_errors_are_shown_and_no_chat_is_opened()
    {
        RunSta(() =>
        {
            var f = Make();
            f.Transport.Throw = new RpcException(4009, "busy");
            f.Vm.BranchChatCommand!.Execute(null);
            Settle();
            Assert.Contains("Wait for the reply", f.Vm.RewindNotice);
            Assert.Same(f.Convo, f.Vm.SelectedConversation);

            f.Transport.Throw = new RpcException(5000, "kaput");
            f.Vm.BranchChatCommand.Execute(null);
            Settle();
            Assert.Contains("kaput", f.Vm.RewindNotice);
            Assert.Same(f.Convo, f.Vm.SelectedConversation);
            Assert.Equal(2, f.Transport.Calls.Count);             // each press is one explicit request; nothing auto-retried
        });
    }

    [Fact]
    public void A_second_press_while_branching_is_ignored_and_a_chat_switch_does_not_steal_focus()
    {
        RunSta(() =>
        {
            var f = Make();
            f.Transport.Hold = new TaskCompletionSource();
            f.Vm.BranchChatCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(100));
            Assert.False(f.Vm.BranchChatCommand.CanExecute(null));
            f.Vm.BranchChatCommand.Execute(null);                 // double press
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.Single(f.Transport.Calls);

            f.Vm.NewChat();                                       // user moves on while the gateway copies
            var elsewhere = f.Vm.SelectedConversation!;
            f.Transport.Hold.SetResult();
            Settle();

            Assert.Same(elsewhere, f.Vm.SelectedConversation);    // focus not stolen
            Assert.Equal("", f.Vm.RewindNotice);
            f.Vm.SelectedConversation = f.Convo;
            Assert.Equal(5, f.Vm.Messages.Count);                 // origin untouched
        });
    }

    [Fact]
    public void Branching_is_disabled_while_a_turn_is_running()
    {
        RunSta(() =>
        {
            var f = Make();
            var gate = new TaskCompletionSource();
            f.Vm.InputText = "next";
            // A running turn: use the real send path with a held stream.
            var hold = new HoldTransport(gate);
            var selection = new ProfileSelectionService(new MemPrefs());
            selection.SetSelectedAsync(new[] { new ProfileKey("demo", "default") }).GetAwaiter().GetResult();
            var vm = new MainViewModel(selection, new NotificationGate(selection), () => hold, () => "t",
                () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
            vm.NewChat();
            vm.SelectedConversation!.StoredSessionId = "orig";
            vm.InputText = "go";
            vm.SendCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(100));
            Assert.False(vm.BranchChatCommand!.CanExecute(null));
            gate.SetResult();
            vm.CancelCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(150));
        });
    }

    private sealed class HoldTransport(TaskCompletionSource gate) : IGatewayTransport, IBranchingTransport
    {
        public Task<BranchResult> BranchSessionAsync(ProfileKey p, string r, int? c, CancellationToken ct) =>
            throw new InvalidOperationException("must not be called while a turn runs");

        public async IAsyncEnumerable<ChatEvent> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await gate.Task.WaitAsync(ct);
            yield return new StreamCompleted();
        }

        public Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProfileInfo>>([]);
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

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
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
