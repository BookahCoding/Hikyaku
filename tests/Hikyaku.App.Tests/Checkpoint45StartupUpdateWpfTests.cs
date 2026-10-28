using System.Windows;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 45 — the "Update to x.y.z" button that the start-up check sets: shown only once a newer version is known, and only
/// where updates live (Settings → Updates, Advanced). New settings check at start by default.
/// </summary>
[Collection("StaMath")]
public class Checkpoint45StartupUpdateWpfTests
{
    private sealed class MemPrefs : IPreferenceStore
    {
        private string? _c;
        public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_c);
        public Task SaveAsync(string content, CancellationToken ct = default) { _c = content; return Task.CompletedTask; }
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
    public void The_update_button_shows_once_a_newer_version_is_known_in_both_views()
    {
        RunSta(() =>
        {
            var selection = new ProfileSelectionService(new MemPrefs());
            var vm = new MainViewModel(selection, new NotificationGate(selection), () => throw new NotSupportedException(), () => "t",
                () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
            vm.ApplyExperience(true);
            Assert.Equal(Visibility.Collapsed, vm.UpdateNoticeVisibility);                 // nothing known yet

            vm.UpdateNotice = "Update to 0.5.1";
            Assert.Equal(Visibility.Visible, vm.UpdateNoticeVisibility);

            vm.ApplyExperience(false);
            Assert.Equal(Visibility.Visible, vm.UpdateNoticeVisibility);                   // 0.6.1: Simple view is offered updates too
            Assert.True(new AppSettings().CheckUpdatesOnStart);
        });
    }
}
