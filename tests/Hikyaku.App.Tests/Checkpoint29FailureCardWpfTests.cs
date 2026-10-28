using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 29 — failed-reply cards through the real send path with a scripted transport that fails in different ways. The recovery
/// action only fills the message box: it never sends, never overwrites a draft, and is offered only when it is safe.
/// </summary>
[Collection("StaMath")]
public class Checkpoint29FailureCardWpfTests
{
    private sealed class Failing : IGatewayTransport
    {
        public Func<IEnumerable<object>> Script = () => [];
        public int Calls;

        public async IAsyncEnumerable<ChatEvent> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            await Task.Yield();
            foreach (var step in Script())
            {
                switch (step)
                {
                    case ChatEvent e: yield return e; break;
                    case Exception ex: throw ex;
                }
            }
        }
        public Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProfileInfo>>([]);
    }

    private static (MainViewModel Vm, Failing T) Make()
    {
        var t = new Failing();
        var selection = new ProfileSelectionService(new MemPrefs29());
        selection.SetSelectedAsync(new[] { new ProfileKey("demo", "default") }).GetAwaiter().GetResult();
        var vm = new MainViewModel(selection, new NotificationGate(selection), () => t, () => "t",
            () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
        vm.NewChat();
        return (vm, t);
    }

    private static MessageVM SendAndGetReply(MainViewModel vm, string text)
    {
        vm.InputText = text;
        vm.SendCommand.Execute(null);
        Pump(400);
        return vm.Messages.Last(m => m.Role == "Assistant");
    }

    [Fact]
    public void An_unexpected_error_before_anything_was_sent_offers_to_put_the_message_back_and_does_not_send_it_again()
    {
        RunSta(() =>
        {
            var (vm, t) = Make();
            t.Script = () => [new InvalidOperationException("boom")];
            var reply = SendAndGetReply(vm, "please summarise");
            Assert.NotNull(reply.Failure);
            Assert.Equal(TurnFailureKind.ClientError, reply.Failure!.Kind);
            Assert.Equal(Visibility.Visible, reply.FailureVisibility);
            Assert.Equal(Visibility.Visible, reply.RestoreVisibility);
            Assert.Equal(Visibility.Collapsed, reply.ActivityVisibility);          // the card replaces the raw line
            Assert.Contains("boom", reply.Failure.Detail);

            vm.RestorePromptCommand!.Execute(reply);
            Assert.Equal("please summarise", vm.InputText);
            Assert.Equal(Visibility.Collapsed, reply.RestoreVisibility);           // offered once
            Assert.Equal(1, t.Calls);                                              // putting it back sent nothing
        });
    }

    [Fact]
    public void A_gateway_error_for_the_turn_gets_a_card_that_says_the_message_arrived()
    {
        RunSta(() =>
        {
            var (vm, t) = Make();
            t.Script = () => [new SessionBound("rt-1", "st-1"), new ProtocolError("model overloaded")];
            var reply = SendAndGetReply(vm, "hello");
            Assert.Equal(TurnFailureKind.GatewayError, reply.Failure!.Kind);
            Assert.Contains("reached the gateway", reply.Failure.DeliveryNote);
            Assert.Equal("model overloaded", reply.Failure.Detail);
            Assert.Equal(Visibility.Visible, reply.RestoreVisibility);
        });
    }

    [Fact]
    public void A_dropped_connection_after_part_of_the_reply_keeps_it_and_does_not_invite_a_duplicate()
    {
        RunSta(() =>
        {
            var (vm, t) = Make();
            t.Script = () => [new SessionBound("rt-1", "st-1"), new TokenDelta("Half of an ans"), new IOException("reset")];
            var reply = SendAndGetReply(vm, "hello");
            Assert.Equal(TurnFailureKind.ConnectionDropped, reply.Failure!.Kind);
            Assert.Contains("part of the reply was kept", reply.Failure.DeliveryNote);
            Assert.Equal("Half of an ans", reply.Text);
            Assert.Equal(Visibility.Collapsed, reply.RestoreVisibility);
            Assert.Equal(1, t.Calls);
        });
    }

    [Fact]
    public void A_drop_before_any_acknowledgement_says_delivery_is_uncertain_and_offers_no_resend()
    {
        RunSta(() =>
        {
            var (vm, t) = Make();
            t.Script = () => [new IOException("reset")];
            var reply = SendAndGetReply(vm, "hello");
            Assert.Equal(TurnFailureKind.ConnectionDropped, reply.Failure!.Kind);
            Assert.Contains("may already have your message", reply.Failure.DeliveryNote);
            Assert.Equal(Visibility.Collapsed, reply.RestoreVisibility);
            vm.RestorePromptCommand!.Execute(reply);
            Assert.Equal("", vm.InputText);                                        // not restorable: the command does nothing
            Assert.Equal(1, t.Calls);
        });
    }

    [Fact]
    public void Restoring_never_overwrites_a_draft_in_the_message_box()
    {
        RunSta(() =>
        {
            var (vm, t) = Make();
            t.Script = () => [new InvalidOperationException("boom")];
            var reply = SendAndGetReply(vm, "first");
            vm.InputText = "something I am already writing";
            vm.RestorePromptCommand!.Execute(reply);
            Assert.Equal("something I am already writing", vm.InputText);
            Assert.Equal(Visibility.Visible, reply.RestoreVisibility);             // still available once the box is free
            Assert.Contains("already has text", vm.RewindNotice);
            vm.InputText = "";
            vm.RestorePromptCommand.Execute(reply);
            Assert.Equal("first", vm.InputText);
        });
    }

    [Fact]
    public void A_successful_reply_has_no_card()
    {
        RunSta(() =>
        {
            var (vm, t) = Make();
            t.Script = () => [new SessionBound("rt-1", "st-1"), new TokenDelta("Fine."), new StreamCompleted()];
            var reply = SendAndGetReply(vm, "hello");
            Assert.Null(reply.Failure);
            Assert.Equal(Visibility.Collapsed, reply.FailureVisibility);
            Assert.Equal("Fine.", reply.Text);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
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

file sealed class MemPrefs29 : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
