using System.Windows;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Release review (v1.0.0) — defects found by the final findings-first review, each reproduced here before it was fixed.
/// Selection is chosen at construction (changing it later hangs the test host).
/// </summary>
[Collection("StaMath")]
public class Checkpoint40ReviewFixesWpfTests
{
    private static MainViewModel Make(bool advanced = true)
    {
        var selection = new ProfileSelectionService(new MemPrefs40());
        selection.SetSelectedAsync([new ProfileKey("demo", "default")]).GetAwaiter().GetResult();
        var vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
            () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
        vm.ApplyExperience(advanced);
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

    private static void SetSelectedYolo(MainViewModel vm, bool on) => vm.SelectedConversation!.YoloEnabled = on;

    [Fact]
    public void Yolo_state_belongs_to_its_chat_so_another_chat_never_shows_it_and_the_chat_that_has_it_always_does()
    {
        RunSta(() =>
        {
            var vm = Make(advanced: false);
            var a = vm.SelectedConversation!;
            SetSelectedYolo(vm, true);
            Assert.Equal("YOLO on", vm.SelectedYoloLabel);

            vm.NewChat();                                                             // chat B: YOLO was never turned on here
            Assert.NotSame(a, vm.SelectedConversation);
            Assert.Equal("YOLO off", vm.SelectedYoloLabel);
            Assert.Equal(Visibility.Collapsed, vm.YoloVisibility);                    // Simple: nothing to warn about in B

            vm.SelectedConversation = a;                                              // back to A: the risk is still in effect
            Assert.Equal("YOLO on", vm.SelectedYoloLabel);
            Assert.Equal(Visibility.Visible, vm.YoloVisibility);
        });
    }

    [Fact]
    public void Turning_yolo_off_in_one_chat_leaves_another_chats_yolo_shown_as_on()
    {
        RunSta(() =>
        {
            var vm = Make(advanced: false);
            var a = vm.SelectedConversation!;
            SetSelectedYolo(vm, true);
            vm.NewChat();
            var b = vm.SelectedConversation!;
            SetSelectedYolo(vm, true);

            vm.ToggleYoloCommand.Execute(null);                                       // turning off asks nothing (demo transport: local only)
            Assert.Equal("YOLO off", vm.SelectedYoloLabel);
            Assert.False(b.YoloEnabled);                                             // the answer landed on B

            vm.SelectedConversation = a;
            Assert.Equal("YOLO on", vm.SelectedYoloLabel);                           // A was not touched
            Assert.Equal(Visibility.Visible, vm.YoloVisibility);
        });
    }

    /// <summary>A Needs-you service that holds one request of each kind and has no connection to answer through.</summary>
    private static (MainViewModel Vm, NeedsYouService Svc) MakeNeedsYou()
    {
        var selection = new ProfileSelectionService(new MemPrefs40());
        selection.SetSelectedAsync([new ProfileKey("demo", "default")]).GetAwaiter().GetResult();
        var gate = new NotificationGate(selection);
        var svc = new NeedsYouService(gate, selection, "demo", () => null);   // the socket is down
        foreach (var (id, method, payload) in new[]
        {
            ("r1", "approval", """{"profile":"default","command":"ls","description":"list"}"""),
            ("r2", "clarify", """{"profile":"default","question":"Which colour?"}"""),
            ("r3", "secret", """{"profile":"default","prompt":"API key","env_var":"X"}""")
        })
            Assert.True(svc.HandleGatewayEvent(new GatewayEvent("server.request:" + method, System.Text.Json.Nodes.JsonNode.Parse(payload), "s1", null, id)));
        var vm = new MainViewModel(selection, gate, () => new DemoTransport(), () => "t",
            () => null, () => svc, () => null, () => null, () => null, () => null, () => null, () => null);
        vm.ApplyExperience(false);
        vm.RefreshNeedsYou();
        return (vm, svc);
    }

    private static NeedsYouItemVM Item(MainViewModel vm, string id) => vm.NeedsYouItems.Single(i => i.Request.Id == id);

    [Fact]
    public void An_approval_that_cannot_be_sent_says_so_and_stays_waiting()
    {
        RunSta(() =>
        {
            var (vm, svc) = MakeNeedsYou();
            Assert.Equal("", vm.NeedsYouStatus);
            vm.AnswerOnceCommand.Execute(Item(vm, "r1"));
            Assert.Contains("Couldn't send your answer", vm.NeedsYouStatus);          // not a silent click
            Assert.Equal(Visibility.Visible, vm.NeedsYouStatusVisibility);
            Assert.Contains(svc.Pending, r => r.Id == "r1");                          // still waiting: the agent never got an answer
            Assert.Contains(vm.NeedsYouItems, i => i.Request.Id == "r1");
        });
    }

    [Fact]
    public void A_typed_answer_that_cannot_be_sent_is_put_back_in_the_box()
    {
        RunSta(() =>
        {
            var (vm, _) = MakeNeedsYou();
            vm.ClarifyAnswer = "blue";
            vm.AnswerClarifyCommand.Execute(Item(vm, "r2"));
            Assert.Equal("blue", vm.ClarifyAnswer);                                   // not lost
            Assert.Contains("Couldn't send your answer", vm.NeedsYouStatus);
        });
    }

    [Fact]
    public void A_secret_that_cannot_be_sent_is_not_kept_and_the_person_is_told()
    {
        RunSta(() =>
        {
            var (vm, _) = MakeNeedsYou();
            vm.SecretValue = "s3cr3t-value";
            vm.AnswerSecretCommand.Execute(Item(vm, "r3"));
            Assert.Equal("", vm.SecretValue);                                         // a secret is never held after the attempt
            Assert.Contains("Couldn't send your answer", vm.NeedsYouStatus);
        });
    }

    /// <summary>The Inbox follow save is Advanced-only inside the command too (the third review pass added the guard without a test).</summary>
    [Fact]
    public void Inbox_follow_save_is_never_attempted_in_simple_whoever_asks()
    {
        RunSta(() =>
        {
            foreach (var advanced in new[] { true, false })
            {
                var selection = new ProfileSelectionService(new MemPrefs40());
                selection.SetSelectedAsync([new ProfileKey("demo", "default")]).GetAwaiter().GetResult();
                var gate = new NotificationGate(selection);
                var store = new InboxStore(new MemPrefs40(), (_, bot) => gate.Allow(Surface.Inbox, new ProfileKey("demo", bot)));
                var inbox = new InboxService(store, gate, "https://gateway.example.invalid/", "demo", () => null, "device-1");   // no socket
                var vm = new MainViewModel(selection, gate, () => new DemoTransport(), () => "t",
                    () => inbox, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
                vm.ApplyExperience(advanced);
                vm.FollowJobId = "job-1";
                vm.SaveFollowCommand.Execute(null);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (advanced && vm.InboxStatus.Length == 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);
                if (advanced) Assert.NotEqual("", vm.InboxStatus);                 // Advanced really tries (and says it failed: no socket)
                else
                {
                    Thread.Sleep(100);
                    Assert.Equal("", vm.InboxStatus);                              // Simple: nothing attempted, nothing said
                }
            }
        });
    }
}

file sealed class MemPrefs40 : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
