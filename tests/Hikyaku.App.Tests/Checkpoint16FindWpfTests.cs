using System.Windows;
using System.Windows.Threading;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 16c — Find in chat through the real MainViewModel (matches, navigation, scope, lifecycle).</summary>
[Collection("StaMath")]
public class Checkpoint16FindWpfTests
{
    private static (MainViewModel Vm, ConversationVM Convo, List<MessageVM> Msgs) Make()
    {
        var selection = new ProfileSelectionService(new MemPrefs());
        selection.SetSelectedAsync(new[] { new ProfileKey("demo", "default") }).GetAwaiter().GetResult();
        var vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
            () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
        vm.NewChat();
        var convo = vm.SelectedConversation!;
        var msgs = new List<MessageVM>();
        foreach (var (role, text) in new[] { ("You", "Find the needle please"), ("Assistant", "No match here"),
                     ("You", "another NEEDLE"), ("Assistant", "needle again, third") })
        {
            var m = new MessageVM(role, text);
            msgs.Add(m); convo.History.Add(m); vm.Messages.Add(m);
        }
        return (vm, convo, msgs);
    }

    [Fact]
    public void Typing_marks_matches_and_the_first_is_current_and_requested_into_view()
    {
        RunSta(() =>
        {
            var (vm, _, m) = Make();
            var scrolled = new List<MessageVM>();
            vm.FindScrollRequested += scrolled.Add;
            vm.OpenFindCommand!.Execute(null);
            Assert.True(vm.IsFindOpen);
            vm.FindText = "needle";

            Assert.Equal("1 of 3", vm.FindStatus);
            Assert.True(m[0].IsFindCurrent);
            Assert.True(m[2].IsFindMatch && !m[2].IsFindCurrent);
            Assert.False(m[1].IsFindMatch);
            Assert.Equal(new[] { m[0] }, scrolled);
        });
    }

    [Fact]
    public void Next_and_previous_move_the_current_match_and_wrap()
    {
        RunSta(() =>
        {
            var (vm, _, m) = Make();
            vm.OpenFindCommand!.Execute(null);
            vm.FindText = "needle";
            vm.FindNextCommand!.Execute(null);
            Assert.Equal("2 of 3", vm.FindStatus);
            Assert.True(m[2].IsFindCurrent);
            Assert.False(m[0].IsFindCurrent);
            vm.FindNextCommand.Execute(null);
            vm.FindNextCommand.Execute(null);
            Assert.Equal("1 of 3", vm.FindStatus);                   // wrapped
            vm.FindPreviousCommand!.Execute(null);
            Assert.Equal("3 of 3", vm.FindStatus);
            Assert.True(m[3].IsFindCurrent);
        });
    }

    [Fact]
    public void No_match_blank_query_and_closing_clear_every_mark()
    {
        RunSta(() =>
        {
            var (vm, _, m) = Make();
            vm.OpenFindCommand!.Execute(null);
            vm.FindText = "zzz";
            Assert.Equal("No matches", vm.FindStatus);
            Assert.All(m, x => Assert.False(x.IsFindMatch || x.IsFindCurrent));
            Assert.False(vm.FindNextCommand!.CanExecute(null));

            vm.FindText = "needle";
            vm.FindText = "";
            Assert.Equal("", vm.FindStatus);
            Assert.All(m, x => Assert.False(x.IsFindMatch || x.IsFindCurrent));

            vm.FindText = "needle";
            vm.CloseFindCommand!.Execute(null);
            Assert.False(vm.IsFindOpen);
            Assert.Equal("", vm.FindText);
            Assert.All(m, x => Assert.False(x.IsFindMatch || x.IsFindCurrent));
            Assert.Equal(Visibility.Collapsed, vm.FindVisibility);
        });
    }

    [Fact]
    public void New_messages_and_streamed_text_are_included_when_navigating()
    {
        RunSta(() =>
        {
            var (vm, convo, m) = Make();
            vm.OpenFindCommand!.Execute(null);
            vm.FindText = "needle";
            var late = new MessageVM("Assistant", "");
            convo.History.Add(late); vm.Messages.Add(late);
            late.Append("a late NEEDLE streamed in");
            vm.FindNextCommand!.Execute(null);                        // recounts: now 4 matches
            Assert.EndsWith("of 4", vm.FindStatus);
            Assert.True(late.IsFindMatch);
        });
    }

    [Fact]
    public void Switching_chats_closes_find_and_leaves_no_marks_behind()
    {
        RunSta(() =>
        {
            var (vm, convo, m) = Make();
            vm.OpenFindCommand!.Execute(null);
            vm.FindText = "needle";
            vm.NewChat();
            Assert.False(vm.IsFindOpen);
            Assert.Equal("", vm.FindText);
            Assert.All(m, x => Assert.False(x.IsFindMatch || x.IsFindCurrent));
            vm.SelectedConversation = convo;
            Assert.Equal("", vm.FindStatus);
        });
    }

    [Fact]
    public void Find_is_unavailable_without_a_chat_and_states_what_it_searches()
    {
        RunSta(() =>
        {
            var selection = new ProfileSelectionService(new MemPrefs());
            selection.SetSelectedAsync(new[] { new ProfileKey("demo", "default") }).GetAwaiter().GetResult();
            var vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
                () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
            Assert.False(vm.OpenFindCommand!.CanExecute(null));
            vm.OpenFindCommand.Execute(null);
            Assert.False(vm.IsFindOpen);
            Assert.Contains("loaded", vm.FindScopeNote, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Closing_a_find_never_edits_the_composer_or_transcript()
    {
        RunSta(() =>
        {
            var (vm, convo, m) = Make();
            vm.InputText = "my draft";
            vm.OpenFindCommand!.Execute(null);
            vm.FindText = "needle";
            vm.CloseFindCommand!.Execute(null);
            Assert.Equal("my draft", vm.InputText);
            Assert.Equal(4, convo.History.Count);
            Assert.Equal("Find the needle please", m[0].Text);
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
