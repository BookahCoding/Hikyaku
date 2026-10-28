using System.Net;
using System.Text;
using System.Text.Json;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 42 — the Inbox rebuilt on the dashboard's scheduled-job routes (option B, the maintainer, 2026-10-27):
/// <c>GET /api/cron/jobs</c> and <c>GET /api/cron/jobs/{id}/runs</c> (BookahHermes CronApi.kt @ 5f00986b). Fixtures only.
/// </summary>
public class Checkpoint42JobInboxTests
{
    private static CronJob J(string id, string? profile, string name = "Daily digest", string status = "ok")
    {
        var p = profile is null ? "" : $"\"profile\":\"{profile}\",";
        using var doc = JsonDocument.Parse($$"""{"id":"{{id}}","name":"{{name}}","prompt":"p",{{p}}"state":"scheduled","enabled":true,"last_status":"{{status}}"}""");
        return CronJob.FromJson(doc.RootElement)!;
    }

    private static SessionSummary Run(string id, long at, string preview = "result text") => new(id, Preview: preview, StartedAt: at);

    private static Task<JobInboxLoad> Load(IReadOnlyList<CronJob> jobs, Func<CronJob, IReadOnlyList<SessionSummary>> runs,
        string? profile = "default", bool all = true, ISet<string>? ticked = null, IReadOnlySet<string>? hidden = null) =>
        JobInbox.LoadAsync(_ => Task.FromResult(jobs), (j, _) => Task.FromResult(runs(j)), profile, all,
            p => (ticked ?? new HashSet<string> { "default", "mail" }).Contains(p), hidden ?? new HashSet<string>(), default);

    [Fact]
    public async Task Only_ticked_profiles_show_and_results_are_newest_first()
    {
        var jobs = new[] { J("j1", "default", "Digest"), J("j2", "mail", "Mail sweep"), J("j3", "family-member", "Hers"), J("j4", null, "Unowned") };
        var load = await Load(jobs, j => j.Id switch
        {
            "j1" => [Run("s1", 100), Run("s2", 300)],
            "j2" => [Run("s3", 200)],
            _ => [Run("x", 999)]
        });
        Assert.Equal(["s2", "s3", "s1"], load.Results.Select(r => r.Run.Id));
        Assert.DoesNotContain(load.Results, r => r.Title is "Hers" or "Unowned");    // not ticked; no owner in the all-profiles list
    }

    [Fact]
    public async Task One_profiles_list_without_profile_tags_belongs_to_that_profile()
    {
        var load = await Load([J("j1", null)], _ => [Run("s1", 1)], profile: "mail", all: false);
        Assert.Equal("mail", Assert.Single(load.Results).Profile);
    }

    [Fact]
    public async Task Hidden_jobs_are_left_out_and_counted_and_a_failing_job_does_not_stop_the_rest()
    {
        var jobs = new[] { J("j1", "default"), J("j2", "default", "Broken"), J("j3", "mail", "Hidden one") };
        var load = await Load(jobs, j => j.Id == "j2" ? throw new CronException(CronFailure.Failed, "boom") : [Run("r-" + j.Id, 5)],
            hidden: new HashSet<string> { "mail/j3" });
        Assert.Equal(["r-j1"], load.Results.Select(r => r.Run.Id));
        Assert.Equal(1, load.HiddenJobs);
        Assert.Contains(load.Problems, p => p.Contains("Broken"));
    }

    [Fact]
    public async Task Only_the_latest_run_carries_the_jobs_failure()
    {
        var load = await Load([J("j1", "default", status: "error")], _ => [Run("old", 1), Run("new", 2)]);
        Assert.True(load.Results.Single(r => r.Run.Id == "new").Failed);
        Assert.False(load.Results.Single(r => r.Run.Id == "old").Failed);
    }

    [Fact]
    public void Notifications_never_replay_old_results_and_announce_each_new_one_once()
    {
        var news = new JobInboxNews();
        var first = new[] { new JobResult(J("j1", "default"), Run("s1", 1), false) };
        Assert.Empty(news.Next(first));                                                  // app start: learn, announce nothing
        var second = first.Append(new JobResult(J("j1", "default"), Run("s2", 2), false)).ToList();
        Assert.Equal(["s2"], news.Next(second).Select(r => r.Run.Id));
        Assert.Empty(news.Next(second));
        var (title, body) = JobInboxNews.Describe([second[1]], p => p == "default" ? "Shisha" : p);
        Assert.Equal("New scheduled job result", title);
        Assert.Equal("Daily digest · Shisha", body);
        Assert.DoesNotContain("result text", title + body);                              // the result itself is never in a notification
    }

    private sealed class MemStore : IPreferenceStore
    {
        public string? Content;
        public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Content);
        public Task SaveAsync(string content, CancellationToken ct = default) { Content = content; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Hidden_jobs_are_remembered_on_this_pc_and_an_unreadable_file_hides_nothing()
    {
        var store = new MemStore();
        var prefs = new JobInboxPrefs(store);
        await prefs.HideAsync("default/j1");
        var again = new JobInboxPrefs(store);
        await again.LoadAsync();
        Assert.Contains("default/j1", again.Hidden);
        await again.ShowAllAsync();
        Assert.Empty(again.Hidden);
        var broken = new JobInboxPrefs(new MemStore { Content = "{not json" });
        await broken.LoadAsync();
        Assert.Empty(broken.Hidden);
    }

    private sealed class Stub(List<string> seen, string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            seen.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task A_jobs_runs_are_read_with_its_own_profile_and_real_shaped_rows()
    {
        var seen = new List<string>();
        // Real shapes: numeric ids and float times are tolerated, a row without an id is skipped.
        var api = new CronApi(new DashboardAuth(new Uri("https://hermes.example/"), new Stub(seen,
            """{"runs":[{"id":"cron_j1_20261027","title":"Digest","preview":"3 new mails","started_at":1761555600.25,"message_count":2},{"title":"no id"}]}""")));
        var runs = await api.RunsAsync(J("j/1", "mail"), 10, default);
        Assert.Equal("/api/cron/jobs/j%2F1/runs?limit=10&profile=mail", Assert.Single(seen));
        var run = Assert.Single(runs);
        Assert.Equal("cron_j1_20261027", run.Id);
        Assert.Equal(1761555600, run.StartedAt);
        Assert.Equal("3 new mails", run.Preview);
    }
}
