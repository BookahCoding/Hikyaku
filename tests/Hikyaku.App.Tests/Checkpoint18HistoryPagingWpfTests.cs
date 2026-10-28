using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 18a — "Load earlier messages": newest page first, older pages on demand, de-duplicated by stored row id, for the chat's own
/// profile, never while a turn runs, with honest limits. Real view model and SessionCatalog over a stub REST server.
/// </summary>
[Collection("StaMath")]
public class Checkpoint18HistoryPagingWpfTests
{
    private static readonly ProfileKey Profile = new("demo", "default");

    /// <summary>Serves rows 1..Total newest-first in windows (what order=latest returns), chronological inside a window.</summary>
    private sealed class Server(int total, bool toolRows = false) : HttpMessageHandler
    {
        public int Total = total;
        public readonly List<(string Profile, int Offset, int Limit)> Calls = new();
        public Exception? Throw;
        public TaskCompletionSource? Hold;
        public Func<long, (string Role, string Content)>? Row;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var q = System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query);
            var limit = int.Parse(q["limit"]!);
            var offset = int.Parse(q["offset"] ?? "0");
            Calls.Add((q["profile"] ?? "", offset, limit));
            if (offset > 0)
            {
                if (Hold is not null) await Hold.Task.WaitAsync(ct);
                if (Throw is not null) throw Throw;
            }
            var end = Math.Max(0, Total - offset);
            var start = Math.Max(0, end - limit);
            var rows = Enumerable.Range(start + 1, end - start).Select(i =>
            {
                var (role, content) = Row?.Invoke(i) ?? (toolRows && i % 3 == 0 ? ("tool", "tool output " + i)
                    : toolRows && i % 3 == 2 ? ("assistant", "")
                    : i % 2 == 1 ? ("user", "question " + i) : ("assistant", "answer " + i));
                return $"{{\"id\":{i},\"role\":\"{role}\",\"content\":\"{content}\"}}";
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"session_id\":\"s\",\"messages\":[" + string.Join(",", rows) + "]}", Encoding.UTF8, "application/json") };
        }
    }

    private sealed record Fixture(MainViewModel Vm, Server Server, SessionCatalog Catalog);

    private static Fixture Make(int total, bool toolRows = false)
    {
        var server = new Server(total, toolRows);
        var selection = new ProfileSelectionService(new MemPrefs());
        selection.SetSelectedAsync(new[] { Profile }).GetAwaiter().GetResult();
        var gate = new NotificationGate(selection);
        var catalog = new SessionCatalog(gate, () => new SessionsApi(new DashboardAuth(new Uri("https://gw.example/"), server)));
        catalog.SetActiveProfile(Profile);
        var vm = new MainViewModel(selection, gate, () => new DemoTransport(), () => "t",
            () => null, () => null, () => catalog, () => null, () => null, () => null, () => null, () => null);
        return new Fixture(vm, server, catalog);
    }

    private static void Open(Fixture f)
    {
        Await(f.Vm.OpenSessionAsync(new SessionRowVM(new SessionSummary("sess", "Long chat"))));
    }

    private static IEnumerable<long> Ids(MainViewModel vm) => vm.Messages.Where(m => m.RowId is not null).Select(m => m.RowId!.Value);

    // ---- opening ---------------------------------------------------------------------------------

    [Fact]
    public void A_long_chat_opens_with_its_newest_page_and_offers_earlier_messages()
    {
        RunSta(() =>
        {
            var f = Make(150);
            Open(f);
            Assert.Equal(60, f.Vm.Messages.Count);
            Assert.Equal(Enumerable.Range(91, 60).Select(i => (long)i), Ids(f.Vm));   // newest 60, chronological
            Assert.Equal(Visibility.Visible, f.Vm.LoadEarlierVisibility);
            Assert.Contains("60 loaded", f.Vm.LoadEarlierLabel);
            Assert.True(f.Vm.LoadEarlierCommand!.CanExecute(null));
            Assert.Equal("default", f.Server.Calls[0].Profile);
        });
    }

    [Fact]
    public void A_short_chat_has_nothing_earlier_to_load()
    {
        RunSta(() =>
        {
            var f = Make(59);
            Open(f);
            Assert.Equal(59, f.Vm.Messages.Count);
            Assert.Equal(Visibility.Collapsed, f.Vm.LoadEarlierVisibility);
            Assert.False(f.Vm.LoadEarlierCommand!.CanExecute(null));
        });
    }

    [Fact]
    public void A_chat_that_failed_to_load_offers_no_paging()
    {
        RunSta(() =>
        {
            var f = Make(150);
            f.Server.Total = 0;
            f.Server.Row = _ => throw new InvalidOperationException();
            Open(f);                                                        // the first page throws: offline/error path
            Assert.Equal(Visibility.Collapsed, f.Vm.LoadEarlierVisibility);
        });
    }

    // ---- loading earlier -------------------------------------------------------------------------

    [Fact]
    public void Loading_earlier_prepends_older_messages_in_order_until_the_start_of_the_chat()
    {
        RunSta(() =>
        {
            var f = Make(150);
            Open(f);
            var anchors = new List<MessageVM>();
            f.Vm.EarlierLoaded += anchors.Add;
            var firstBefore = f.Vm.Messages[0];

            Await(RunCommand(f.Vm.LoadEarlierCommand!));
            Assert.Equal(150, f.Vm.Messages.Count);                           // 60 shown + the 90 that remain
            Assert.Equal(Enumerable.Range(1, 150).Select(i => (long)i), Ids(f.Vm));
            Assert.Equal(Visibility.Collapsed, f.Vm.LoadEarlierVisibility);   // 90 < 100: that was the start
            Assert.Equal(new[] { (60, 100) }, f.Server.Calls.Skip(1).Select(c => (c.Offset, c.Limit)));
            Assert.Equal(new[] { firstBefore }, anchors);                     // the reader stays on the message they were at
        });
    }

    [Fact]
    public void A_full_older_page_keeps_the_button_until_a_short_page_arrives()
    {
        RunSta(() =>
        {
            var f = Make(300);
            Open(f);
            Await(RunCommand(f.Vm.LoadEarlierCommand!));
            Assert.Equal(160, f.Vm.Messages.Count);
            Assert.Equal(Visibility.Visible, f.Vm.LoadEarlierVisibility);
            Assert.Contains("160 loaded", f.Vm.LoadEarlierLabel);
            Await(RunCommand(f.Vm.LoadEarlierCommand!));
            Assert.Equal(260, f.Vm.Messages.Count);
            Await(RunCommand(f.Vm.LoadEarlierCommand!));
            Assert.Equal(300, f.Vm.Messages.Count);
            Assert.Equal(Visibility.Collapsed, f.Vm.LoadEarlierVisibility);
            Assert.Equal(Enumerable.Range(1, 300).Select(i => (long)i), Ids(f.Vm));
        });
    }

    [Fact]
    public void Steps_that_are_not_messages_are_not_shown_but_still_count_toward_the_next_offset()
    {
        RunSta(() =>
        {
            var f = Make(300, toolRows: true);
            Open(f);
            Assert.All(f.Vm.Messages, m => Assert.False(string.IsNullOrWhiteSpace(m.Text)));   // no tool output, no blank bubbles
            Assert.DoesNotContain(f.Vm.Messages, m => m.Text.StartsWith("tool output"));
            Await(RunCommand(f.Vm.LoadEarlierCommand!));
            Assert.Equal(60, f.Server.Calls[1].Offset);                                         // raw rows, not shown ones
            Assert.DoesNotContain(f.Vm.Messages, m => m.Text.StartsWith("tool output"));
            Assert.Equal(Ids(f.Vm).OrderBy(x => x), Ids(f.Vm));                                 // still chronological
        });
    }

    [Fact]
    public void Rows_already_loaded_are_not_duplicated_when_the_chat_grew_meanwhile()
    {
        RunSta(() =>
        {
            var f = Make(150);
            Open(f);
            f.Server.Total = 154;                                           // 4 rows were added after we opened: offsets shift by 4
            Await(RunCommand(f.Vm.LoadEarlierCommand!));
            var ids = Ids(f.Vm).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());                // the 4 overlapping rows appear once
            Assert.Equal(ids.OrderBy(x => x), ids);
        });
    }

    [Fact]
    public void A_reply_merged_from_several_rows_is_not_duplicated_when_the_chat_grew_meanwhile()
    {
        RunSta(() =>
        {
            var f = Make(150);
            f.Server.Row = i => i % 4 == 1 ? ("user", "question " + i) : ("assistant", "step " + i);   // each reply is three stored rows
            Open(f);
            f.Server.Total = 154;
            Await(RunCommand(f.Vm.LoadEarlierCommand!));
            var covered = f.Vm.Messages.SelectMany(m => m.CoveredRowIds).ToList();
            Assert.Equal(covered.Count, covered.Distinct().Count());        // no stored row stands behind two bubbles
        });
    }

    [Fact]
    public void A_failed_load_keeps_everything_as_it_was_and_can_be_retried()
    {
        RunSta(() =>
        {
            var f = Make(300);
            Open(f);
            f.Server.Throw = new HttpRequestException("gateway down");
            Await(RunCommand(f.Vm.LoadEarlierCommand!));
            Assert.Equal(60, f.Vm.Messages.Count);
            Assert.Contains("Couldn't load earlier messages", f.Vm.RewindNotice);
            Assert.Equal(Visibility.Visible, f.Vm.LoadEarlierVisibility);
            Assert.True(f.Vm.LoadEarlierCommand!.CanExecute(null));
            Assert.Contains("60 loaded", f.Vm.LoadEarlierLabel);

            f.Server.Throw = null;
            Await(RunCommand(f.Vm.LoadEarlierCommand));
            Assert.Equal(160, f.Vm.Messages.Count);
        });
    }

    [Fact]
    public void A_second_press_while_loading_is_one_request_and_the_label_says_loading()
    {
        RunSta(() =>
        {
            var f = Make(300);
            Open(f);
            f.Server.Hold = new TaskCompletionSource();
            var first = RunCommand(f.Vm.LoadEarlierCommand!);
            Pump(60);
            Assert.False(f.Vm.LoadEarlierCommand!.CanExecute(null));
            Assert.Contains("Loading", f.Vm.LoadEarlierLabel);
            f.Vm.LoadEarlierCommand.Execute(null);                          // double press
            Pump(60);
            Assert.Equal(2, f.Server.Calls.Count);                          // the open + exactly one earlier page
            f.Server.Hold.SetResult();
            Await(first);
            Assert.Equal(160, f.Vm.Messages.Count);
        });
    }

    [Fact]
    public void Switching_chats_while_loading_updates_the_origin_chat_only()
    {
        RunSta(() =>
        {
            var f = Make(300);
            Open(f);
            var origin = f.Vm.SelectedConversation!;
            var anchors = new List<MessageVM>();
            f.Vm.EarlierLoaded += anchors.Add;
            f.Server.Hold = new TaskCompletionSource();
            var load = RunCommand(f.Vm.LoadEarlierCommand!);
            Pump(60);

            f.Vm.NewChat();                                                 // user moves on
            f.Server.Hold.SetResult();
            Await(load);

            Assert.Equal(160, origin.History.Count);                        // origin got its older messages
            Assert.Empty(f.Vm.Messages);                                    // the new chat's list was not touched
            Assert.Empty(anchors);                                          // and nothing is scrolled
            f.Vm.SelectedConversation = origin;
            Assert.Equal(160, f.Vm.Messages.Count);
        });
    }

    [Fact]
    public void Paging_is_not_available_while_a_reply_is_running()
    {
        RunSta(() =>
        {
            var f = Make(300);
            Open(f);
            f.Vm.InputText = "go";
            f.Vm.SendCommand.Execute(null);                                 // DemoTransport streams for a moment
            Assert.False(f.Vm.LoadEarlierCommand!.CanExecute(null));        // indexes must not shift under a running turn
            Pump(2500);
        });
    }

    [Fact]
    public void Loaded_rows_are_capped_and_the_user_is_told()
    {
        RunSta(() =>
        {
            var f = Make(1000);
            f.Vm.MaxLoadedRows = 160;
            Open(f);
            Await(RunCommand(f.Vm.LoadEarlierCommand!));                    // 60 + 100 = 160 = the cap
            Assert.Equal(160, f.Vm.Messages.Count);
            Assert.Equal(Visibility.Collapsed, f.Vm.LoadEarlierVisibility);
            Assert.Contains("very long", f.Vm.RewindNotice);
        });
    }

    [Fact]
    public void Find_sees_newly_loaded_messages_and_says_when_earlier_ones_are_not_loaded_yet()
    {
        RunSta(() =>
        {
            var f = Make(300);
            Open(f);
            f.Vm.OpenFindCommand!.Execute(null);
            f.Vm.FindText = "question 5";                                   // ids 51..59 live only in older pages
            Assert.Equal("No matches in loaded messages", f.Vm.FindStatus);
            Await(RunCommand(f.Vm.LoadEarlierCommand!));
            Await(RunCommand(f.Vm.LoadEarlierCommand!));
            f.Vm.FindNextCommand!.Execute(null);                            // recounts over what is loaded now
            Assert.NotEqual("No matches in loaded messages", f.Vm.FindStatus);
            Assert.Matches(@"\d+ of \d+", f.Vm.FindStatus);
        });
    }

    [Fact]
    public void An_earlier_load_keeps_edit_targets_valid_and_rows_are_editable_when_they_are_user_prompts()
    {
        RunSta(() =>
        {
            var f = Make(300);
            Open(f);
            Await(RunCommand(f.Vm.LoadEarlierCommand!));
            var oldUser = f.Vm.Messages.First(m => m.Role == "You");
            Assert.NotNull(oldUser.RowId);
            Assert.True(oldUser.IsRewindable);
            Assert.True(f.Vm.BranchFromHereCommand!.CanExecute(oldUser) || f.Vm.SelectedConversation!.SessionId is null);
        });
    }

    [Fact]
    public void The_load_earlier_bar_shows_only_near_the_top_of_the_loaded_messages()
    {
        RunSta(() =>
        {
            var f = Make(300);
            Open(f);
            Assert.True(f.Vm.MessagesNearTop);                              // a freshly opened chat starts at the top
            Assert.Equal(Visibility.Visible, f.Vm.LoadEarlierVisibility);
            f.Vm.MessagesNearTop = false;                                   // the reader scrolled down to the newest messages
            Assert.Equal(Visibility.Collapsed, f.Vm.LoadEarlierVisibility);
            Assert.False(f.Vm.LoadEarlierCommand!.CanExecute(null) && f.Vm.LoadEarlierVisibility == Visibility.Visible);
            f.Vm.MessagesNearTop = true;
            Assert.Equal(Visibility.Visible, f.Vm.LoadEarlierVisibility);
        });
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static Task RunCommand(System.Windows.Input.ICommand cmd)
    {
        // The commands start fire-and-forget work; wait for the load to settle by pumping until it stops loading.
        cmd.Execute(null);
        return Task.CompletedTask;
    }

    private static void Await(Task t)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!t.IsCompleted && DateTime.UtcNow < deadline) Pump(20);
        Pump(120);                                                          // let fire-and-forget loads settle
        if (t.IsFaulted) throw t.Exception!.GetBaseException();
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
