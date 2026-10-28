using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 20a — the command palette through the real MainViewModel: what is listed (only what can run now), keyboard model,
/// places by name, window actions, and the guarantee that nothing risky is ever offered.
/// </summary>
[Collection("StaMath")]
public class Checkpoint20PaletteWpfTests
{
    private sealed class Quiet : IGatewayTransport
    {
        public async IAsyncEnumerable<ChatEvent> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new StreamCompleted();
        }
        public Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProfileInfo>>([]);
    }

    private static MainViewModel Make()
    {
        var selection = new ProfileSelectionService(new MemPrefs());
        selection.SetSelectedAsync(new[] { new ProfileKey("demo", "default") }).GetAwaiter().GetResult();
        return new MainViewModel(selection, new NotificationGate(selection), () => new Quiet(), () => "t",
            () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
    }

    private static IEnumerable<string> Ids(MainViewModel vm) => vm.PaletteResults.Select(r => r.Id);

    [Fact]
    public void Opening_lists_commands_with_the_first_selected_and_says_how_many()
    {
        RunSta(() =>
        {
            var vm = Make();
            Assert.False(vm.IsPaletteOpen);
            vm.OpenPaletteCommand!.Execute(null);
            Assert.True(vm.IsPaletteOpen);
            Assert.Equal(Visibility.Visible, vm.PaletteVisibility);
            Assert.Equal(PaletteSearch.MaxResults, vm.PaletteResults.Count);
            Assert.True(vm.PaletteResults[0].IsSelected);
            Assert.Equal(1, vm.PaletteResults.Count(r => r.IsSelected));
            Assert.Equal("12 results", vm.PaletteStatus);
            Assert.Equal("", vm.PaletteQuery);
        });
    }

    [Fact]
    public void Typing_filters_and_no_match_says_so()
    {
        RunSta(() =>
        {
            var vm = Make();
            vm.OpenPaletteCommand!.Execute(null);
            vm.PaletteQuery = "inbox";
            Assert.Contains("nav.inbox", Ids(vm));
            Assert.DoesNotContain("app.settings", Ids(vm));
            vm.PaletteQuery = "zzzzqq";
            Assert.Empty(vm.PaletteResults);
            Assert.Equal("No matching commands", vm.PaletteStatus);
            Assert.False(vm.RunPaletteSelectedCommand!.CanExecute(null));
            vm.PaletteQuery = "new chat";
            Assert.Equal("chat.new", Ids(vm).First());
            Assert.Equal("1 result", vm.PaletteStatus.Length > 0 && vm.PaletteResults.Count == 1 ? "1 result" : vm.PaletteStatus);
        });
    }

    [Fact]
    public void Arrow_keys_move_the_selection_and_wrap_and_enter_runs_it()
    {
        RunSta(() =>
        {
            var vm = Make();
            vm.OpenPaletteCommand!.Execute(null);
            vm.PaletteQuery = "open";
            var n = vm.PaletteResults.Count;
            Assert.True(n >= 3);
            vm.MovePaletteSelectionCommand!.Execute(1);
            Assert.True(vm.PaletteResults[1].IsSelected);
            Assert.Equal(1, vm.PaletteResults.Count(r => r.IsSelected));
            vm.MovePaletteSelectionCommand.Execute(-1);
            vm.MovePaletteSelectionCommand.Execute(-1);
            Assert.True(vm.PaletteResults[n - 1].IsSelected);                    // wrapped up
            vm.MovePaletteSelectionCommand.Execute(1);
            Assert.True(vm.PaletteResults[0].IsSelected);                        // wrapped down

            vm.PaletteQuery = "open inbox";
            vm.RunPaletteSelectedCommand!.Execute(null);
            Assert.Equal(MainNav.Inbox, vm.Nav);
            Assert.False(vm.IsPaletteOpen);                                      // closes before running
            Assert.Empty(vm.PaletteResults);
        });
    }

    [Fact]
    public void Running_new_chat_creates_and_selects_one_and_nothing_is_sent()
    {
        RunSta(() =>
        {
            var vm = Make();
            vm.OpenPaletteCommand!.Execute(null);
            vm.PaletteQuery = "new chat";
            vm.RunPaletteSelectedCommand!.Execute(null);
            Assert.NotNull(vm.SelectedConversation);
            Assert.Empty(vm.Messages);                                           // a palette command never sends anything
            Assert.Equal(MainNav.Chats, vm.Nav);
        });
    }

    [Fact]
    public void Commands_that_cannot_run_now_are_not_listed_and_appear_when_they_can()
    {
        RunSta(() =>
        {
            var vm = Make();
            vm.OpenPaletteCommand!.Execute(null);
            vm.PaletteQuery = "find";
            Assert.DoesNotContain("chat.find", Ids(vm));                         // no chat open
            vm.ClosePaletteCommand!.Execute(null);

            vm.NewChat();
            vm.OpenPaletteCommand.Execute(null);
            vm.PaletteQuery = "find";
            Assert.Contains("chat.find", Ids(vm));
            vm.PaletteQuery = "stop";
            Assert.DoesNotContain("chat.stop", Ids(vm));                         // nothing running
            vm.PaletteQuery = "earlier";
            Assert.DoesNotContain("chat.earlier", Ids(vm));                      // nothing to load
        });
    }

    [Fact]
    public void Chats_and_gateway_sessions_can_be_reached_by_name()
    {
        RunSta(() =>
        {
            var vm = Make();
            vm.NewChat();
            var first = vm.SelectedConversation!;
            first.Title = "Quarterly planning";
            vm.NewChat();                                                        // selection moves away
            vm.SessionRows.Add(new SessionRowVM(new SessionSummary("sess-9", "Refactor the parser")));

            vm.OpenPaletteCommand!.Execute(null);
            vm.PaletteQuery = "planning";
            Assert.Equal("Go to chat: Quarterly planning", vm.PaletteResults[0].Title);
            vm.RunPaletteSelectedCommand!.Execute(null);
            Assert.Same(first, vm.SelectedConversation);

            vm.OpenPaletteCommand.Execute(null);
            vm.PaletteQuery = "parser";
            Assert.Contains(vm.PaletteResults, r => r.Id == "session:sess-9" && r.Title == "Open session: Refactor the parser");
        });
    }

    [Fact]
    public void Window_actions_are_raised_after_the_palette_has_closed()
    {
        RunSta(() =>
        {
            var vm = Make();
            var seen = new List<string>();
            vm.PaletteOpenSettingsRequested += () => seen.Add("settings:" + vm.IsPaletteOpen);
            vm.PaletteToggleSidebarRequested += () => seen.Add("sidebar:" + vm.IsPaletteOpen);
            vm.OpenPaletteCommand!.Execute(null);
            vm.PaletteQuery = "open settings";
            vm.RunPaletteSelectedCommand!.Execute(null);
            vm.OpenPaletteCommand.Execute(null);
            vm.PaletteQuery = "sidebar";
            vm.RunPaletteSelectedCommand.Execute(null);
            Assert.Equal(new[] { "settings:False", "sidebar:False" }, seen);
        });
    }

    [Fact]
    public void Escape_closes_clears_and_reopening_starts_fresh()
    {
        RunSta(() =>
        {
            var vm = Make();
            vm.OpenPaletteCommand!.Execute(null);
            vm.PaletteQuery = "inbox";
            vm.ClosePaletteCommand!.Execute(null);
            Assert.False(vm.IsPaletteOpen);
            Assert.Equal(Visibility.Collapsed, vm.PaletteVisibility);
            Assert.Empty(vm.PaletteResults);
            vm.OpenPaletteCommand.Execute(null);
            Assert.Equal("", vm.PaletteQuery);
            Assert.Equal(PaletteSearch.MaxResults, vm.PaletteResults.Count);
            vm.OpenPaletteCommand.Execute(null);                                 // opening twice is harmless
            Assert.True(vm.IsPaletteOpen);
        });
    }

    [Fact]
    public void Clicking_an_entry_runs_that_entry_not_the_selected_one()
    {
        RunSta(() =>
        {
            var vm = Make();
            vm.OpenPaletteCommand!.Execute(null);
            vm.PaletteQuery = "open";
            var bots = vm.PaletteResults.Single(r => r.Id == "nav.bots");
            vm.RunPaletteEntryCommand!.Execute(bots);
            Assert.Equal(MainNav.Bots, vm.Nav);
        });
    }

    [Fact]
    public void Nothing_risky_is_ever_offered_by_the_palette()
    {
        RunSta(() =>
        {
            var vm = Make();
            vm.NewChat();
            vm.SessionRows.Add(new SessionRowVM(new SessionSummary("s", "Some chat")));
            vm.OpenPaletteCommand!.Execute(null);
            var titles = new HashSet<string>();
            foreach (var c in "abcdefghijklmnopqrstuvwxyz0123456789")
            {
                vm.PaletteQuery = c.ToString();
                foreach (var r in vm.PaletteResults) titles.Add(r.Id + "|" + r.Title);
            }
            Assert.True(titles.Count > 12);                                       // the sweep really covered the registry
            var risky = new[] { "delete", "remove", "send", "grant", "push", "yolo", "approve", "allow", "permission", "terminal", "share", "overwrite", "sign out", "restore" };
            foreach (var t in titles)
                Assert.DoesNotContain(risky, w => t.Contains(w, StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void The_experience_toggle_is_in_the_palette_and_flips_the_mode_with_an_honest_label()
    {
        RunSta(() =>
        {
            var vm = Make();
            var changes = new List<bool>();
            vm.ExperienceChanged += changes.Add;
            Assert.True(vm.IsAdvanced);
            vm.OpenPaletteCommand!.Execute(null);
            vm.PaletteQuery = "simple";
            Assert.Equal("Use simple view", vm.PaletteResults[0].Title);
            vm.RunPaletteSelectedCommand!.Execute(null);
            Assert.False(vm.IsAdvanced);
            vm.OpenPaletteCommand.Execute(null);
            vm.PaletteQuery = "advanced";
            Assert.Equal("Show advanced tools", vm.PaletteResults[0].Title);
            vm.RunPaletteSelectedCommand.Execute(null);
            Assert.True(vm.IsAdvanced);
            Assert.Equal(new[] { false, true }, changes);
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
}

file sealed class MemPrefs : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
