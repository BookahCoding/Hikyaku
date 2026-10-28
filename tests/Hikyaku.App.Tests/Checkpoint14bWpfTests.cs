using System.Windows;
using System.Windows.Threading;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Checkpoint 14b — WPF voice draft/scope/dispatcher regressions (fixtures only).</summary>
[Collection("StaMath")]
public class Checkpoint14bWpfTests
{
    [Fact]
    public void Edited_voice_draft_does_not_insert_into_new_chat_after_scope_switch()
    {
        RunSta(() =>
        {
            var mic = new FakeMicrophoneCapture { NextPcm = new byte[640] };
            var transport = new FixtureAudioTransport { Transcript = "old dictation" };
            using var ctl = new VoiceCaptureController(mic, transport, new FakeSpeechPlayer());
            var vm = CreateBareVm();
            vm.AttachVoice(ctl);
            vm.NewChat();
            var chatA = vm.SelectedConversation!;

            vm.ToggleMicCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(40));
            vm.StopMicCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(40));
            vm.TranscribeVoiceCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(80));
            Assert.Contains("old dictation", vm.VoiceTranscriptDraft, StringComparison.Ordinal);
            vm.VoiceTranscriptDraft = "user-edited old draft";

            vm.NewChat(); // new conversation / scope
            Pump(TimeSpan.FromMilliseconds(50));
            var chatB = vm.SelectedConversation!;
            Assert.NotSame(chatA, chatB);

            Assert.True(string.IsNullOrWhiteSpace(vm.VoiceTranscriptDraft),
                "VM draft must clear on scope change; was: " + vm.VoiceTranscriptDraft);
            Assert.True(string.IsNullOrWhiteSpace(ctl.Transcript));

            vm.InsertVoiceTranscriptCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(40));
            Assert.DoesNotContain("user-edited old draft", vm.InputText ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("old dictation", vm.InputText ?? "", StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Late_async_transcribe_after_chat_switch_does_not_mutate_new_ui_draft()
    {
        RunSta(() =>
        {
            var mic = new FakeMicrophoneCapture { NextPcm = new byte[640] };
            var transport = new FixtureAudioTransport { DelayMs = 120, Transcript = "late async" };
            using var ctl = new VoiceCaptureController(mic, transport, new FakeSpeechPlayer());
            var vm = CreateBareVm();
            vm.AttachVoice(ctl);
            vm.NewChat();

            vm.ToggleMicCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(40));
            vm.StopMicCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(40));
            vm.TranscribeVoiceCommand!.Execute(null); // starts delayed transport

            vm.NewChat();
            Pump(TimeSpan.FromMilliseconds(40));
            Assert.True(string.IsNullOrWhiteSpace(vm.VoiceTranscriptDraft));

            Pump(TimeSpan.FromMilliseconds(200)); // allow late completion
            Assert.True(string.IsNullOrWhiteSpace(vm.VoiceTranscriptDraft),
                "Late completion must not paint into new chat draft");
            Assert.Null(ctl.Transcript);
            Assert.DoesNotContain("late async", vm.InputText ?? "", StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Background_voice_changed_uses_nonblocking_dispatcher_and_keeps_final_indicator()
    {
        RunSta(() =>
        {
            EnsureApplication();
            var mic = new FakeMicrophoneCapture { NextPcm = new byte[640] };
            using var ctl = new VoiceCaptureController(mic, new FixtureAudioTransport(), new FakeSpeechPlayer());
            var vm = CreateBareVm();
            vm.AttachVoice(ctl);
            vm.NewChat();

            vm.ToggleMicCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(40));
            Assert.True(vm.VoiceRecording);
            vm.StopMicCommand!.Execute(null);
            Pump(TimeSpan.FromMilliseconds(40));
            Assert.Equal(Visibility.Visible, vm.VoicePreviewVisibility);

            // Flood Changed from a background thread (EditTranscript → Raise).
            // Blocking Dispatcher.Invoke would prevent the UI pump from draining.
            var started = new ManualResetEventSlim(false);
            var done = new ManualResetEventSlim(false);
            Exception? bgErr = null;
            var bg = new Thread(() =>
            {
                try
                {
                    started.Set();
                    for (var i = 0; i < 80; i++)
                        ctl.EditTranscript("coalesce-" + i);
                }
                catch (Exception ex) { bgErr = ex; }
                finally { done.Set(); }
            });
            bg.IsBackground = true;
            bg.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)));

            Pump(TimeSpan.FromMilliseconds(400));
            Assert.True(done.Wait(TimeSpan.FromSeconds(2)), "Background Changed flood must not deadlock UI via Invoke");
            Assert.Null(bgErr);

            Pump(TimeSpan.FromMilliseconds(80));
            Assert.False(vm.VoiceRecording);
            Assert.Equal(Visibility.Visible, vm.VoicePreviewVisibility);
            Assert.Contains("coalesce-", vm.VoiceTranscriptDraft, StringComparison.Ordinal);
        });
    }

    private static void EnsureApplication()
    {
        if (Application.Current is not null) return;
        new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
    }

    private static MainViewModel CreateBareVm()
    {
        var selection = new ProfileSelectionService(new MemoryPrefs14b());
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

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}

file sealed class MemoryPrefs14b : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default)
    {
        _content = content;
        return Task.CompletedTask;
    }
}
