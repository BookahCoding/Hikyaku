using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 49 (0.7.0) — the Inbox shows what each run found: the agent's answer or the script's output instead of the run
/// document's header or the job's prompt, a status, unread marks that clear when a result is opened, folder unread counts, an
/// "Ask about this" for agent runs, a few fetches at a time, and the plugin hint only in Advanced view.
/// </summary>
[Collection("StaMath")]
public class Checkpoint49InboxResultsWpfTests
{
    /// <summary>One script job and <paramref name="agentJobs"/> agent jobs ("important-mail", then "job 1", "job 2", …), each with up to 10 runs.</summary>
    private sealed class Stub(int agentJobs) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var q = request.RequestUri!.PathAndQuery;
            var jobs = string.Join(",", Enumerable.Range(0, agentJobs).Select(k =>
                $$"""{"id":"agent{{k}}","name":"{{(k == 0 ? "important-mail" : "job " + k)}}","prompt":"/bookahmail-mail","profile":"default","state":"scheduled"}"""));
            string Runs(int k, int count) => "{\"runs\":[" + string.Join(",", Enumerable.Range(0, count).Select(i =>
                $$"""{"id":"cron_agent_{{k}}_{{i}}","source":"cron","preview":"/bookahmail-mail","started_at":{{1761555600 + k * 100 + i}}}""")) + "]}";
            var agentRuns = System.Text.RegularExpressions.Regex.Match(q, @"/agent(\d+)/runs");
            var json = q.StartsWith("/api/cron/jobs?") ? "[" + jobs + """,{"id":"script","name":"homelab-alerts","prompt":"p","profile":"default","state":"scheduled","last_status":"error"}]"""
                : agentRuns.Success ? Runs(int.Parse(agentRuns.Groups[1].Value), agentRuns.Groups[1].Value == "0" ? 1 : 10)
                : q.Contains("/script/runs") ? """{"runs":[{"id":"cron_output:script:2026-10-27_21-00-00","source":"cron_output","preview":"# Cron Job: homelab-alerts **Job ID:** script **Run Time:** 2026-10-27 21:00:00 **Mode:** no_agent (script) --- disk 91% full on nas","started_at":1761595200}]}"""
                : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class MemStore : IPreferenceStore
    {
        public string? Content;
        public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Content);
        public Task SaveAsync(string content, CancellationToken ct = default) { Content = content; return Task.CompletedTask; }
    }

    private sealed class NoPlugin : IJsonRpcPeer
    {
        public Task<JsonNode?> RequestAsync(string method, JsonObject? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default) =>
            throw new RpcException(InboxApi.NotACommand, "not a command");
    }

    private sealed class Answers
    {
        public int InFlight, MaxInFlight, Calls;
        public TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Hold;

        public async Task<IReadOnlyList<SessionMessage>> Load(string id, string? profile, int limit, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref InFlight);
            lock (this) { MaxInFlight = Math.Max(MaxInFlight, now); Calls++; }
            try
            {
                if (Hold) await Gate.Task;
                else await Task.Yield();
                return [new SessionMessage("1", "user", "/bookahmail-mail"), new SessionMessage("2", "assistant", "You have 2 important mails.\n\nOne from the bank.")];
            }
            finally { Interlocked.Decrement(ref InFlight); }
        }
    }

    private static (MainViewModel Vm, Answers Answers, InboxReadStore Reads) Make(int agentJobs = 1, bool advanced = true)
    {
        var selection = new ProfileSelectionService(new MemStore());
        selection.SetSelectedAsync([new ProfileKey("gw", "default")]).GetAwaiter().GetResult();
        var auth = new DashboardAuth(new Uri("https://hermes.example/"), new Stub(agentJobs));
        var groupsStore = new InboxGroupsStore(new MemStore());
        var vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
            () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => groupsStore);
        vm.ApplyExperience(advanced);
        vm.AttachCron(() => new CronApi(auth), "gw");
        vm.AttachJobInbox(new JobInboxPrefs(new MemStore()));
        var answers = new Answers();
        var reads = new InboxReadStore(new MemStore());
        vm.AttachJobRunDetails(new JobRunDetails(answers.Load, () => new NoPlugin()), reads);
        return (vm, answers, reads);
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

    private static bool PumpUntil(Func<bool> done, int ms = 5000)
    {
        var frame = new DispatcherFrame();
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Background, (_, _) =>
        {
            if (done() || DateTime.UtcNow - started > TimeSpan.FromMilliseconds(ms)) frame.Continue = false;
        }, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        return done();
    }

    private static void ShowAll(MainViewModel vm)
    {
        vm.RefreshJobInboxAsync().GetAwaiter().GetResult();
        vm.JobInboxShowAllResults = true;
    }

    [Fact]
    public void Rows_show_what_each_run_found_with_its_status_and_a_way_to_ask_about_agent_results()
    {
        RunSta(() =>
        {
            var (vm, _, _) = Make();
            ShowAll(vm);
            var agent = vm.JobResults.Single(r => r.Title == "important-mail");
            var script = vm.JobResults.Single(r => r.Title == "homelab-alerts");
            Assert.True(PumpUntil(() => agent.Excerpt.StartsWith("You have 2 important mails.")));
            Assert.DoesNotContain("/bookahmail-mail", agent.Excerpt);
            Assert.Equal("disk 91% full on nas", script.Excerpt);                     // the header is gone
            Assert.Equal("Failed", script.StatusLabel);
            Assert.Equal(Visibility.Collapsed, agent.AskVisibility);                  // shown once the result is opened
            agent.OpenCommand.Execute(null);
            script.OpenCommand.Execute(null);
            Assert.Equal(Visibility.Visible, agent.AskVisibility);
            Assert.Equal(Visibility.Collapsed, script.AskVisibility);                 // a script run has no chat
        });
    }

    [Fact]
    public void Opening_a_result_shows_it_whole_and_marks_it_read_and_its_folder_count_drops()
    {
        RunSta(() =>
        {
            var (vm, _, reads) = Make();
            vm.RefreshJobInboxAsync().GetAwaiter().GetResult();
            var folder = vm.JobInboxFolders.Single(f => f.Name == "important-mail");
            Assert.Contains("1 new", folder.Count);
            vm.JobInboxShowAllResults = true;
            var agent = vm.JobResults.Single(r => r.Title == "important-mail");
            Assert.True(agent.IsUnread);
            Assert.True(PumpUntil(() => agent.FullText.Length > 0));
            agent.OpenCommand.Execute(null);
            Assert.True(agent.IsExpanded);
            Assert.False(agent.IsUnread);
            Assert.Equal("You have 2 important mails.\n\nOne from the bank.", agent.FullText);
            Assert.True(reads.IsRead("gw", agent.Result.Key));
            vm.JobInboxShowAllResults = false;
            Assert.DoesNotContain("new", vm.JobInboxFolders.Single(f => f.Name == "important-mail").Count);
        });
    }

    [Fact]
    public void A_long_list_is_fetched_a_few_at_a_time_and_only_for_the_rows_on_show()
    {
        RunSta(() =>
        {
            var (vm, answers, _) = Make(agentJobs: 5);                               // 1 + 4 × 10 agent runs and one script run
            answers.Hold = true;
            ShowAll(vm);
            PumpUntil(() => false, 400);
            Assert.InRange(answers.MaxInFlight, 1, 3);
            answers.Gate.SetResult();
            var settled = PumpUntil(() => answers.InFlight == 0 && answers.Calls >= 19);
            Assert.True(settled, $"calls {answers.Calls}, in flight {answers.InFlight}, max {answers.MaxInFlight}, rows {vm.JobResults.Count}");
            PumpUntil(() => false, 300);
            // The first 20 rows, plus each job's newest run (fetched first, for the folders' previews): never all of them.
            var expected = vm.JobResults.Take(20).Select(r => r.Result)
                .Concat(vm.JobResults.GroupBy(r => r.Result.JobKey).Select(g => g.First().Result))
                .Where(r => !r.IsScriptOutput).Select(r => r.Key).Distinct().Count();
            Assert.Equal(expected, answers.Calls);
            Assert.True(answers.Calls < vm.JobResults.Count - 10);
            Assert.InRange(answers.MaxInFlight, 1, 3);
        });
    }

    [Fact]
    public void Mark_all_read_clears_the_list_on_show_and_the_plugin_hint_shows_only_in_advanced()
    {
        RunSta(() =>
        {
            var (vm, _, reads) = Make();
            ShowAll(vm);
            Assert.True(PumpUntil(() => vm.JobResults.All(r => r.Excerpt != "Loading…")));
            vm.MarkAllJobResultsReadCommand.Execute(null);
            Assert.All(vm.JobResults, r => Assert.False(r.IsUnread));
            Assert.All(vm.JobResults, r => Assert.True(reads.IsRead("gw", r.Result.Key)));
            Assert.Equal(Visibility.Visible, vm.JobInboxPluginHintVisibility);           // the script row asked; no plugin
            Assert.Contains("hermes plugins install BookahCoding/Hikyaku/hermes-plugin/hikyaku-cron", vm.JobInboxPluginHint);
            vm.ApplyExperience(false);
            Assert.Equal(Visibility.Collapsed, vm.JobInboxPluginHintVisibility);
        });
    }
}
