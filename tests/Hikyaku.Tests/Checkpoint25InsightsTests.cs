using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 25 — Insights (usage analytics). Wire contract: BookahHermes InsightsApi.kt @ 5f00986b. Fixtures only.</summary>
public class Checkpoint25InsightsTests
{
    private const string Real = """
    {"period_days":7,
     "daily":[{"day":"2026-10-24","input_tokens":1000,"output_tokens":500,"cache_read_tokens":3000,"estimated_cost":0.5,"actual_cost":0,"sessions":2,"api_calls":9},
              {"day":"2026-10-26T00:00:00","input_tokens":"200","output_tokens":100.0,"estimated_cost":"0.1","actual_cost":0.25,"sessions":1,"api_calls":3},
              {"day":"garbage"},{"input_tokens":5}],
     "by_model":[{"model":"small","input_tokens":10,"output_tokens":10},{"model":"big","input_tokens":900,"output_tokens":100,"estimated_cost":0.4,"sessions":2,"api_calls":4},{"input_tokens":1}],
     "totals":{"total_input":1200,"total_output":600,"total_cache_read":3000,"total_estimated_cost":0.6,"total_actual_cost":0,"total_sessions":3,"total_api_calls":12},
     "skills":{"top_skills":[{"skill":"notes","total_count":2},{"skill":"web","total_count":9}]},
     "tools":[{"tool":"read","count":3},{"tool":"bash","count":10}]}
    """;

    private sealed class Stub(List<string> seen, string body, HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            seen.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Usage_asks_for_the_clamped_period_and_the_profile()
    {
        var seen = new List<string>();
        using var auth = new DashboardAuth(new Uri("https://hermes.example/"), new Stub(seen, Real));
        await new InsightsApi(auth).UsageAsync("work", 9999, CancellationToken.None);
        await new InsightsApi(auth).UsageAsync("work", 0, CancellationToken.None);
        Assert.Equal(["GET /api/analytics/usage?days=365&profile=work", "GET /api/analytics/usage?days=1&profile=work"], seen);
    }

    [Fact]
    public void A_real_shaped_report_parses_tolerantly()
    {
        var r = InsightsApi.Parse(JsonDocument.Parse(Real).RootElement, 30);
        Assert.Equal(7, r.PeriodDays);
        Assert.Equal(2, r.Daily.Count);                           // garbage days dropped; a datetime-ish day keeps its date
        Assert.Equal(new DateOnly(2026, 10, 26), r.Daily[1].Day);
        Assert.Equal(300, r.Daily[1].Tokens);                     // string and float numbers
        Assert.Equal(0.25, r.Daily[1].Cost);                      // billed beats estimated
        Assert.Equal(0.5, r.Daily[0].Cost);                       // estimate when nothing was billed
        Assert.Equal(["big", "small"], r.ByModel.Select(m => m.Model));   // by tokens
        Assert.Equal(["web", "notes"], r.TopSkills.Select(s => s.Skill));
        Assert.Equal(["bash", "read"], r.Tools.Select(t => t.Tool));
        Assert.Equal(71, r.Totals.CachePercent);                  // 3000 of (1200 + 3000) prompt tokens
    }

    [Fact]
    public void Cache_share_is_cache_over_input_plus_cache_and_absent_without_input()
    {
        Assert.Equal(71, new UsageTotals(1200, 0, 3000, 0, 0, 0, 0).CachePercent);
        Assert.Null(new UsageTotals(0, 5, 0, 0, 0, 0, 0).CachePercent);
    }

    [Fact]
    public void A_gateway_without_totals_gets_them_added_up_not_zeroed()
    {
        var r = InsightsApi.Parse(JsonDocument.Parse("""{"daily":[{"day":"2026-10-01","input_tokens":10,"output_tokens":5,"sessions":1},{"day":"2026-10-02","input_tokens":20,"sessions":2}]}""").RootElement, 14);
        Assert.Equal(30, r.Totals.Input);
        Assert.Equal(3, r.Totals.Sessions);
        Assert.Equal(14, r.PeriodDays);                           // the requested period when the report omits it
    }

    [Fact]
    public void An_empty_or_odd_report_is_an_empty_report()
    {
        foreach (var json in new[] { "{}", "[]", "\"x\"", "null" })
        {
            var r = InsightsApi.Parse(JsonDocument.Parse(json).RootElement, 30);
            Assert.Empty(r.Daily);
            Assert.Equal(0, r.Totals.Sessions);
        }
    }

    [Fact]
    public void The_daily_series_fills_missing_days_with_zero_rows_and_keeps_the_axis_even()
    {
        var r = InsightsApi.Parse(JsonDocument.Parse(Real).RootElement, 7);
        var s = r.DailySeries(new DateOnly(2026, 10, 26));
        Assert.Equal(7, s.Count);
        Assert.Equal(new DateOnly(2026, 10, 20), s[0].Day);
        Assert.Equal(new DateOnly(2026, 10, 26), s[^1].Day);
        Assert.Equal(0, s[0].Tokens);                             // a day with no use
        Assert.Equal(1500, s.Single(d => d.Day == new DateOnly(2026, 10, 24)).Tokens);
    }

    [Fact]
    public void A_day_ahead_of_the_clients_clock_is_never_cut_off()
    {
        var r = InsightsApi.Parse(JsonDocument.Parse("""{"period_days":3,"daily":[{"day":"2026-10-27","input_tokens":5}]}""").RootElement, 3);
        var s = r.DailySeries(new DateOnly(2026, 10, 26));       // the gateway's date is a day ahead
        Assert.Equal(3, s.Count);
        Assert.Equal(new DateOnly(2026, 10, 27), s[^1].Day);
        Assert.Equal(5, s[^1].Tokens);
    }

    [Fact]
    public async Task A_gateway_failure_is_a_typed_error()
    {
        using var auth = new DashboardAuth(new Uri("https://hermes.example/"), new Stub([], """{"detail":"nope"}""", HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<RestException>(() => new InsightsApi(auth).UsageAsync("p", 30, CancellationToken.None));
        Assert.Equal(RestFailure.Unsupported, ex.Failure);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1234, "1.2k")]
    [InlineData(1_234_567, "1.2M")]
    [InlineData(2_500_000_000, "2.5B")]
    public void Token_counts_are_short(long n, string expected) => Assert.Equal(expected, UsageFormat.Tokens(n));

    [Theory]
    [InlineData(0, "$0.00")]
    [InlineData(0.004, "$0.004")]
    [InlineData(1.5, "$1.50")]
    public void Costs_keep_sub_cent_amounts_visible(double usd, string expected) => Assert.Equal(expected, UsageFormat.Cost(usd));
}
