using System.Net;
using System.Net.Http;
using System.Text;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 42 — the Inbox page on the dashboard's scheduled-job routes, through the real view model and a fixture gateway.
/// The ticked profiles are chosen at construction (changing them later hangs the test host).
/// </summary>
[Collection("StaMath")]
public class Checkpoint42JobInboxWpfTests
{
    private sealed class Stub(List<string> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var q = request.RequestUri!.PathAndQuery;
            lock (seen) seen.Add(q);
            string json;
            if (q.StartsWith("/api/cron/jobs?profile=all")) json = """
                [{"id":"d1","name":"Digest","prompt":"p","profile":"default","state":"scheduled"},
                 {"id":"w1","name":"Work digest","prompt":"p","profile":"work","state":"scheduled"},
                 {"id":"x1","name":"Someone else's","prompt":"p","profile":"family-member","state":"scheduled"},
                 {"id":"u1","name":"Unowned","prompt":"p","state":"scheduled"}]
                """;
            else if (q.StartsWith("/api/cron/jobs?profile=work")) json = """[{"id":"w1","name":"Work digest","prompt":"p","state":"scheduled"}]""";
            else if (q.StartsWith("/api/cron/jobs?profile=")) json = """[{"id":"d1","name":"Digest","prompt":"p","state":"scheduled","last_status":"error"}]""";
            else if (q.Contains("/runs")) json = $$"""{"runs":[{"id":"run-{{q.Split('/')[4]}}","preview":"synthetic result","started_at":1761555600}]}""";
            else json = "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class MemStore : IPreferenceStore
    {
        public string? Content;
        public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Content);
        public Task SaveAsync(string content, CancellationToken ct = default) { Content = content; return Task.CompletedTask; }
    }

    private static (MainViewModel Vm, List<string> Seen) Make(bool advanced = true)
    {
        var seen = new List<string>();
        var selection = new ProfileSelectionService(new MemStore());
        selection.SetSelectedAsync([new ProfileKey("gw", "default"), new ProfileKey("gw", "work")]).GetAwaiter().GetResult();
        var auth = new DashboardAuth(new Uri("https://hermes.example/"), new Stub(seen));
        var vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
            () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
        vm.AttachCron(() => new CronApi(auth), "gw");
        vm.AttachJobInbox(new JobInboxPrefs(new MemStore()));
        vm.ApplyExperience(advanced);
        vm.JobInboxShowAllResults = true;                                             // Stage 43 opens on folders; these tests read the list
        return (vm, seen);
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
    public void The_inbox_shows_the_chosen_profiles_job_results_and_follows_the_switcher()
    {
        RunSta(() =>
        {
            var (vm, seen) = Make(advanced: false);                                       // the Inbox is part of Simple view
            vm.RefreshJobInboxAsync().GetAwaiter().GetResult();
            Assert.Contains("/api/cron/jobs?profile=default", seen);
            var row = Assert.Single(vm.JobResults);
            Assert.Equal("Digest", row.Title);
            Assert.Equal(System.Windows.Visibility.Visible, row.FailedVisibility);          // the job's last run failed
            Assert.Contains("/api/cron/jobs/d1/runs?limit=10&profile=default", seen);       // runs asked with the job's own profile

            vm.SwitchProfile(new ProfileKey("gw", "work"));
            vm.RefreshJobInboxAsync().GetAwaiter().GetResult();
            Assert.Equal("Work digest", Assert.Single(vm.JobResults).Title);
        });
    }

    [Fact]
    public void All_my_profiles_shows_every_ticked_profile_and_never_an_unticked_or_unowned_job()
    {
        RunSta(() =>
        {
            var (vm, _) = Make();
            vm.JobInboxAllProfiles = true;
            vm.RefreshJobInboxAsync().GetAwaiter().GetResult();
            Assert.Equal(["Digest", "Work digest"], vm.JobResults.Select(r => r.Title).Order());
            Assert.All(vm.JobResults, r => Assert.Equal(System.Windows.Visibility.Visible, r.ProfileVisibility));
        });
    }

    [Fact]
    public void Hiding_a_job_removes_its_results_here_and_show_them_brings_it_back()
    {
        RunSta(() =>
        {
            var (vm, _) = Make();
            vm.RefreshJobInboxAsync().GetAwaiter().GetResult();
            vm.JobResults[0].HideCommand.Execute(null);
            SpinWait.SpinUntil(() => vm.JobResults.Count == 0, 2000);
            Assert.Empty(vm.JobResults);
            Assert.Equal("1 job is hidden.", vm.HiddenJobsNote);
            vm.RefreshJobInboxAsync().GetAwaiter().GetResult();
            Assert.Empty(vm.JobResults);                                                     // stays hidden on refresh
            vm.ShowHiddenJobsCommand.Execute(null);
            SpinWait.SpinUntil(() => vm.JobResults.Count == 1, 2000);
            Assert.Single(vm.JobResults);
        });
    }

    [Fact]
    public void Clicking_a_result_opens_that_runs_chat_under_the_jobs_own_profile()
    {
        RunSta(() =>
        {
            var (vm, _) = Make();
            vm.JobInboxAllProfiles = true;
            vm.RefreshJobInboxAsync().GetAwaiter().GetResult();
            var work = vm.JobResults.Single(r => r.Title == "Work digest");
            work.OpenCommand.Execute(null);
            SpinWait.SpinUntil(() => vm.SelectedConversation?.StoredSessionId == "run-w1", 2000);
            Assert.Equal(MainNav.Chats, vm.Nav);
            Assert.Equal("run-w1", vm.SelectedConversation!.StoredSessionId);
            Assert.Equal("work", vm.SelectedConversation.Profile.ProfileId);
        });
    }
}
