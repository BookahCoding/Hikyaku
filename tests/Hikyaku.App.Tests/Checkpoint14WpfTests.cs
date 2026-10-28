using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Checkpoint 14 WPF — mic toggle / preview / unsupported gate via real controller fixtures.</summary>
[Collection("StaMath")]
public class Checkpoint14WpfTests
{
    [Fact]
    public void Voice_controller_preview_panel_bindings_round_trip_on_dispatcher()
    {
        RunSta(() =>
        {
            var mic = new FakeMicrophoneCapture { NextPcm = new byte[640] };
            var transport = new FixtureAudioTransport { Transcript = "wpf fixture" };
            using var ctl = new VoiceCaptureController(mic, transport, new FakeSpeechPlayer());
            var vm = CreateBareVm();
            vm.AttachVoice(ctl);
            vm.NewChat();
            Assert.NotNull(vm.SelectedConversation);
            Assert.Contains("off", vm.VoicePhaseLabel, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Visibility.Collapsed, vm.VoicePreviewVisibility);

            Assert.True(vm.ToggleMicCommand!.CanExecute(null));
            vm.ToggleMicCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.True(vm.VoiceRecording);
            Assert.Equal(Visibility.Visible, vm.VoiceActiveIndicatorVisibility);

            vm.StopMicCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.False(vm.VoiceRecording);
            Assert.Equal(Visibility.Visible, vm.VoicePreviewVisibility);

            vm.TranscribeVoiceCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(80));
            Assert.Contains("wpf fixture", vm.VoiceTranscriptDraft, StringComparison.Ordinal);

            vm.InsertVoiceTranscriptCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.Contains("wpf fixture", vm.InputText, StringComparison.Ordinal);
            Assert.Equal(Visibility.Collapsed, vm.VoicePreviewVisibility);
        });
    }

    [Fact]
    public void Unsupported_transport_gates_mic_toggle()
    {
        RunSta(() =>
        {
            var transport = new FixtureAudioTransport { Capability = VoiceTransportCapability.Unsupported };
            using var ctl = new VoiceCaptureController(new FakeMicrophoneCapture(), transport, new FakeSpeechPlayer());
            var vm = CreateBareVm();
            vm.AttachVoice(ctl);
            vm.NewChat();
            ctl.RefreshCapabilityAsync().GetAwaiter().GetResult();
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.True(vm.VoiceUnsupported);
            Assert.Contains("unavailable", vm.VoicePhaseLabel, StringComparison.OrdinalIgnoreCase);
            Assert.False(vm.ToggleMicCommand!.CanExecute(null));
        });
    }

    [Fact]
    public void Unknown_dictation_capability_keeps_mic_usable_end_to_end()
    {
        // 15a: voice-live unavailable (chained) must not disable STT; capability is Unknown, not Unsupported.
        RunSta(() =>
        {
            var transport = new FixtureAudioTransport { Capability = VoiceTransportCapability.Unknown, Transcript = "unknown ok" };
            using var ctl = new VoiceCaptureController(
                new FakeMicrophoneCapture { NextPcm = new byte[640] }, transport, new FakeSpeechPlayer());
            var vm = CreateBareVm();
            vm.AttachVoice(ctl);
            vm.NewChat();
            ctl.RefreshCapabilityAsync().GetAwaiter().GetResult();
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.False(vm.VoiceUnsupported);
            Assert.True(vm.ToggleMicCommand!.CanExecute(null));
            vm.ToggleMicCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(50));
            vm.StopMicCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(50));
            vm.TranscribeVoiceCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(80));
            Assert.Contains("unknown ok", vm.VoiceTranscriptDraft, StringComparison.Ordinal);
            Assert.Equal(string.Empty, vm.InputText ?? string.Empty); // never auto-inserted or sent
        });
    }

    [Fact]
    public void Transcribe_route_404_disables_mic_after_real_attempt()
    {
        RunSta(() =>
        {
            var transport = new FixtureAudioTransport { Capability = VoiceTransportCapability.Unknown, ThrowUnsupportedOnTranscribe = true };
            using var ctl = new VoiceCaptureController(
                new FakeMicrophoneCapture { NextPcm = new byte[640] }, transport, new FakeSpeechPlayer());
            var vm = CreateBareVm();
            vm.AttachVoice(ctl);
            vm.NewChat();
            ctl.RefreshCapabilityAsync().GetAwaiter().GetResult();
            vm.ToggleMicCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(50));
            vm.StopMicCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(50));
            vm.TranscribeVoiceCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(100));
            Assert.True(vm.VoiceUnsupported);
            Assert.False(vm.ToggleMicCommand.CanExecute(null));
            Assert.False(vm.VoiceHasPreview);
        });
    }

    [Fact]
    public void Cancel_and_chat_switch_discard_preview()
    {
        RunSta(() =>
        {
            using var ctl = new VoiceCaptureController(
                new FakeMicrophoneCapture { NextPcm = new byte[640] },
                new FixtureAudioTransport(),
                new FakeSpeechPlayer());
            var vm = CreateBareVm();
            vm.AttachVoice(ctl);
            vm.NewChat();
            vm.ToggleMicCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(40));
            vm.StopMicCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(40));
            Assert.Equal(Visibility.Visible, vm.VoicePreviewVisibility);
            vm.CancelVoicePreviewCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(40));
            Assert.Equal(Visibility.Collapsed, vm.VoicePreviewVisibility);

            vm.ToggleMicCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(40));
            vm.StopMicCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(40));
            vm.NewChat(); // switches conversation / scope
            Pump(TimeSpan.FromMilliseconds(40));
            Assert.Equal(Visibility.Collapsed, vm.VoicePreviewVisibility);
            Assert.False(vm.VoiceRecording);
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
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
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
