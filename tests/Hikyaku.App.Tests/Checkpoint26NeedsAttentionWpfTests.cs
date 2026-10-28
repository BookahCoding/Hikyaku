using System.Text.Json.Nodes;
using System.Windows.Threading;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 26 — the "Needs you" list filter (Android's AttentionFilter.NeedsAttention: a chat waiting on an answer, or with a reply not
/// yet read). Real MainViewModel, scripted peer; no live gateway.
/// </summary>
[Collection("StaMath")]
public class Checkpoint26NeedsAttentionWpfTests
{
    private sealed class Peer : IJsonRpcPeer
    {
        public Func<JsonNode?> Answer = () => Active(("s1", "working"), ("s2", "waiting"), ("s3", "idle"), ("s4", "idle"));
        public Exception? Throw;

        public Task<JsonNode?> RequestAsync(string method, JsonObject? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            if (Throw is not null) throw Throw;
            return Task.FromResult(Answer());
        }
    }

    private static JsonNode Active(params (string Key, string Status)[] rows) => new JsonObject
    {
        ["sessions"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
        { ["session_key"] = r.Key, ["id"] = "rt-" + r.Key, ["status"] = r.Status, ["title"] = r.Key }).ToArray())
    };

    // s1 running, s2 waiting, s3 idle but unread, s4 idle and read.
    private static (MainViewModel Vm, Peer Peer) Make(Peer? peer = null)
    {
        peer ??= new Peer();
        var selection = new ProfileSelectionService(new MemPrefs26());
        selection.SetSelectedAsync(new[] { new ProfileKey("demo", "default") }).GetAwaiter().GetResult();
        var vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
            () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null, rpc: () => peer)
        {
            LivePollInterval = TimeSpan.FromMilliseconds(40),
            LiveSettleDelay = TimeSpan.FromMilliseconds(10)
        };
        vm.SessionRows.Add(new SessionRowVM(new SessionSummary("s1", "Chat s1")));
        vm.SessionRows.Add(new SessionRowVM(new SessionSummary("s2", "Chat s2")));
        vm.SessionRows.Add(new SessionRowVM(new SessionSummary("s3", "Chat s3", Unread: true)));
        vm.SessionRows.Add(new SessionRowVM(new SessionSummary("s4", "Chat s4")));
        return (vm, peer);
    }

    private static IEnumerable<string> Visible(MainViewModel vm) => vm.VisibleSessionRows.Select(r => r.Summary.Id);

    [Fact]
    public void Needs_you_lists_waiting_and_unread_chats_with_words_and_leaves_running_and_idle_out()
    {
        RunSta(() =>
        {
            var (vm, _) = Make();
            vm.SetAttentionFilterCommand!.Execute("NeedsYou");
            Pump(250);
            Assert.Equal(new[] { "s2", "s3" }, Visible(vm));
            Assert.Equal("Waiting for you", vm.SessionRows[1].LiveLabel);
            Assert.Equal("New reply", vm.SessionRows[2].LiveLabel);          // text, not only presence in the list
            Assert.Equal("Needs you (2)", vm.NeedsYouFilterLabel);
            Assert.Equal("", vm.LiveNote);
            Assert.Equal(AttentionFilter.NeedsYou, vm.AttentionMode);
            vm.SetAttentionFilterCommand.Execute("NeedsYou");                // choosing it again turns it off
        });
    }

    [Fact]
    public void The_running_chip_still_shows_only_running_and_waiting_not_unread()
    {
        RunSta(() =>
        {
            var (vm, _) = Make();
            vm.ToggleRunningFilterCommand!.Execute(null);
            Pump(250);
            Assert.Equal(new[] { "s1", "s2" }, Visible(vm));
            Assert.Equal("", vm.SessionRows[2].LiveLabel);                   // "New reply" belongs to the Needs-you view only
            Assert.Equal("Needs you", vm.NeedsYouFilterLabel);
            vm.ToggleRunningFilterCommand.Execute(null);
        });
    }

    [Fact]
    public void Switching_between_the_two_filters_keeps_one_poll_and_turning_off_shows_everything()
    {
        RunSta(() =>
        {
            var (vm, _) = Make();
            vm.ToggleRunningFilterCommand!.Execute(null);
            Pump(150);
            vm.SetAttentionFilterCommand!.Execute("NeedsYou");
            Pump(100);
            Assert.Equal(new[] { "s2", "s3" }, Visible(vm));
            Assert.False(vm.RunningFilterOn);
            vm.SetAttentionFilterCommand.Execute("NeedsYou");
            Assert.Equal(new[] { "s1", "s2", "s3", "s4" }, Visible(vm));
            Assert.Equal(AttentionFilter.All, vm.AttentionMode);
        });
    }

    [Fact]
    public void Without_a_live_answer_unread_chats_still_show_and_the_note_says_what_is_missing()
    {
        RunSta(() =>
        {
            var (vm, peer) = Make(new Peer { Throw = new InvalidOperationException("down") });
            vm.SetAttentionFilterCommand!.Execute("NeedsYou");
            Pump(250);
            Assert.Equal(new[] { "s3" }, Visible(vm));                       // unread is known locally; waiting is not
            Assert.Contains("unread reply only", vm.LiveNote);
            vm.SetAttentionFilterCommand.Execute("NeedsYou");
        });
    }

    [Fact]
    public void Nothing_waiting_says_so()
    {
        RunSta(() =>
        {
            var peer = new Peer { Answer = () => Active(("s1", "working")) };
            var (vm, _) = Make(peer);
            vm.SessionRows.Clear();
            vm.SessionRows.Add(new SessionRowVM(new SessionSummary("s1", "Chat s1")));
            vm.SetAttentionFilterCommand!.Execute("NeedsYou");
            Pump(250);
            Assert.Empty(Visible(vm));
            Assert.Equal("Nothing needs you.", vm.LiveNote);
            vm.SetAttentionFilterCommand.Execute("NeedsYou");
        });
    }

    [Fact]
    public void An_unknown_filter_value_changes_nothing()
    {
        RunSta(() =>
        {
            var (vm, _) = Make();
            vm.SetAttentionFilterCommand!.Execute("bogus");
            vm.SetAttentionFilterCommand.Execute(null);
            Assert.Equal(AttentionFilter.All, vm.AttentionMode);
            Assert.Equal(4, vm.VisibleSessionRows.Count);
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

file sealed class MemPrefs26 : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
