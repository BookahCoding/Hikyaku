using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 22 — the Scheduled jobs page through the real MainViewModel: profile scoping, owner-profile actions, confirmations,
/// failure handling and the Simple-mode rule. Fixture HTTP only; no live gateway.
/// </summary>
[Collection("StaMath")]
public class Checkpoint22CronWpfTests
{
    private sealed record Seen(HttpMethod Method, string PathAndQuery, string Body);

    private sealed class Stub(List<Seen> seen, Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            seen.Add(new Seen(request.Method, request.RequestUri!.PathAndQuery, body));
            return reply(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // Three profiles exist on the gateway; only default and work are chosen in Hikyaku.
    private const string Listing = """
    [
      {"id":"d1","name":"Default job","prompt":"p","schedule_display":"every 1h","state":"scheduled","profile":"default","deliver":"local",
       "schedule":{"kind":"interval","minutes":60}},
      {"id":"w1","name":"[bot:Mika] Work job","prompt":"p","schedule_display":"daily at 09:00","state":"scheduled","profile":"work","deliver":"telegram"},
      {"id":"s1","name":"Secret profile job","prompt":"p","schedule_display":"every 1h","state":"scheduled","profile":"private"},
      {"id":"n1","name":"Nobody's job","prompt":"p","schedule_display":"every 1h","state":"scheduled"}
    ]
    """;

    private sealed class Rig : IDisposable
    {
        public MainViewModel Vm = null!;
        public List<Seen> Seen = [];
        public DashboardAuth Auth = null!;
        public List<(string Title, string Message)> Asked = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Reply = _ => Json(HttpStatusCode.OK, Listing);

        public Rig(bool confirm = true, params string[] profiles)
        {
            profiles = profiles.Length == 0 ? ["default", "work"] : profiles;
            var selection = new ProfileSelectionService(new MemPrefs22());
            selection.SetSelectedAsync(profiles.Select(p => new ProfileKey("gw", p))).GetAwaiter().GetResult();
            Auth = new DashboardAuth(new Uri("https://hermes.example/"), new Stub(Seen, r => Reply(r)));
            Vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
                () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
            Vm.AttachCron(() => new CronApi(Auth), "gw");
            Vm.ConfirmCron = (t, m) => { Asked.Add((t, m)); return confirm; };
        }

        public void Dispose() => Auth.Dispose();
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
    public void In_simple_view_scheduled_job_actions_send_nothing_whoever_asks()
    {
        RunSta(() =>
        {
            using var rig = new Rig(confirm: true);
            rig.Vm.RefreshCronAsync().GetAwaiter().GetResult();
            var row = rig.Vm.CronRows[0];
            rig.Vm.EditCron = _ => Task.FromResult<CronEditorResult?>(new("Nightly", "do it", "daily at 02:00", "local", "default"));   // the form would say yes
            rig.Vm.ApplyExperience(false);
            rig.Seen.Clear();
            rig.Vm.RunCronActionAsync(row, "run").GetAwaiter().GetResult();
            rig.Vm.RunCronActionAsync(row, "delete").GetAwaiter().GetResult();
            rig.Vm.NewCronJobAsync().GetAwaiter().GetResult();
            Assert.Empty(rig.Seen);
            Assert.Empty(rig.Asked);
        });
    }

    [Fact]
    public void All_profiles_shows_only_the_profiles_you_chose_and_never_an_unowned_job()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.CronAllProfiles = true;
            rig.Vm.RefreshCronAsync().GetAwaiter().GetResult();
            Assert.Equal("/api/cron/jobs?profile=all", rig.Seen.Single().PathAndQuery);
            Assert.Equal(["Default job", "Work job"], rig.Vm.CronRows.Select(r => r.Title));
            Assert.Equal("Routine · Mika · Profile work · Delivers to telegram", rig.Vm.CronRows[1].Meta);
        });
    }

