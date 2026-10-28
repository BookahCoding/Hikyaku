using System.Net;
using System.Net.Http;
using System.Text;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 43 — the Inbox opens on folders like the Android app, a job can be put in a group, and a script-only run (an output
/// document, not a chat) expands in place instead of opening a chat that answers 404. Fixture gateway; selection set at construction.
/// </summary>
[Collection("StaMath")]
public class Checkpoint43InboxFoldersWpfTests
{
    private sealed class Stub(List<string> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var q = request.RequestUri!.PathAndQuery;
            lock (seen) seen.Add(q);
            var json = q.StartsWith("/api/cron/jobs?") ? """
                [{"id":"agent","name":"Morning summary","prompt":"p","profile":"default","state":"scheduled"},
                 {"id":"script","name":"homelab-alerts","prompt":"p","profile":"default","state":"scheduled","last_status":"error"}]
                """
                : q.Contains("/agent/runs") ? """{"runs":[{"id":"cron_agent_20261027_090000","source":"cron","preview":"agent text","started_at":1761555600}]}"""
                : q.Contains("/script/runs") ? """{"runs":[{"id":"cron_output:script:2026-10-27_21-00-00","source":"cron_output","preview":"disk 91% full on nas","started_at":1761595200}]}"""
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

    private static (MainViewModel Vm, List<string> Seen, MemStore Groups) Make()
    {
        var seen = new List<string>();
        var groups = new MemStore();
        var selection = new ProfileSelectionService(new MemStore());
        selection.SetSelectedAsync([new ProfileKey("gw", "default")]).GetAwaiter().GetResult();
        var auth = new DashboardAuth(new Uri("https://hermes.example/"), new Stub(seen));
        var groupsStore = new InboxGroupsStore(groups);
        var vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
            () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => groupsStore);
        vm.AttachCron(() => new CronApi(auth), "gw");
        vm.AttachJobInbox(new JobInboxPrefs(new MemStore()));
        return (vm, seen, groups);
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

    [Fact]
    public void The_inbox_opens_on_folders_and_a_folder_shows_its_results_with_a_way_back()
    {
        RunSta(() =>
        {
            var (vm, _, _) = Make();
            vm.RefreshJobInboxAsync().GetAwaiter().GetResult();
            Assert.True(vm.InFolderList);
            Assert.Equal(["Failed", "homelab-alerts", "Morning summary"], vm.JobInboxFolders.Select(f => f.Name));
            vm.JobInboxFolders.Single(f => f.Name == "Morning summary").OpenCommand.Execute(null);
            Assert.Equal("Morning summary", vm.JobInboxTitle);
            Assert.Equal("Morning summary", Assert.Single(vm.JobResults).Title);
            vm.BackToInboxFoldersCommand.Execute(null);
            Assert.True(vm.InFolderList);
            Assert.Empty(vm.JobResults);
        });
    }

    [Fact]
    public void A_script_only_run_expands_in_place_and_never_asks_for_a_chat()
    {
        RunSta(() =>
        {
            var (vm, seen, _) = Make();
            vm.Nav = MainNav.Inbox;
            vm.JobInboxShowAllResults = true;
            vm.RefreshJobInboxAsync().GetAwaiter().GetResult();
            var script = vm.JobResults.Single(r => r.Title == "homelab-alerts");
            Assert.Equal(System.Windows.Visibility.Visible, script.ScriptVisibility);
            lock (seen) seen.Clear();
            script.OpenCommand.Execute(null);
            Assert.True(script.IsExpanded);                                               // shows the whole preview
            Assert.Equal(MainNav.Inbox, vm.Nav);                                         // no chat opened
            Assert.Null(vm.SelectedConversation?.StoredSessionId);
            lock (seen) Assert.DoesNotContain(seen, q => q.Contains("/api/sessions/"));   // the 404 the live test hit

            vm.JobResults.Single(r => r.Title == "Morning summary").OpenCommand.Execute(null);
            SpinWait.SpinUntil(() => vm.SelectedConversation?.StoredSessionId == "cron_agent_20261027_090000", 2000);
            Assert.Equal("cron_agent_20261027_090000", vm.SelectedConversation!.StoredSessionId);   // an agent run still opens
        });
    }

    [Fact]
    public void A_job_put_in_a_group_shows_under_that_group_and_the_group_is_remembered()
    {
        RunSta(() =>
        {
            var (vm, _, groups) = Make();
            vm.PromptText = (_, _) => "Home";
            vm.RefreshJobInboxAsync().GetAwaiter().GetResult();
            vm.JobInboxFolders.Single(f => f.Name == "homelab-alerts").MoveToGroupCommand!.Execute(null);
            SpinWait.SpinUntil(() => vm.JobInboxFolders.Any(f => f.Name == "Home"), 2000);
            Assert.Contains(vm.JobInboxFolders, f => f.Name == "Home" && f.Folder.Kind == InboxFolderKind.Group);
            Assert.DoesNotContain(vm.JobInboxFolders, f => f.Name == "homelab-alerts");
            Assert.Contains("Home", groups.Content);                                      // saved on this PC
            Assert.Null(vm.JobInboxFolders.Single(f => f.Name == "Failed").MoveToGroupCommand);   // the Failed folder is not a job
        });
    }
}
