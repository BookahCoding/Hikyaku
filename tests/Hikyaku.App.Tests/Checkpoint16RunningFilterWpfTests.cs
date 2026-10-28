using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 16d — the Running filter through the real MainViewModel: toggle → poll session.active_list → rows matched only
/// against the listed chats → labels, honest notes, nudges, and a clean stop. Scripted peer only; no live gateway.
/// </summary>
[Collection("StaMath")]
public class Checkpoint16RunningFilterWpfTests
{
    private sealed class Peer : IJsonRpcPeer, IGatewayEventSource
    {
        public event Action<GatewayEvent>? EventReceived;
        public int Asks;
        public Func<JsonNode?> Answer = () => Active(("s1", "working"), ("s2", "waiting"), ("s3", "starting"), ("s4", "idle"));
        public Exception? Throw;
        public TaskCompletionSource? Hold;

        public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            Assert.Equal("session.active_list", method);
            Interlocked.Increment(ref Asks);
            if (Hold is not null) await Hold.Task.WaitAsync(ct);
            if (Throw is not null) throw Throw;
            return Answer();
        }

        public void Raise(string type) => EventReceived?.Invoke(new GatewayEvent(type, null, null, null));
    }

    private static JsonNode Active(params (string Key, string Status)[] rows) => new JsonObject
    {
        ["sessions"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
        { ["session_key"] = r.Key, ["id"] = "rt-" + r.Key, ["status"] = r.Status, ["title"] = r.Key }).ToArray())
    };

    private static (MainViewModel Vm, Peer Peer) Make(Peer? peer = null, bool connected = true)
    {
        peer ??= new Peer();
        var selection = new ProfileSelectionService(new MemPrefs());
        selection.SetSelectedAsync(new[] { new ProfileKey("demo", "default") }).GetAwaiter().GetResult();
        var vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
            () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null,
            rpc: () => connected ? peer : null)
        {
            LivePollInterval = TimeSpan.FromMilliseconds(40),
            LiveSettleDelay = TimeSpan.FromMilliseconds(10)
        };
        foreach (var id in new[] { "s1", "s2", "s3", "s4" })
            vm.SessionRows.Add(new SessionRowVM(new SessionSummary(id, "Chat " + id)));
        return (vm, peer);
    }

    private static IEnumerable<string> Visible(MainViewModel vm) => vm.VisibleSessionRows.Select(r => r.Summary.Id);

    [Fact]
    public void Turning_the_filter_on_shows_only_running_chats_with_text_labels()
    {
        RunSta(() =>
        {
            var (vm, peer) = Make();
            Assert.Equal(new[] { "s1", "s2", "s3", "s4" }, Visible(vm));          // off: everything
            vm.ToggleRunningFilterCommand!.Execute(null);
            Pump(250);

            Assert.Equal(new[] { "s1", "s2" }, Visible(vm));                      // working + waiting; starting is not a turn
            Assert.Equal("Running", vm.SessionRows[0].LiveLabel);
            Assert.Equal("Waiting for you", vm.SessionRows[1].LiveLabel);
            Assert.Equal("", vm.SessionRows[2].LiveLabel);
            Assert.Equal("Running (2)", vm.RunningFilterLabel);
            Assert.Equal("", vm.LiveNote);
            vm.ToggleRunningFilterCommand.Execute(null);
        });
    }

    [Fact]
    public void Statuses_for_chats_that_are_not_listed_are_ignored()
    {
        RunSta(() =>
        {
            var peer = new Peer { Answer = () => Active(("other-profile-chat", "working"), ("s1", "working")) };
            var (vm, _) = Make(peer);
            vm.ToggleRunningFilterCommand!.Execute(null);
            Pump(250);
            Assert.Equal(new[] { "s1" }, Visible(vm));
            Assert.Equal("Running (1)", vm.RunningFilterLabel);                   // the foreign row is not counted
            vm.ToggleRunningFilterCommand.Execute(null);
        });
    }

    [Fact]
    public void Until_the_gateway_answers_nothing_is_hidden_and_the_note_says_so()
    {
        RunSta(() =>
        {
            var peer = new Peer { Hold = new TaskCompletionSource() };
            var (vm, _) = Make(peer);
            vm.ToggleRunningFilterCommand!.Execute(null);
            Pump(120);
            Assert.Equal(4, vm.VisibleSessionRows.Count);
            Assert.Contains("Checking", vm.LiveNote);
            Assert.Equal("Running", vm.RunningFilterLabel);                       // no count before an answer
            vm.ToggleRunningFilterCommand.Execute(null);
            peer.Hold.SetResult();
        });
    }

    [Fact]
    public void Unsupported_gateways_and_errors_never_hide_chats()
    {
        RunSta(() =>
        {
            var (vm, peer) = Make(new Peer { Throw = new RpcException(-32601, "nf") });
            vm.ToggleRunningFilterCommand!.Execute(null);
            Pump(200);
            Assert.Equal(4, vm.VisibleSessionRows.Count);
            Assert.Contains("doesn't report running chats", vm.LiveNote);
            Assert.Equal(1, peer.Asks);                                           // never asked again
            vm.ToggleRunningFilterCommand.Execute(null);

            var (vm2, _) = Make(new Peer { Throw = new HttpRequestException("down") });
            vm2.ToggleRunningFilterCommand!.Execute(null);
            Pump(200);
            Assert.Equal(4, vm2.VisibleSessionRows.Count);
            Assert.Contains("Couldn't read", vm2.LiveNote);
            vm2.ToggleRunningFilterCommand.Execute(null);
        });
    }

    [Fact]
    public void A_method_that_disappears_after_an_answer_restores_every_chat_and_drops_the_stale_labels()
    {
        RunSta(() =>
        {
            var peer = new Peer();
            var (vm, _) = Make(peer);
            vm.LivePollInterval = TimeSpan.FromSeconds(30);
            vm.ToggleRunningFilterCommand!.Execute(null);
            Pump(150);
            Assert.Equal(new[] { "s1", "s2" }, Visible(vm));

            peer.Throw = new RpcException(-32601, "method not found");              // e.g. the gateway was downgraded
            peer.Raise("sessions.changed");
            Pump(250);
            Assert.Equal(4, vm.VisibleSessionRows.Count);                           // never hide chats on a stale answer
            Assert.All(vm.SessionRows, r => Assert.Equal("", r.LiveLabel));
            Assert.Contains("doesn't report running chats", vm.LiveNote);
            vm.ToggleRunningFilterCommand.Execute(null);
        });
    }

    [Fact]
    public void Not_connected_keeps_every_chat_visible_and_retries()
    {
        RunSta(() =>
        {
            var (vm, peer) = Make(connected: false);
            vm.ToggleRunningFilterCommand!.Execute(null);
            Pump(200);
            Assert.Equal(4, vm.VisibleSessionRows.Count);
            Assert.Contains("Couldn't read", vm.LiveNote);
            Assert.Equal(0, peer.Asks);
            vm.ToggleRunningFilterCommand.Execute(null);
        });
    }

    [Fact]
    public void A_gateway_event_refreshes_status_without_waiting_for_the_poll()
    {
        RunSta(() =>
        {
            var peer = new Peer();
            var (vm, _) = Make(peer);
            vm.LivePollInterval = TimeSpan.FromSeconds(30);                       // only a nudge can refresh in time
            vm.ToggleRunningFilterCommand!.Execute(null);
            Pump(150);
            Assert.Equal(new[] { "s1", "s2" }, Visible(vm));

            peer.Answer = () => Active(("s1", "idle"), ("s2", "waiting"));
            peer.Raise("message.complete");                                       // another client's turn ended
            Pump(250);
            Assert.Equal(new[] { "s2" }, Visible(vm));
            vm.ToggleRunningFilterCommand.Execute(null);
        });
    }

    [Fact]
    public void A_replaced_session_list_is_matched_again_and_zero_running_says_so()
    {
        RunSta(() =>
        {
            var (vm, _) = Make();
            vm.ToggleRunningFilterCommand!.Execute(null);
            Pump(150);
            vm.SessionRows.Clear();                                               // e.g. another profile's chats
            vm.SessionRows.Add(new SessionRowVM(new SessionSummary("x1", "Other")));
            Assert.Empty(vm.VisibleSessionRows);
            Assert.Contains("No listed chat is running", vm.LiveNote);
            Assert.Equal("", vm.SessionRows[0].LiveLabel);
            vm.ToggleRunningFilterCommand.Execute(null);
        });
    }

    [Fact]
    public void Turning_the_filter_off_restores_every_chat_clears_labels_and_stops_polling()
    {
        RunSta(() =>
        {
            var (vm, peer) = Make();
            vm.ToggleRunningFilterCommand!.Execute(null);
            Pump(150);
            vm.ToggleRunningFilterCommand.Execute(null);
            Assert.False(vm.RunningFilterOn);
            Assert.Equal(new[] { "s1", "s2", "s3", "s4" }, Visible(vm));
            Assert.All(vm.SessionRows, r => Assert.Equal("", r.LiveLabel));
            Assert.Equal("", vm.LiveNote);
            var asks = peer.Asks;
            Pump(250);
            Assert.Equal(asks, peer.Asks);                                        // no background polling when off
            peer.Raise("message.start");                                          // events no longer matter either
            Pump(100);
            Assert.Equal(asks, peer.Asks);
        });
    }

    [Fact]
    public void Turning_it_on_again_starts_fresh_with_no_old_rows()
    {
        RunSta(() =>
        {
            var peer = new Peer();
            var (vm, _) = Make(peer);
            vm.ToggleRunningFilterCommand!.Execute(null);
            Pump(150);
            vm.ToggleRunningFilterCommand.Execute(null);
            peer.Hold = new TaskCompletionSource();
            vm.ToggleRunningFilterCommand.Execute(null);                          // on again, gateway slow
            Pump(100);
            Assert.Equal(4, vm.VisibleSessionRows.Count);                         // old "running" answer is gone
            Assert.Contains("Checking", vm.LiveNote);
            vm.ToggleRunningFilterCommand.Execute(null);
            peer.Hold.SetResult();
        });
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
