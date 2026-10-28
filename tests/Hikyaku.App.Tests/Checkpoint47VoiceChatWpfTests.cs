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
}
