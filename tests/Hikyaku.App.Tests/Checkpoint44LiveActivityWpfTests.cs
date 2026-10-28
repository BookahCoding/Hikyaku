using System.Runtime.CompilerServices;
using System.Windows;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 44 — a reply shows what the agent did while answering, through the real view model and a scripted transport:
/// reasoning that becomes "Thought for Ns", tool steps that fill in, the plan, a flagged output, and an activity line that
/// goes when the turn ends. Simple view keeps the warning and the plain activity line, not the reasoning, the plan or notices.
/// </summary>
[Collection("StaMath")]
public class Checkpoint44LiveActivityWpfTests
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

    private static MainViewModel Make(IGatewayTransport transport, bool advanced)
    {
        var selection = new ProfileSelectionService(new MemPrefs());
        selection.SetSelectedAsync([new ProfileKey("gw", "default")]).GetAwaiter().GetResult();
        var vm = new MainViewModel(selection, new NotificationGate(selection), () => transport, () => "t",
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

    private static readonly ChatEvent[] Turn =
    [
        new ReasoningDelta("Check the folder first."),
        new ToolActivity("terminal: ls -la"),                                           // the transport sends the old summary first, as the gateway does
        new ToolStarted("t1", "terminal", "the project folder", "ls -la"),
        new PlanUpdated(new TodoList([new TodoItem("1", "List the folder", TodoStatus.InProgress), new TodoItem("2", "Answer", TodoStatus.Pending)], 1)),
        new ToolFinished("t1", "listed 1 file", 0.3, null, "notes.txt", false),
        new ToolActivity("web_fetch: https://example.invalid/"),
        new ToolStarted("t2", "web_fetch", null, "https://example.invalid/"),
        new ToolRiskFlagged("t2", ["instructions aimed at the agent"], true),
        new ToolFinished("t2", null, null, null, "synthetic page", false),
        new PlanUpdated(new TodoList([new TodoItem("1", "List the folder", TodoStatus.Completed)], 0)),   // an older revision: ignored
        new AgentNotice("Context was compressed."),
        new ToolActivity("browser"),
        new ToolStarted("t3", "browser", null, null),                                  // never reports back before the turn ends
        new TokenDelta("There is one file."),
        new StreamCompleted()
    ];

    private static MessageVM SendAndWait(MainViewModel vm)
    {
        vm.InputText = "What is in the folder?";
        vm.SendCommand.Execute(null);
        SpinWait.SpinUntil(() => !vm.CancelCommand.CanExecute(null) && vm.SelectedConversation!.History.LastOrDefault() is { Role: "Assistant" } r && r.Text.Length > 0, 5000);
        return vm.SelectedConversation!.History.Last();
    }

    [Fact]
    public void A_reply_shows_its_reasoning_steps_plan_warning_and_notice_and_nothing_stays_running()
    {
        RunSta(() =>
        {
            var vm = Make(new Scripted(Turn), advanced: true);
            var reply = SendAndWait(vm);

            Assert.Equal("Check the folder first.", reply.Reasoning);
            Assert.StartsWith("Thought for", reply.ReasoningHeader);                       // fixed when the turn moved on
            Assert.Equal(["terminal", "web_fetch", "browser"], reply.Tools.Select(t => t.Name));
            Assert.False(reply.IsLive);
            Assert.All(reply.Tools, t => Assert.False(t.Running));                         // a step that never reported back is closed too
            Assert.Equal("notes.txt", reply.Tools[0].Output);
            Assert.Equal("listed 1 file · 0.3 s", reply.Tools[0].Meta);
            Assert.Contains("instructions aimed at the agent", reply.Tools[1].Risk);
            Assert.Equal(Visibility.Visible, reply.RiskWarningVisibility);
            Assert.Equal("Plan · 0 of 2 done", reply.PlanHeader);                          // the late, older plan did not replace it
            Assert.Equal(["Context was compressed."], reply.Notices);
            Assert.Equal(Visibility.Collapsed, reply.ActivityLineVisibility);              // the turn is over
            Assert.Equal(Visibility.Visible, vm.ReasoningDetailsVisibility);
            Assert.Equal(Visibility.Visible, vm.PlanDetailsVisibility);
            Assert.Equal(Visibility.Visible, vm.NoticeDetailsVisibility);
        });
    }

    [Fact]
    public void Simple_view_keeps_the_warning_and_a_plain_activity_line_but_not_the_reasoning_the_plan_or_notices()
    {
        RunSta(() =>
        {
            var vm = Make(new Scripted(Turn), advanced: false);
            var reply = SendAndWait(vm);
            Assert.Equal(Visibility.Collapsed, vm.ReasoningDetailsVisibility);
            Assert.Equal(Visibility.Collapsed, vm.PlanDetailsVisibility);
            Assert.Equal(Visibility.Collapsed, vm.NoticeDetailsVisibility);
            Assert.Equal(3, reply.Tools.Count);                                            // one step per tool, not one per event
            Assert.Equal(Visibility.Collapsed, reply.ActivityVisibility);                  // no tool line under the reply in Simple
            Assert.Equal(Visibility.Visible, vm.SimpleActivityVisibility);
            Assert.Equal(Visibility.Collapsed, vm.AdvancedActivityVisibility);
            Assert.Equal(Visibility.Visible, reply.RiskWarningVisibility);                 // a risk is never hidden in Simple

            var live = new MessageVM("Assistant") { IsLive = true };
            live.StartTool(new ToolStarted("x", "terminal", null, "rm -rf build"), DateTimeOffset.UtcNow);
            Assert.Equal("Working on it…", live.ActivityLineSimple);                        // no tool names or commands in Simple
            Assert.Equal("Running terminal…", live.ActivityLineAdvanced);
            Assert.Equal(Visibility.Visible, live.ActivityLineVisibility);
        });
    }
}
