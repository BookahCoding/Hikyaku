using System.Windows;
using System.Windows.Threading;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 30 WPF: the per-reply Read aloud action through the real view model, fixture transport and player.</summary>
[Collection("StaMath")]
public class Checkpoint30ReadAloudWpfTests
{
    private static (MainViewModel Vm, FixtureAudioTransport Transport, FakeSpeechPlayer Player, MessageVM Reply, VoiceCaptureController Ctl) Setup(string text = "Hello **world**")
    {
        var transport = new FixtureAudioTransport();
        var player = new FakeSpeechPlayer();
        var ctl = new VoiceCaptureController(new FakeMicrophoneCapture(), transport, player);
        var vm = CreateBareVm();
        vm.AttachVoice(ctl);
        vm.NewChat();
        var reply = new MessageVM("Assistant", text);
        vm.SelectedConversation!.History.Add(new MessageVM("You", "hi"));
        vm.SelectedConversation.History.Add(reply);
        return (vm, transport, player, reply, ctl);
    }

    [Fact]
    public void Offered_only_on_assistant_replies_with_text()
    {
        RunSta(() =>
        {
            Assert.Equal(Visibility.Visible, new MessageVM("Assistant", "text").ReadAloudVisibility);
            Assert.Equal(Visibility.Collapsed, new MessageVM("Assistant", "  ").ReadAloudVisibility);
            Assert.Equal(Visibility.Collapsed, new MessageVM("You", "text").ReadAloudVisibility);
            Assert.Equal(Visibility.Collapsed, new MessageVM("/btw", "text") { IsSideCard = true }.ReadAloudVisibility);
            var failed = new MessageVM("Assistant", "text");
            failed.Failure = TurnFailures.Describe(TurnFailureKind.GatewayError, "x", PromptDeliveryState.Pending, false);
            Assert.Equal(Visibility.Collapsed, failed.ReadAloudVisibility);
            var streaming = new MessageVM("Assistant");
            Assert.Equal(Visibility.Collapsed, streaming.ReadAloudVisibility);
            streaming.Append("first words");
            Assert.Equal(Visibility.Visible, streaming.ReadAloudVisibility);
        });
    }

    [Fact]
    public void Click_reads_the_reply_then_the_label_returns_when_the_audio_ends()
    {
        RunSta(() =>
        {
            var (vm, transport, player, reply, ctl) = Setup();
            using var _ = ctl;
            player.HoldMs = 5_000;
            Assert.Equal("Read aloud", reply.ReadAloudLabel);
            Assert.True(vm.ReadAloudMessageCommand!.CanExecute(reply));

            vm.ReadAloudMessageCommand.Execute(reply);
            PumpUntil(() => player.PlayCount == 1);
            Assert.True(reply.IsReading);
            Assert.Equal("Stop reading", reply.ReadAloudLabel);
            Assert.Equal(1, transport.SpeakCalls);
            Assert.DoesNotContain("**", transport.LastSpokenText);

            player.SimulateMediaEnded(player.CurrentSessionId);
            Pump(150);
            Assert.False(reply.IsReading);
            Assert.Equal("Read aloud", reply.ReadAloudLabel);
            ctl.StopPlayback();
        });
    }

    [Fact]
    public void Second_click_stops_reading_and_sends_nothing_more()
    {
        RunSta(() =>
        {
            var (vm, transport, player, reply, ctl) = Setup();
            using var _ = ctl;
            player.HoldMs = 5_000;
            vm.ReadAloudMessageCommand!.Execute(reply);
            PumpUntil(() => player.PlayCount == 1);
            Assert.True(reply.IsReading);

            vm.ReadAloudMessageCommand.Execute(reply);
            Pump(150);
            Assert.False(reply.IsReading);
            Assert.True(player.IsIdle);
            Assert.Equal(1, transport.SpeakCalls);
            Assert.Contains("Stopped", vm.VoiceStatus);
        });
    }

    [Fact]
    public void Reading_another_reply_moves_the_label()
    {
        RunSta(() =>
        {
            var (vm, transport, player, first, ctl) = Setup("one");
            using var _ = ctl;
            var second = new MessageVM("Assistant", "two");
            vm.SelectedConversation!.History.Add(second);
            player.HoldMs = 5_000;
            vm.ReadAloudMessageCommand!.Execute(first);
            PumpUntil(() => player.PlayCount == 1);
            vm.ReadAloudMessageCommand.Execute(second);
            PumpUntil(() => player.PlayCount == 2);
            Assert.False(first.IsReading);
            Assert.True(second.IsReading);
            Assert.Equal(2, transport.SpeakCalls);
            Assert.Equal("two", transport.LastSpokenText);
            ctl.StopPlayback();
        });
    }

    [Fact]
    public void A_gateway_without_text_to_speech_is_reported_and_nothing_sticks()
    {
        RunSta(() =>
        {
            var (vm, transport, player, reply, ctl) = Setup();
            using var _ = ctl;
            transport.ThrowUnsupportedOnSpeak = true;
            vm.ReadAloudMessageCommand!.Execute(reply);
            Pump(200);
            Assert.False(reply.IsReading);
            Assert.Equal(VoiceCaptureController.ReadUnsupportedCopy, vm.VoiceStatus);
            Assert.Equal(0, player.PlayCount);
        });
    }

    [Fact]
    public void Switching_chat_stops_reading_and_clears_the_label()
    {
        RunSta(() =>
        {
            var (vm, _, player, reply, ctl) = Setup();
            using var _ = ctl;
            player.HoldMs = 5_000;
            vm.ReadAloudMessageCommand!.Execute(reply);
            PumpUntil(() => player.PlayCount == 1);
            Assert.True(reply.IsReading);
            vm.NewChat();
            Pump(150);
            Assert.False(reply.IsReading);
            Assert.True(player.IsIdle);
        });
    }

    [Fact]
    public void A_reply_not_in_the_open_chat_cannot_be_read()
    {
        RunSta(() =>
        {
            var (vm, transport, _, _, ctl) = Setup();
            using var _ = ctl;
            var stranger = new MessageVM("Assistant", "elsewhere");
            Assert.False(vm.ReadAloudMessageCommand!.CanExecute(stranger));
            Assert.Equal(0, transport.SpeakCalls);
        });
    }

    private static MainViewModel CreateBareVm()
    {
        var selection = new ProfileSelectionService(new MemoryPrefs());
        selection.SetSelectedAsync(new[] { new ProfileKey("demo", "default") }).GetAwaiter().GetResult();
        var gate = new NotificationGate(selection);
        return new MainViewModel(
            selection, gate,
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

    private static void PumpUntil(Func<bool> cond)
    {
        for (var i = 0; i < 100 && !cond(); i++) Pump(20);
        Assert.True(cond(), "condition not reached");
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
