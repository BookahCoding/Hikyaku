using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// 0.5.2 live finding: after Settings was saved (the maintainer switched Simple → Advanced) the chat was no longer selected —
/// Send, + and the microphone greyed out — and New chat did not help. Saving Settings refreshes the chat list, which empties
/// and refills the sidebar list; that list's selection is bound two-way to the selected chat, so emptying it wrote "no chat"
/// back into the app. Reproduced here with a real ListBox bound exactly like MainWindow's "Open here" list.
/// </summary>
[Collection("StaMath")]
public class Checkpoint48SelectionSurvivesSettingsWpfTests
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

    private static void Settle() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    [Fact]
    public void Saving_settings_keeps_the_open_chat_selected_and_new_chat_still_works()
    {
        RunSta(() =>
        {
            var selection = new ProfileSelectionService(new MemPrefs());
            selection.SetSelectedAsync([new ProfileKey("gw", "default")]).GetAwaiter().GetResult();
            var vm = new MainViewModel(selection, new NotificationGate(selection), () => throw new NotSupportedException(), () => "t",
                () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
            vm.ApplyExperience(false);
            var list = new ListBox();
            list.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.Conversations)));
            list.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedConversation)) { Mode = BindingMode.TwoWay });
            var window = new Window { Content = list, DataContext = vm, Left = -10000, Top = -10000, ShowInTaskbar = false, Width = 300, Height = 300 };
            window.Show();
            try
            {
                vm.NewChat();
                Settle();
                var chat = vm.SelectedConversation;
                Assert.NotNull(chat);

                // What MainWindow does after Settings is saved.
                vm.ApplyExperience(true);
                vm.RefreshVisibility();
                Settle();
                Assert.Same(chat, vm.SelectedConversation);
                vm.InputText = "can't send a message";
                Assert.True(vm.SendCommand.CanExecute(null));

                vm.NewChat();
                Settle();
                Assert.NotNull(vm.SelectedConversation);
                Assert.NotSame(chat, vm.SelectedConversation);
            }
            finally { window.Close(); }
        });
    }
}
