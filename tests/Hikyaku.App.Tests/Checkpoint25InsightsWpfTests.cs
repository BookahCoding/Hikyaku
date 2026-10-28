using System.Net;
using System.Net.Http;
using System.Text;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 25 — the Insights page through the real MainViewModel (fixture HTTP; no live gateway).</summary>
[Collection("StaMath")]
public class Checkpoint25InsightsWpfTests
{
    private const string Report = """
    {"period_days":3,"daily":[{"day":"2026-10-25","input_tokens":1000,"output_tokens":1000,"sessions":2},{"day":"2026-10-26","input_tokens":500,"output_tokens":500,"sessions":1,"actual_cost":0.5}],
     "by_model":[{"model":"big","input_tokens":1500,"output_tokens":1500,"sessions":3}],
     "totals":{"total_input":1500,"total_output":1500,"total_cache_read":1500,"total_estimated_cost":0.3,"total_actual_cost":0.5,"total_sessions":3,"total_api_calls":7},
     "skills":{"top_skills":[{"skill":"web","total_count":4}]},"tools":[{"tool":"bash","count":6}]}
    """;

    private sealed class Stub(List<string> seen, Func<HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            seen.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(reply());
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (MainViewModel Vm, List<string> Seen, DashboardAuth Auth, Func<HttpResponseMessage>[] Reply) Make()
    {
        var seen = new List<string>();
        var reply = new Func<HttpResponseMessage>[] { () => Json(HttpStatusCode.OK, Report) };
        var selection = new ProfileSelectionService(new MemPrefs25());
        selection.SetSelectedAsync(new[] { new ProfileKey("gw", "default") }).GetAwaiter().GetResult();
        var auth = new DashboardAuth(new Uri("https://hermes.example/"), new Stub(seen, () => reply[0]()));
        var vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
            () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
        vm.AttachCron(() => null, "gw");
        vm.AttachInsights(() => new InsightsApi(auth));
        vm.InsightsToday = () => new DateOnly(2026, 10, 26);
        return (vm, seen, auth, reply);
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
    public void Refresh_shows_totals_chart_and_top_lists_for_the_active_profile()
    {
        RunSta(() =>
        {
            var (vm, seen, auth, _) = Make();
            using var _a = auth;
            vm.RefreshInsightsAsync().GetAwaiter().GetResult();
            Assert.Equal("/api/analytics/usage?days=30&profile=default", seen.Single());
            Assert.Contains("3 chat(s) · 7 model calls", vm.InsightsSummary);
            Assert.Contains("$0.50 billed", vm.InsightsSummary);               // the provider's figure wins and is labelled so
            Assert.Contains("50% of prompt tokens from cache", vm.InsightsSummary);
            Assert.Equal(3, vm.UsageBars.Count);                                // the report's period (3 days), gaps filled
            Assert.Equal(0, vm.UsageBars[0].BarHeight);                         // 24 Oct: nothing
            Assert.Equal(100, vm.UsageBars[1].BarHeight);                       // busiest day
            Assert.Equal(50, vm.UsageBars[2].BarHeight);
            Assert.Equal("big", vm.ModelLines.Single().Title);
            Assert.Equal("bash", vm.ToolLines.Single().Title);
            Assert.Equal("web", vm.SkillLines.Single().Title);
            Assert.Equal("", vm.InsightsStatus);
        });
    }

    [Fact]
    public void Cost_is_called_estimated_unless_the_provider_billed_it()
    {
        RunSta(() =>
        {
            var (vm, _, auth, reply) = Make();
            using var _a = auth;
            reply[0] = () => Json(HttpStatusCode.OK, """{"totals":{"total_input":10,"total_estimated_cost":0.25,"total_actual_cost":0}}""");
            vm.RefreshInsightsAsync().GetAwaiter().GetResult();
            Assert.Contains("$0.25 estimated", vm.InsightsSummary);
            Assert.DoesNotContain("billed", vm.InsightsSummary);
        });
    }

    [Fact]
    public void Choosing_a_period_asks_for_it_and_ignores_other_values()
    {
        RunSta(() =>
        {
            var (vm, seen, auth, _) = Make();
            using var _a = auth;
            vm.SetInsightsPeriodCommand!.Execute("90");
            Assert.Equal(90, vm.InsightsDays);
            vm.SetInsightsPeriodCommand.Execute("12345");
            vm.SetInsightsPeriodCommand.Execute(null);
            Assert.Equal(90, vm.InsightsDays);
            Assert.EndsWith("days=90&profile=default", seen.Last());
        });
    }

    [Fact]
    public void An_empty_period_says_so_and_a_failure_keeps_the_last_numbers()
    {
        RunSta(() =>
        {
            var (vm, _, auth, reply) = Make();
            using var _a = auth;
            reply[0] = () => Json(HttpStatusCode.OK, "{}");
            vm.RefreshInsightsAsync().GetAwaiter().GetResult();
            Assert.Equal("No usage recorded in this period.", vm.InsightsStatus);

            reply[0] = () => Json(HttpStatusCode.OK, Report);
            vm.RefreshInsightsAsync().GetAwaiter().GetResult();
            var before = vm.InsightsSummary;
            reply[0] = () => Json(HttpStatusCode.InternalServerError, """{"detail":"db busy"}""");
            vm.RefreshInsightsAsync().GetAwaiter().GetResult();
            Assert.Equal("db busy", vm.InsightsStatus);
            Assert.Equal(before, vm.InsightsSummary);
            Assert.NotEmpty(vm.UsageBars);
        });
    }

    [Fact]
    public void A_gateway_without_analytics_and_no_connection_are_explained()
    {
        RunSta(() =>
        {
            var (vm, _, auth, reply) = Make();
            using var _a = auth;
            reply[0] = () => Json(HttpStatusCode.NotFound, "{}");
            vm.RefreshInsightsAsync().GetAwaiter().GetResult();
            Assert.Contains("does not offer usage analytics", vm.InsightsStatus);
            vm.AttachInsights(() => null);
            vm.RefreshInsightsAsync().GetAwaiter().GetResult();
            Assert.Contains("Connect to a gateway", vm.InsightsStatus);
        });
    }

    [Fact]
    public void Insights_is_in_the_palette_in_advanced_only_maintainer_2026_10_27()
    {
        RunSta(() =>
        {
            var (vm, _, auth, _) = Make();
            using var _a = auth;
            foreach (var advanced in new[] { true, false })
            {
                vm.ApplyExperience(advanced);
                vm.OpenPaletteCommand!.Execute(null);
                vm.PaletteQuery = "insights";
                Assert.Equal(advanced, vm.PaletteResults.Any(r => r.Id == "nav.insights"));
                vm.ClosePaletteCommand!.Execute(null);
            }
        });
    }
}

file sealed class MemPrefs25 : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
