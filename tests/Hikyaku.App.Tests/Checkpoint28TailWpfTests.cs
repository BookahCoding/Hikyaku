using System.Windows;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 28 — reading position through the real MainViewModel (the window's scrolling is fed through ReportMessageScroll).</summary>
[Collection("StaMath")]
public class Checkpoint28TailWpfTests
{
    private static MainViewModel Make()
    {
        var selection = new ProfileSelectionService(new MemPrefs28());
        selection.SetSelectedAsync(new[] { new ProfileKey("demo", "default") }).GetAwaiter().GetResult();
        return new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
            () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
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
    public void Opening_or_switching_a_chat_scrolls_to_its_newest_message_and_follows()
    {
        RunSta(() =>
        {
            var vm = Make();
            var asked = 0;
            vm.ScrollToEndRequested += () => asked++;
            vm.NewChat();
            Assert.True(asked >= 1);
            vm.ReportMessageScroll(100, 1000, 500, -300, 0);          // reader scrolled away
            Assert.False(vm.FollowingLatest);
            var before = asked;
            vm.NewChat();                                              // another chat is shown
            Assert.True(asked > before);
            Assert.True(vm.FollowingLatest);
            Assert.Equal(Visibility.Collapsed, vm.JumpToLatestVisibility);
        });
    }

    [Fact]
    public void A_growing_reply_asks_the_window_to_follow_only_while_the_reader_is_at_the_bottom()
    {
        RunSta(() =>
        {
            var vm = Make();
            vm.NewChat();
            Assert.True(vm.ReportMessageScroll(500, 1060, 500, 0, 60));    // at the bottom, content grew: follow
            Assert.False(vm.ReportMessageScroll(300, 1060, 500, -260, 0)); // reader scrolls up
            Assert.False(vm.ReportMessageScroll(300, 1200, 500, 0, 140));  // reply keeps growing: no yanking
            Assert.Equal(Visibility.Visible, vm.JumpToLatestVisibility);
            Assert.True(vm.HasNewBelow);
            Assert.Equal("New messages below  ↓", vm.JumpToLatestLabel);
        });
    }

    [Fact]
    public void Jump_to_latest_follows_again_scrolls_and_hides_the_button()
    {
        RunSta(() =>
        {
            var vm = Make();
            vm.NewChat();
            vm.ReportMessageScroll(100, 1000, 500, -400, 0);
            Assert.Equal(Visibility.Visible, vm.JumpToLatestVisibility);
            Assert.Equal("Jump to latest  ↓", vm.JumpToLatestLabel);
            var asked = 0;
            vm.ScrollToEndRequested += () => asked++;
            vm.JumpToLatestCommand!.Execute(null);
            Assert.Equal(1, asked);
            Assert.True(vm.FollowingLatest);
            Assert.Equal(Visibility.Collapsed, vm.JumpToLatestVisibility);
        });
    }

    [Fact]
    public void Older_messages_loaded_above_are_not_reported_as_new_content_below()
    {
        RunSta(() =>
        {
            var vm = Make();
            vm.NewChat();
            var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
            vm.TailClock = () => now;
            vm.ReportMessageScroll(50, 1000, 500, -450, 0);                // reading near the top
            typeof(MainViewModel).GetMethod("IgnoreTailGrowthBriefly", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(vm, null);
            vm.ReportMessageScroll(50, 1800, 500, 0, 800);                 // 800px of older messages appeared above
            Assert.False(vm.HasNewBelow);

            now = now.AddSeconds(5);                                       // later, real new content below is flagged again
            vm.ReportMessageScroll(50, 1900, 500, 0, 100);
            Assert.True(vm.HasNewBelow);
        });
    }
}

file sealed class MemPrefs28 : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
