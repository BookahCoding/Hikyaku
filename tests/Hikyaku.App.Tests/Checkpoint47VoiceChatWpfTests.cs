using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 47 — the voice chat in the chat window, through the real view model, a scripted gateway and a fake microphone and
/// speaker: the headset button in both views, the bar and its plain status words, a missing microphone said in words, ending
/// on a chat switch, and one whole spoken turn (said → sent as the person's message → reply spoken → listening again).
/// </summary>
[Collection("StaMath")]
public class Checkpoint47VoiceChatWpfTests
{
    private sealed class Scripted(params ChatEvent[] events) : IGatewayTransport
    {
        public async IAsyncEnumerable<ChatEvent> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }

        public Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class MemPrefs : IPreferenceStore
    {
        private string? _c;
        public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_c);
        public Task SaveAsync(string content, CancellationToken ct = default) { _c = content; return Task.CompletedTask; }
    }

    private sealed class FakeAudio : IVoiceChatAudio
    {
        public bool Disposed;
        public event Action<short[], long>? MicFrame;
        public event Action<bool>? PlayingChanged;
        public event Action? SpeechDrained;
        public bool IsPlaying => false;
        public long T;
        public void Frames(float level, long ms)
        {
            var frame = Enumerable.Repeat((short)Math.Round(level / 4f * 32768f), 320).ToArray();
            for (var end = T + ms; T < end;) { T += 20; MicFrame?.Invoke(frame, T); }
        }
        public void BeginSpeech(int sampleRate, int channels) { }
        public void QueuePcm(byte[] pcm16le) => PlayingChanged?.Invoke(true);
        public void EndSpeech() { PlayingChanged?.Invoke(false); SpeechDrained?.Invoke(); }
        public void StopSpeech() { }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeBackend : IVoiceChatBackend
    {
        public FakeAudio Audio = new();
        public string? MicProblem;
        public readonly List<bool> Leases = new();
        public readonly List<string> Spoken = new();
        public string Transcript = "hello there";
        public (IVoiceChatAudio? Audio, string? Problem) OpenAudio() => MicProblem is null ? (Audio, null) : (null, MicProblem);
        public Task<VoiceChatSettings> LoadSettingsAsync(string profile, CancellationToken ct) => Task.FromResult(VoiceChatSettings.Defaults);
        public Task<string> TranscribeAsync(byte[] wav, string profile, CancellationToken ct) => Task.FromResult(Transcript);
        public Task<ISpeechStream?> OpenSpeechAsync(string profile, CancellationToken ct) => Task.FromResult<ISpeechStream?>(null);
        public Task<SpokenAudio> SpeakAsync(string text, string profile, CancellationToken ct)
        {
            lock (Spoken) Spoken.Add(text);
            return Task.FromResult(new SpokenAudio([1, 2], "audio/wav"));
        }
        public Task PlayClipAsync(SpokenAudio audio, CancellationToken ct) => Task.CompletedTask;
        public void StopClip() { }
        public Task LeaseAsync(bool active, string profile) { lock (Leases) Leases.Add(active); return Task.CompletedTask; }
    }

    private static MainViewModel Make(IGatewayTransport transport, FakeBackend backend, bool advanced)
    {
        var selection = new ProfileSelectionService(new MemPrefs());
        selection.SetSelectedAsync([new ProfileKey("gw", "default")]).GetAwaiter().GetResult();
        var vm = new MainViewModel(selection, new NotificationGate(selection), () => transport, () => "t",
            () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
        vm.ApplyExperience(advanced);
        vm.AttachVoiceChat(() => backend);
        vm.NewChat();
        return vm;
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

    /// <summary>Runs this thread's dispatcher until <paramref name="done"/> holds or 5 s pass.</summary>
    private static bool PumpUntil(Func<bool> done)
    {
        var frame = new DispatcherFrame();
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Background, (_, _) =>
        {
            if (done() || DateTime.UtcNow - started > TimeSpan.FromSeconds(5)) frame.Continue = false;
        }, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        return done();
    }

    [Fact]
    public void The_headset_button_is_offered_in_both_views_and_opens_the_bar_listening()
    {
        RunSta(() =>
        {
            foreach (var advanced in new[] { false, true })
            {
                var backend = new FakeBackend();
                var vm = Make(new Scripted(), backend, advanced);
                Assert.Equal(Visibility.Visible, vm.VoiceChatButtonVisibility);
                Assert.Equal(Visibility.Collapsed, vm.VoiceChatBarVisibility);
                vm.ToggleVoiceChatCommand.Execute(null);
                Assert.True(PumpUntil(() => vm.VoiceChatStatus == "Listening…"));
                Assert.Equal(Visibility.Visible, vm.VoiceChatBarVisibility);
                Assert.Equal([true], backend.Leases);

                vm.ToggleVoiceChatCommand.Execute(null);                        // the same button ends it
                Assert.True(PumpUntil(() => vm.VoiceChatBarVisibility == Visibility.Collapsed));
                Assert.True(backend.Audio.Disposed);
                Assert.True(PumpUntil(() => backend.Leases.Count == 2));
                Assert.Equal([true, false], backend.Leases);
            }
        });
    }

    [Fact]
    public void A_missing_microphone_is_said_in_words_and_nothing_starts()
    {
        RunSta(() =>
        {
            var backend = new FakeBackend { MicProblem = "Hikyaku could not open the microphone." };
            var vm = Make(new Scripted(), backend, advanced: false);
            vm.ToggleVoiceChatCommand.Execute(null);
            Assert.True(PumpUntil(() => vm.VoiceChatProblem.Length > 0));
            Assert.Equal("Hikyaku could not open the microphone.", vm.VoiceChatProblem);
            Assert.False(vm.VoiceChatActive);
            Assert.Empty(backend.Leases);
        });
    }

    [Fact]
    public void Switching_to_another_chat_ends_the_voice_chat()
    {
        RunSta(() =>
        {
            var backend = new FakeBackend();
            var vm = Make(new Scripted(), backend, advanced: true);
            vm.ToggleVoiceChatCommand.Execute(null);
            Assert.True(PumpUntil(() => vm.VoiceChatActive));
            vm.NewChat();
            Assert.True(PumpUntil(() => !vm.VoiceChatActive));
            Assert.True(backend.Audio.Disposed);
        });
    }

    [Fact]
    public void A_spoken_turn_is_sent_as_the_persons_message_and_the_reply_is_spoken_then_it_listens_again()
    {
        RunSta(() =>
        {
            var backend = new FakeBackend { Transcript = "What is in the folder?" };
            var vm = Make(new Scripted(new TokenDelta("There is one file."), new StreamCompleted()), backend, advanced: false);
            vm.ToggleVoiceChatCommand.Execute(null);
            Assert.True(PumpUntil(() => vm.VoiceChatStatus == "Listening…"));
            backend.Audio.Frames(0.005f, 300);
            backend.Audio.Frames(0.2f, 1000);
            backend.Audio.Frames(0.005f, 1400);

            Assert.True(PumpUntil(() => vm.SelectedConversation!.History.Any(m => m.Role == "You" && m.Text == "What is in the folder?")
                                        || vm.SelectedConversation!.History.Any(m => m.IsMine && m.Text == "What is in the folder?")));
            Assert.True(PumpUntil(() => { lock (backend.Spoken) return backend.Spoken.Contains("There is one file."); }));
            Assert.True(PumpUntil(() => vm.VoiceChatStatus == "Listening…"));
        });
    }

    /// <summary>A gateway that answers the first turn and then holds it, ignoring Stop, until released.</summary>
    private sealed class Holding(TaskCompletionSource release) : IGatewayTransport
    {
        public async IAsyncEnumerable<ChatEvent> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new TokenDelta("Once");
            await release.Task;
            yield return new StreamCompleted();
        }

        public Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private static void SayInto(FakeAudio audio) { audio.Frames(0.005f, 300); audio.Frames(0.2f, 1000); audio.Frames(0.005f, 1400); }

    [Fact]
    public void A_cut_in_waiting_for_the_old_turn_is_never_sent_into_another_chat_after_the_voice_chat_ended()
    {
        RunSta(() =>
        {
            var release = new TaskCompletionSource();
            var backend = new FakeBackend { Transcript = "first" };
            var vm = Make(new Holding(release), backend, advanced: true);
            vm.ToggleVoiceChatCommand.Execute(null);
            Assert.True(PumpUntil(() => vm.VoiceChatStatus == "Listening…"));
            SayInto(backend.Audio);
            Assert.True(PumpUntil(() => vm.SelectedConversation!.History.Any(m => m.IsMine && m.Text == "first")));
            Assert.True(PumpUntil(() => vm.VoiceChatStatus == "Thinking…"));
            backend.Transcript = "second";
            backend.Audio.Frames(0.01f, 600);
            backend.Audio.Frames(0.45f, 1400);                              // a cut-in, while the old turn will not stop
            backend.Audio.Frames(0.01f, 1500);
            PumpUntil(() => false);                                         // let the cut-in be transcribed and queued
            var first = vm.SelectedConversation!;
            vm.NewChat();                                                   // the person moves on: the voice chat ends
            Assert.True(PumpUntil(() => !vm.VoiceChatActive));
            release.SetResult();                                            // the old turn finally ends
            PumpUntil(() => false);
            Assert.DoesNotContain(vm.SelectedConversation!.History, m => m.IsMine);
            Assert.DoesNotContain(first.History, m => m.IsMine && m.Text == "second");
            Assert.Equal("", vm.InputText);
        });
    }

    [Fact]
    public void A_spoken_turn_leaves_the_typed_draft_and_staged_files_alone()
    {
        RunSta(() =>
        {
            var backend = new FakeBackend { Transcript = "What is in the folder?" };
            var vm = Make(new Scripted(new TokenDelta("One file."), new StreamCompleted()), backend, advanced: true);
            vm.InputText = "my half-written note";
            Assert.Null(vm.AddPastedImage([0x89, 0x50, 0x4E, 0x47]));
            vm.ToggleVoiceChatCommand.Execute(null);
            Assert.True(PumpUntil(() => vm.VoiceChatStatus == "Listening…"));
            SayInto(backend.Audio);
            Assert.True(PumpUntil(() => vm.SelectedConversation!.History.Any(m => m.IsMine)));
            var sent = vm.SelectedConversation!.History.First(m => m.IsMine);
            Assert.Equal("What is in the folder?", sent.Text);
            Assert.Empty(sent.Attachments);                                 // the staged picture did not go with it
            Assert.Equal("my half-written note", vm.InputText);
            Assert.Single(vm.PendingAttachments);
        });
    }

    [Fact]
    public void Only_this_chats_own_approval_pauses_the_voice_chat()
    {
        RunSta(() =>
        {
            var release = new TaskCompletionSource();
            var backend = new FakeBackend { Transcript = "Delete the old logs" };
            var vm = Make(new Holding(release), backend, advanced: true);
            vm.SelectedConversation!.SessionId = "s-mine";
            vm.ToggleVoiceChatCommand.Execute(null);
            Assert.True(PumpUntil(() => vm.VoiceChatStatus == "Listening…"));
            SayInto(backend.Audio);
            Assert.True(PumpUntil(() => vm.VoiceChatStatus == "Thinking…"));
            ApprovalRequest Ask(string id, string session) => new(id, session, new ProfileKey("gw", "default"), "rm -rf logs", "Delete logs", "terminal", []);
            vm.NeedsYouItems.Add(new NeedsYouItemVM(Ask("a1", "s-other")));    // another chat's approval
            PumpUntil(() => false);
            Assert.Equal("Thinking…", vm.VoiceChatStatus);
            vm.NeedsYouItems.Add(new NeedsYouItemVM(Ask("a2", "s-mine")));     // this chat's
            Assert.True(PumpUntil(() => vm.VoiceChatStatus.StartsWith("Waiting for you")));
            release.SetResult();                                               // the turn ends while it waits
            PumpUntil(() => false);
            vm.NeedsYouItems.RemoveAt(1);                                      // answered
            Assert.True(PumpUntil(() => vm.VoiceChatStatus == "Listening…"));  // not stuck waiting
        });
    }

    [Fact]
    public void Microphone_buffers_are_read_in_the_order_the_driver_filled_them()
    {
        var next = 6;
        Assert.Equal([6, 7, 0], MicBufferRing.TakeDone(ref next, [true, false, false, false, false, false, true, true]));
        Assert.Equal(1, next);
        Assert.Empty(MicBufferRing.TakeDone(ref next, [true, false, false, false, false, false, false, false]));   // 1 is not done yet
        Assert.Equal(1, next);
    }
}