    [Fact]
    public void One_profile_listing_asks_for_that_profile_and_tags_untagged_jobs_with_it()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Reply = _ => Json(HttpStatusCode.OK, """[{"id":"x","name":"Untagged","prompt":"p","schedule_display":"every 1h"}]""");
            rig.Vm.RefreshCronAsync().GetAwaiter().GetResult();
            Assert.Equal("/api/cron/jobs?profile=default", rig.Seen.Single().PathAndQuery);
            Assert.Equal("default", rig.Vm.CronRows.Single().Job.Profile);
        });
    }

    [Fact]
    public void Jobs_that_exist_only_for_unchosen_profiles_say_so_instead_of_looking_empty()
    {
        RunSta(() =>
        {
            using var rig = new Rig(true, "default");
            rig.Vm.CronAllProfiles = true;
            rig.Reply = _ => Json(HttpStatusCode.OK, """[{"id":"s1","name":"x","prompt":"p","profile":"private"}]""");
            rig.Vm.RefreshCronAsync().GetAwaiter().GetResult();
            Assert.Empty(rig.Vm.CronRows);
            Assert.Contains("none belong to the profiles you chose", rig.Vm.CronStatus);
        });
    }

    [Fact]
    public void Run_now_goes_to_the_owning_profile_and_only_after_an_explicit_yes()
    {
        RunSta(() =>
        {
            using var rig = new Rig(confirm: false);
            rig.Vm.CronAllProfiles = true;
            rig.Vm.RefreshCronAsync().GetAwaiter().GetResult();
            var work = rig.Vm.CronRows[1];
            rig.Seen.Clear();

            rig.Vm.RunCronActionAsync(work, "run").GetAwaiter().GetResult();
            Assert.Empty(rig.Seen);                                   // declined: nothing sent
            Assert.Contains("telegram", rig.Asked.Single().Message);  // and the question says where the result goes

            using var yes = new Rig(confirm: true);
            yes.Vm.CronAllProfiles = true;
            yes.Vm.RefreshCronAsync().GetAwaiter().GetResult();
            yes.Seen.Clear();
            yes.Reply = _ => Json(HttpStatusCode.OK, """{"id":"w1","name":"[bot:Mika] Work job","prompt":"p","state":"scheduled"}""");
            yes.Vm.RunCronActionAsync(yes.Vm.CronRows[1], "run").GetAwaiter().GetResult();
            var s = yes.Seen.Single();
            Assert.Equal(HttpMethod.Post, s.Method);
            Assert.Equal("/api/cron/jobs/w1/trigger?profile=work", s.PathAndQuery);   // the owner, though "default" is active
            Assert.Equal("work", yes.Vm.CronRows[1].Job.Profile);                      // reply without an owner keeps the row's
        });
    }

    [Fact]
    public void Without_a_confirmation_handler_run_and_delete_do_nothing()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.ConfirmCron = null;
            rig.Vm.RefreshCronAsync().GetAwaiter().GetResult();
            rig.Seen.Clear();
            rig.Vm.RunCronActionAsync(rig.Vm.CronRows[0], "run").GetAwaiter().GetResult();
            rig.Vm.RunCronActionAsync(rig.Vm.CronRows[0], "delete").GetAwaiter().GetResult();
            Assert.Empty(rig.Seen);
            Assert.Single(rig.Vm.CronRows, r => r.Job.Id == "d1");
        });
    }

    [Fact]
    public void Delete_names_the_job_removes_the_row_only_on_success_and_keeps_it_on_failure()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.RefreshCronAsync().GetAwaiter().GetResult();
            var row = rig.Vm.CronRows[0];
            rig.Seen.Clear();

            rig.Reply = _ => Json(HttpStatusCode.InternalServerError, """{"detail":"disk full"}""");
            rig.Vm.RunCronActionAsync(row, "delete").GetAwaiter().GetResult();
            Assert.Contains(row, rig.Vm.CronRows);
            Assert.Equal("disk full", rig.Vm.CronStatus);
            Assert.Contains("Default job", rig.Asked.Last().Message);

            rig.Reply = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
            rig.Vm.RunCronActionAsync(row, "delete").GetAwaiter().GetResult();
            Assert.DoesNotContain(row, rig.Vm.CronRows);
            Assert.Equal(HttpMethod.Delete, rig.Seen.Last().Method);
            Assert.Equal("/api/cron/jobs/d1?profile=default", rig.Seen.Last().PathAndQuery);
        });
    }

    [Fact]
    public void Pause_and_resume_update_the_row_from_the_reply()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.RefreshCronAsync().GetAwaiter().GetResult();
            var row = rig.Vm.CronRows[0];
            Assert.Equal("Pause", row.PauseResumeLabel);
            rig.Reply = _ => Json(HttpStatusCode.OK, """{"id":"d1","name":"Default job","prompt":"p","state":"paused","enabled":false,"paused_reason":"manual"}""");
            rig.Vm.RunCronActionAsync(row, "pause").GetAwaiter().GetResult();
            Assert.True(row.Job.Paused);
            Assert.Equal("Resume", row.PauseResumeLabel);
            Assert.Equal("Paused", row.NextRun);
            Assert.Contains("manual", row.Notes);
            Assert.Equal("/api/cron/jobs/d1/pause?profile=default", rig.Seen.Last().PathAndQuery);
            Assert.Empty(rig.Asked);       // reversible: no confirmation
        });
    }

    [Fact]
    public void A_run_already_in_progress_is_reported_and_the_row_is_unchanged()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.RefreshCronAsync().GetAwaiter().GetResult();
            var row = rig.Vm.CronRows[0];
            var before = row.Job;
            rig.Reply = _ => Json(HttpStatusCode.Conflict, """{"detail":"Job is already running"}""");
            rig.Vm.RunCronActionAsync(row, "run").GetAwaiter().GetResult();
            Assert.Equal("Job is already running", rig.Vm.CronStatus);
            Assert.Same(before, row.Job);
            Assert.False(row.Busy);
        });
    }

    [Fact]
    public void A_job_removed_elsewhere_disappears_and_the_list_refreshes()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.RefreshCronAsync().GetAwaiter().GetResult();
            var row = rig.Vm.CronRows[0];
            rig.Reply = r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, "[]") : Json(HttpStatusCode.NotFound, "{}");
            rig.Vm.RunCronActionAsync(row, "pause").GetAwaiter().GetResult();
            Assert.DoesNotContain(row, rig.Vm.CronRows);
        });
    }

    [Fact]
    public void Create_posts_to_the_chosen_selected_profile_and_refuses_an_unselected_one()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.EditCron = _ => Task.FromResult<CronEditorResult?>(new("Nightly", "do it", "daily at 02:00", "local", "work"));
            rig.Reply = _ => Json(HttpStatusCode.OK, """{"id":"new1","name":"Nightly","prompt":"do it","state":"scheduled"}""");
            rig.Vm.NewCronJobAsync().GetAwaiter().GetResult();
            var s = rig.Seen.Single();
            Assert.Equal(HttpMethod.Post, s.Method);
            Assert.Equal("/api/cron/jobs?profile=work", s.PathAndQuery);
            Assert.Contains("\"schedule\":\"daily at 02:00\"", s.Body);
            Assert.Empty(rig.Vm.CronRows);                       // created in "work"; the list shows "default"
            Assert.Contains("profile work", rig.Vm.CronStatus);

            rig.Seen.Clear();
            rig.Vm.EditCron = _ => Task.FromResult<CronEditorResult?>(new("x", "p", "every 1h", "local", "private"));
            rig.Vm.NewCronJobAsync().GetAwaiter().GetResult();
            Assert.Empty(rig.Seen);
        });
    }

    [Fact]
    public void A_gateway_rejection_of_the_schedule_is_shown_in_the_gateways_words_and_nothing_is_added()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.EditCron = _ => Task.FromResult<CronEditorResult?>(new("n", "p", "whenever", "local", "default"));
            rig.Reply = _ => Json(HttpStatusCode.BadRequest, """{"detail":"Could not parse schedule \"whenever\""}""");
            rig.Vm.NewCronJobAsync().GetAwaiter().GetResult();
            Assert.Equal("Could not parse schedule \"whenever\"", rig.Vm.CronStatus);
            Assert.Empty(rig.Vm.CronRows);
        });
    }

    [Fact]
    public void Edit_sends_only_what_changed_and_nothing_at_all_when_nothing_did()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.RefreshCronAsync().GetAwaiter().GetResult();
            var row = rig.Vm.CronRows[0];
            rig.Seen.Clear();

            rig.Vm.EditCron = req => Task.FromResult<CronEditorResult?>(new(req.Existing!.Name, req.Existing.Prompt, req.Existing.EditableSchedule, req.Existing.Deliver ?? "local", req.Existing.Profile));
            rig.Vm.RunCronActionAsync(row, "edit").GetAwaiter().GetResult();
            Assert.Empty(rig.Seen);
            Assert.Equal("Nothing changed.", rig.Vm.CronStatus);

            rig.Vm.EditCron = req => Task.FromResult<CronEditorResult?>(new("Renamed", req.Existing!.Prompt, "every 2h", "local", req.Existing.Profile));
            rig.Reply = _ => Json(HttpStatusCode.OK, """{"id":"d1","name":"Renamed","prompt":"p","schedule_display":"every 2h","state":"scheduled","profile":"default"}""");
            rig.Vm.RunCronActionAsync(row, "edit").GetAwaiter().GetResult();
            var s = rig.Seen.Single();
            Assert.Equal(HttpMethod.Put, s.Method);
            Assert.Equal("/api/cron/jobs/d1?profile=default", s.PathAndQuery);
            Assert.Equal("""{"updates":{"name":"Renamed","schedule":"every 2h"}}""", s.Body);
            Assert.Equal("Renamed", row.Title);
        });
    }

    [Fact]
    public void A_gateway_without_cron_routes_says_so()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Reply = _ => Json(HttpStatusCode.NotFound, """{"detail":"Not Found"}""");
            rig.Vm.RefreshCronAsync().GetAwaiter().GetResult();
            Assert.Contains("does not offer scheduled jobs", rig.Vm.CronStatus);
            Assert.Empty(rig.Vm.CronRows);
        });
    }

    [Fact]
    public void Simple_mode_hides_the_page_entry_and_leaves_the_page_if_it_was_open()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.ApplyExperience(true);
            Assert.Equal(Visibility.Visible, rig.Vm.ScheduledNavVisibility);
            rig.Vm.Nav = MainNav.Scheduled;
            Assert.Equal(Visibility.Visible, rig.Vm.ScheduledVisibility);

            rig.Vm.IsAdvanced = false;
            Assert.Equal(Visibility.Collapsed, rig.Vm.ScheduledNavVisibility);
            Assert.Equal(MainNav.Chats, rig.Vm.Nav);
            Assert.Equal(Visibility.Collapsed, rig.Vm.ScheduledVisibility);
        });
    }

    [Fact]
    public void The_palette_offers_scheduled_jobs_only_in_advanced_mode()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.ApplyExperience(true);
            rig.Vm.OpenPaletteCommand!.Execute(null);
            rig.Vm.PaletteQuery = "scheduled";
            Assert.Contains(rig.Vm.PaletteResults, r => r.Title == "Open Scheduled jobs");
            rig.Vm.ClosePaletteCommand!.Execute(null);

            rig.Vm.IsAdvanced = false;
            rig.Vm.OpenPaletteCommand.Execute(null);
            rig.Vm.PaletteQuery = "scheduled";
            Assert.DoesNotContain(rig.Vm.PaletteResults, r => r.Title == "Open Scheduled jobs");
        });
    }
}

file sealed class MemPrefs22 : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
