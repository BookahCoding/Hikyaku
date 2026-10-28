using System.Windows;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Copy on a message copies that message's text and never sends it.</summary>
[Collection("StaMath")]
public class Checkpoint46CopyMessageWpfTests
{
    [Fact]
    public void Copy_is_offered_once_there_is_text_and_hands_that_text_over()
    {
        RunSta(() =>
        {
            var copied = new List<string>();
            var vm = CreateBareVm();
            vm.CopyPlainText = copied.Add;

            var empty = new MessageVM("Assistant");
            Assert.Equal(Visibility.Collapsed, empty.CopyVisibility);
            vm.CopyMessageCommand.Execute(empty);
            vm.CopyMessageCommand.Execute(null);
            Assert.Empty(copied);

            empty.Append("  ");
            Assert.Equal(Visibility.Collapsed, empty.CopyVisibility);

            var you = new MessageVM("You", "my question");
            var reply = new MessageVM("Assistant", "Hello **there**");
            var failed = new MessageVM("Assistant", "partial");
            failed.Failure = TurnFailures.Describe(TurnFailureKind.GatewayError, "x", PromptDeliveryState.Pending, false);
            Assert.Equal(Visibility.Visible, you.CopyVisibility);
            Assert.Equal(Visibility.Visible, reply.CopyVisibility);
            Assert.Equal(Visibility.Visible, failed.CopyVisibility);

            vm.CopyMessageCommand.Execute(reply);
            vm.CopyMessageCommand.Execute(you);
            Assert.Equal(["Hello **there**", "my question"], copied);
        });
    }

    [Fact]
    public void Copy_without_a_clipboard_hook_does_not_throw()
    {
        RunSta(() =>
        {
            var vm = CreateBareVm();
            vm.CopyMessageCommand.Execute(new MessageVM("Assistant", "text"));
        });
    }

    private static MainViewModel CreateBareVm()
    {
        var selection = new ProfileSelectionService(new MemoryPrefs());
        selection.SetSelectedAsync([new ProfileKey("demo", "default")]).GetAwaiter().GetResult();
        return new MainViewModel(
            selection, new NotificationGate(selection),
            () => new DemoTransport(),
            () => "test",
            () => null, () => null, () => null, () => null, () => null,
            () => null, () => null, () => null);
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }
}

file sealed class MemoryPrefs : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default)
    {
        _content = content;
        return Task.CompletedTask;
    }
}
