using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 23 — the palette shows the real shortcut keys, and the new palette entries reach the window.</summary>
[Collection("StaMath")]
public class Checkpoint23ShortcutWpfTests
{
    private static MainViewModel Make()
    {
        var selection = new ProfileSelectionService(new MemPrefs23());
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
    public void Palette_entries_show_the_keys_from_the_shortcut_table()
    {
        RunSta(() =>
        {
            var vm = Make();
            vm.OpenPaletteCommand!.Execute(null);
            vm.PaletteQuery = "new chat";
            Assert.Equal("Ctrl+N", vm.PaletteResults.First(r => r.Id == "chat.new").Hint);
            vm.PaletteQuery = "settings";
            Assert.Equal("Ctrl+,", vm.PaletteResults.First(r => r.Id == "app.settings").Hint);
            vm.PaletteQuery = "search sessions";
            Assert.Equal("Ctrl+Shift+F", vm.PaletteResults.First(r => r.Id == "chats.search").Hint);
        });
    }

    [Fact]
    public void Search_sessions_and_shortcut_list_entries_reach_the_window_and_close_the_palette()
    {
        RunSta(() =>
        {
            var vm = Make();
            var search = 0; var help = 0;
            vm.PaletteSearchSessionsRequested += () => search++;
            vm.PaletteShowShortcutsRequested += () => help++;
            foreach (var (query, id) in new[] { ("search sessions", "chats.search"), ("keyboard shortcuts", "app.shortcuts") })
            {
                vm.OpenPaletteCommand!.Execute(null);
                vm.PaletteQuery = query;
                vm.RunPaletteEntryCommand!.Execute(vm.PaletteResults.First(r => r.Id == id));
                Assert.False(vm.IsPaletteOpen);
            }
            Assert.Equal((1, 1), (search, help));
        });
    }

    [Fact]
    public void New_chat_leaves_a_draft_in_the_composer_alone()
    {
        RunSta(() =>
        {
            var vm = Make();
            vm.InputText = "half-written message";
            vm.NewChat();
            Assert.Equal("half-written message", vm.InputText);
            Assert.Equal(MainNav.Chats, vm.Nav);
        });
    }
}

file sealed class MemPrefs23 : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
