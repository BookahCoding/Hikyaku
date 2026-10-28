using System.Net;
using System.Text;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 22 — scheduled jobs. Wire contract: BookahHermes CronApi.kt @ 5f00986b (the dashboard cron routes Hermes Desktop's
/// Cron page uses). Fixtures only; no live gateway was contacted.
/// </summary>
public class Checkpoint22CronTests
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

    private static (CronApi Api, List<Seen> Seen, DashboardAuth Auth) Make(Func<HttpRequestMessage, HttpResponseMessage> reply)
    {
        var seen = new List<Seen>();
        var auth = new DashboardAuth(new Uri("https://hermes.example/"), new Stub(seen, reply));
        return (new CronApi(auth), seen, auth);
    }

    private const string RealShapeList = """
    [
      {"id":"a1b2c3","name":"[bot:Mika] Morning inbox","prompt":"Summarize my inbox","schedule_display":"every 2h",
       "state":"scheduled","enabled":true,"next_run_at":"2026-10-26T18:00:00+00:00","last_run_at":"2026-10-26T16:00:00+00:00",
       "last_status":"ok","profile":"default","deliver":"local",
       "schedule":{"kind":"interval","minutes":120}},
      {"id":4242,"name":"","prompt":"Check the backups\nand report","schedule_display":"0 9 * * *","state":"paused","enabled":"false",
       "next_run_at":null,"last_run_at":1792000000.5,"last_status":"delivery_failed","last_delivery_error":"telegram: chat not found",
       "last_fire_error":{"at":"2026-10-25T09:00:00+00:00","detail":"runner busy"},"paused_reason":"too many failures","profile":"work",
       "schedule":{"kind":"cron","expr":"0 9 * * *"}},
      {"id":"once1","name":"One shot","prompt":"x","schedule_display":"once at 2026-11-01 09:00","state":"scheduled","enabled":true,
       "schedule":{"kind":"once","run_at":"2026-11-01T09:00:00+00:00"},"last_fire_error":"plain string"},
      {"id":"legacy","name":"Legacy","prompt":"x","schedule_display":"weekdays at 9am","schedule":"weekdays at 9am"},
      {"name":"no id is dropped"},
      "garbage"
    ]
    """;

    [Fact]
    public async Task Lists_all_profiles_and_parses_real_shapes_tolerantly()
    {
        var (api, seen, auth) = Make(_ => Json(HttpStatusCode.OK, RealShapeList));
        using var _a = auth;
        var jobs = await api.JobsAsync("default", allProfiles: true, CancellationToken.None);

        Assert.Equal("/api/cron/jobs?profile=all", seen.Single().PathAndQuery);
        Assert.Equal(4, jobs.Count); // the id-less object and the string are dropped

        var routine = jobs[0];
        Assert.True(routine.IsRoutine);
        Assert.Equal("Mika", routine.RoutineBot);
        Assert.Equal("Morning inbox", routine.DisplayName);
        Assert.Equal("every 120m", routine.EditableSchedule);
        Assert.Equal(DateTimeOffset.Parse("2026-10-26T18:00:00+00:00"), routine.NextRunAt);

        var paused = jobs[1];
        Assert.Equal("4242", paused.Id);           // numeric id
        Assert.False(paused.Enabled);              // "false" string
        Assert.True(paused.Paused);
        Assert.Equal("Check the backups", paused.DisplayName); // name blank -> first prompt line
        Assert.NotNull(paused.LastRunAt);          // float epoch seconds
        Assert.True(paused.DeliveryFailed);
        Assert.True(paused.MissedFire);
        Assert.Equal("runner busy", paused.LastFireError);
        Assert.Equal("0 9 * * *", paused.EditableSchedule);
        Assert.Equal("work", paused.Profile);

        Assert.Equal("", jobs[2].EditableSchedule);            // one-shot has no editable form
        Assert.Equal("plain string", jobs[2].LastFireError);   // string-shaped fire error
        Assert.Equal("weekdays at 9am", jobs[3].EditableSchedule); // legacy plain-string schedule
    }

    [Fact]
    public async Task One_profile_listing_scopes_to_that_profile()
    {
        var (api, seen, auth) = Make(_ => Json(HttpStatusCode.OK, """{"jobs":[]}"""));
        using var _a = auth;
        await api.JobsAsync("my profile", allProfiles: false, CancellationToken.None);
        Assert.Equal("/api/cron/jobs?profile=my%20profile", seen.Single().PathAndQuery);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("trigger")]
    public async Task Actions_go_to_the_owning_profile_not_the_active_one(string verb)
    {
        var (api, seen, auth) = Make(_ => Json(HttpStatusCode.OK, """{"id":"j/1","name":"n","prompt":"p","state":"paused","profile":"work"}"""));
        using var _a = auth;
        var job = CronJob.FromJson(System.Text.Json.JsonDocument.Parse("""{"id":"j/1","name":"n","prompt":"p","profile":"work"}""").RootElement)!;
        _ = verb switch
        {
            "pause" => await api.PauseAsync(job, CancellationToken.None),
            "resume" => await api.ResumeAsync(job, CancellationToken.None),
            _ => await api.TriggerAsync(job, CancellationToken.None)
        };
        var s = seen.Single();
        Assert.Equal(HttpMethod.Post, s.Method);
        Assert.Equal($"/api/cron/jobs/j%2F1/{verb}?profile=work", s.PathAndQuery);
    }

    [Fact]
    public async Task Create_posts_the_draft_to_the_chosen_profile()
    {
        var (api, seen, auth) = Make(_ => Json(HttpStatusCode.OK, """{"id":"new1","name":"Daily","prompt":"p","state":"scheduled"}"""));
        using var _a = auth;
        var made = await api.CreateAsync(new CronJobDraft("Daily", "do it", "daily at 09:00", "local"), "work", CancellationToken.None);
        var s = seen.Single();
        Assert.Equal(HttpMethod.Post, s.Method);
        Assert.Equal("/api/cron/jobs?profile=work", s.PathAndQuery);
        using var body = System.Text.Json.JsonDocument.Parse(s.Body);
        Assert.Equal("daily at 09:00", body.RootElement.GetProperty("schedule").GetString());
        Assert.Equal("do it", body.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("local", body.RootElement.GetProperty("deliver").GetString());
        Assert.Equal("new1", made.Id);
    }

    [Fact]
    public async Task Update_sends_only_the_changed_fields_wrapped_in_updates()
    {
        var (api, seen, auth) = Make(_ => Json(HttpStatusCode.OK, """{"id":"j1","name":"n2","prompt":"p","state":"scheduled","profile":"work"}"""));
        using var _a = auth;
        var job = CronJob.FromJson(System.Text.Json.JsonDocument.Parse("""{"id":"j1","name":"n","prompt":"p","profile":"work"}""").RootElement)!;
        await api.UpdateAsync(job, new Dictionary<string, string> { ["name"] = "n2" }, CancellationToken.None);
        var s = seen.Single();
        Assert.Equal(HttpMethod.Put, s.Method);
        Assert.Equal("/api/cron/jobs/j1?profile=work", s.PathAndQuery);
        Assert.Equal("""{"updates":{"name":"n2"}}""", s.Body);
        await Assert.ThrowsAsync<ArgumentException>(() => api.UpdateAsync(job, new Dictionary<string, string>(), CancellationToken.None));
    }

    [Fact]
    public async Task Delete_goes_to_the_owning_profile()
    {
        var (api, seen, auth) = Make(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var _a = auth;
        var job = CronJob.FromJson(System.Text.Json.JsonDocument.Parse("""{"id":"j1","profile":"work"}""").RootElement)!;
        await api.DeleteAsync(job, CancellationToken.None);
        Assert.Equal(HttpMethod.Delete, seen.Single().Method);
        Assert.Equal("/api/cron/jobs/j1?profile=work", seen.Single().PathAndQuery);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CronFailure.Auth)]
    [InlineData(HttpStatusCode.Forbidden, CronFailure.Auth)]
    [InlineData(HttpStatusCode.Conflict, CronFailure.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, CronFailure.Invalid)]
    [InlineData(HttpStatusCode.NotFound, CronFailure.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, CronFailure.Failed)]
    public async Task Job_errors_are_typed_and_carry_the_gateways_reason(HttpStatusCode code, CronFailure expected)
    {
        var (api, _, auth) = Make(_ => Json(code, """{"detail":"Could not parse schedule \"blah\""}"""));
        using var _a = auth;
        var job = CronJob.FromJson(System.Text.Json.JsonDocument.Parse("""{"id":"j1"}""").RootElement)!;
        var ex = await Assert.ThrowsAsync<CronException>(() => api.TriggerAsync(job, CancellationToken.None));
        Assert.Equal(expected, ex.Failure);
        Assert.Contains("Could not parse schedule", ex.Message);
    }

    [Fact]
    public async Task A_missing_collection_means_the_gateway_has_no_cron_routes()
    {
        var (api, _, auth) = Make(_ => Json(HttpStatusCode.NotFound, """{"detail":"Not Found"}"""));
        using var _a = auth;
        var ex = await Assert.ThrowsAsync<CronException>(() => api.JobsAsync("p", true, CancellationToken.None));
        Assert.Equal(CronFailure.Unsupported, ex.Failure);
    }

    [Fact]
    public async Task Validation_error_lists_use_the_first_message_and_error_text_is_sanitized()
    {
        var (api, _, auth) = Make(_ => Json((HttpStatusCode)422, """{"detail":[{"msg":"field required\u0007"}]}"""));
        using var _a = auth;
        var ex = await Assert.ThrowsAsync<CronException>(() =>
            api.CreateAsync(new CronJobDraft("n", "p", "every 1h"), null, CancellationToken.None));
        Assert.Equal(CronFailure.Invalid, ex.Failure);
        Assert.Equal("field required", ex.Message);
    }

    [Fact]
    public async Task Oversized_response_is_refused_not_buffered()
    {
        var big = new string('x', CronApi.MaxResponseBytes + 10);
        var (api, _, auth) = Make(_ => Json(HttpStatusCode.OK, "[\"" + big + "\"]"));
        using var _a = auth;
        var ex = await Assert.ThrowsAsync<CronException>(() => api.JobsAsync(null, true, CancellationToken.None));
        Assert.Equal(CronFailure.Failed, ex.Failure);
    }

    [Fact]
    public async Task Delivery_targets_always_offer_local_first()
    {
        var (api, seen, auth) = Make(_ => Json(HttpStatusCode.OK,
            """{"targets":[{"id":"telegram","name":"Telegram","home_target_set":false},{"id":7},{"name":"no id"}]}"""));
        using var _a = auth;
        var t = await api.DeliveryTargetsAsync("work", CancellationToken.None);
        Assert.Equal("/api/cron/delivery-targets?profile=work", seen.Single().PathAndQuery);
        Assert.Equal(["local", "telegram", "7"], t.Select(x => x.Id));
        Assert.False(t[1].HomeTargetSet);
    }

    // ---- schedule picker --------------------------------------------------------------------------

    [Theory]
    [InlineData(ScheduleKind.EveryMinutes, 30, 9, 0, DayOfWeek.Monday, "every 30m")]
    [InlineData(ScheduleKind.EveryHours, 2, 9, 0, DayOfWeek.Monday, "every 2h")]
    [InlineData(ScheduleKind.InMinutes, 45, 9, 0, DayOfWeek.Monday, "in 45m")]
    [InlineData(ScheduleKind.InHours, 3, 9, 0, DayOfWeek.Monday, "in 3h")]
    [InlineData(ScheduleKind.Daily, 1, 7, 5, DayOfWeek.Monday, "daily at 07:05")]
    [InlineData(ScheduleKind.Weekdays, 1, 9, 30, DayOfWeek.Monday, "weekdays at 09:30")]
    [InlineData(ScheduleKind.Weekly, 1, 14, 0, DayOfWeek.Friday, "every friday 14:00")]
    public void Picker_writes_the_documented_schedule_forms(ScheduleKind kind, int n, int h, int m, DayOfWeek d, string expected)
    {
        var spec = new ScheduleSpec(kind, n, h, m, d);
        Assert.Null(spec.Problem());
        Assert.Equal(expected, spec.ToText());
        // and what it writes opens back in the same picker position
        var back = ScheduleSpec.FromText(expected);
        Assert.Equal(kind, back.Kind);
        Assert.Equal(expected, back.ToText());
    }

    [Theory]
    [InlineData("0 9 * * *")]
    [InlineData("weekdays at 9am")]
    [InlineData("2026-03-15T09:00:00")]
    [InlineData("every monday, wednesday 9am")]
    [InlineData("daily at 25:00")] // not a picker-writable time
    public void Anything_the_picker_cannot_write_stays_in_advanced_unchanged(string raw)
    {
        var spec = ScheduleSpec.FromText(raw);
        Assert.Equal(ScheduleKind.Advanced, spec.Kind);
        Assert.Equal(raw, spec.ToText());
    }

    [Theory]
    [InlineData(ScheduleKind.EveryMinutes, 0, "")]
    [InlineData(ScheduleKind.EveryHours, 100000, "")]
    [InlineData(ScheduleKind.Advanced, 1, "")]
    [InlineData(ScheduleKind.Advanced, 1, "every\n1h")]
    public void Picker_rejects_unusable_input(ScheduleKind kind, int n, string raw)
        => Assert.NotNull(new ScheduleSpec(kind, n, Raw: raw).Problem());

    [Fact]
    public void Draft_rejects_empty_or_oversized_input_and_nothing_else()
    {
        Assert.NotNull(new CronJobDraft("n", "  ", "every 1h").Problem());
        Assert.NotNull(new CronJobDraft("n", "p", "").Problem());
        Assert.NotNull(new CronJobDraft(new string('n', 201), "p", "every 1h").Problem());
        Assert.NotNull(new CronJobDraft("n", new string('p', CronJobDraft.MaxPromptChars + 1), "every 1h").Problem());
        Assert.Null(new CronJobDraft("", "p", "0 9 * * *").Problem()); // a blank name is fine; the gateway names it
    }
}
