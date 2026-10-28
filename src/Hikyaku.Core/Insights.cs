using System.Globalization;
using System.Text.Json;

namespace Hikyaku.Core;

/// <summary>One day of use (<c>YYYY-MM-DD</c> in the gateway's time zone); days without any are left out by the gateway.</summary>
public sealed record UsageDay(DateOnly Day, long Input, long Output, long CacheRead, double EstimatedCost, double ActualCost, int Sessions, int ApiCalls)
{
    public long Tokens => Input + Output;
    /// <summary>What the provider billed when it says so, otherwise the list-price estimate.</summary>
    public double Cost => ActualCost > 0 ? ActualCost : EstimatedCost;
}

public sealed record ModelUsage(string Model, long Input, long Output, double EstimatedCost, int Sessions, int ApiCalls)
{
    public long Tokens => Input + Output;
}

public sealed record ToolUsage(string Tool, int Count);
public sealed record SkillUsage(string Skill, int Count);

public sealed record UsageTotals(long Input, long Output, long CacheRead, double EstimatedCost, double ActualCost, int Sessions, int ApiCalls)
{
    public double Cost => ActualCost > 0 ? ActualCost : EstimatedCost;
    /// <summary>True when the cost is the provider's billed figure rather than a list-price estimate.</summary>
    public bool CostIsBilled => ActualCost > 0;
    /// <summary>Share of prompt tokens served from the provider's cache, 0–100; null with no input.</summary>
    public int? CachePercent => Input + CacheRead > 0 ? (int)(CacheRead * 100 / (Input + CacheRead)) : null;
}

/// <summary><c>GET /api/analytics/usage</c>, the data behind Hermes Desktop's Insights.</summary>
public sealed record UsageReport(
    IReadOnlyList<UsageDay> Daily,
    IReadOnlyList<ModelUsage> ByModel,
    UsageTotals Totals,
    int PeriodDays,
    IReadOnlyList<SkillUsage> TopSkills,
    IReadOnlyList<ToolUsage> Tools)
{
    /// <summary>
    /// The period day by day ending on <paramref name="today"/>, with the days the gateway left out (no use) as zero rows so a chart's
    /// axis stays even. A day the gateway reported is never cut off, even one ahead of <paramref name="today"/> (time-zone skew).
    /// </summary>
    public IReadOnlyList<UsageDay> DailySeries(DateOnly today)
    {
        var byDay = Daily.GroupBy(d => d.Day).ToDictionary(g => g.Key, g => g.First());
        var days = Math.Clamp(PeriodDays, 1, 365);
        var end = today;
        if (byDay.Count > 0)
        {
            var last = byDay.Keys.Max();
            var first = byDay.Keys.Min();
            if (last > end) end = last;
            if (first < end.AddDays(-(days - 1))) end = new[] { first.AddDays(days - 1), last }.Max();
        }
        var series = new List<UsageDay>(days);
        for (var i = days - 1; i >= 0; i--)
        {
            var d = end.AddDays(-i);
            series.Add(byDay.TryGetValue(d, out var u) ? u : new UsageDay(d, 0, 0, 0, 0, 0, 0, 0));
        }
        return series;
    }
}

/// <summary>Read-only usage analytics (BookahHermes <c>InsightsApi.kt</c> @ 5f00986b). The profile is named on every call.</summary>
public sealed class InsightsApi(DashboardAuth auth)
{
    public const int MaxRows = 200;

    public async Task<UsageReport> UsageAsync(string profile, int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 365);
        using var doc = await DashboardRest.SendAsync(auth, HttpMethod.Get,
            "api/analytics/usage?days=" + days.ToString(CultureInfo.InvariantCulture), profile, null, true, ct).ConfigureAwait(false);
        return Parse(doc.RootElement, days);
    }

    public static UsageReport Parse(JsonElement r, int requestedDays)
    {
        var daily = new List<UsageDay>();
        foreach (var e in Items(r, "daily"))
        {
            if (DashboardRest.Str(e, "day") is not { Length: >= 10 } s || !DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            daily.Add(new UsageDay(day, Long(e, "input_tokens"), Long(e, "output_tokens"), Long(e, "cache_read_tokens"),
                Dbl(e, "estimated_cost"), Dbl(e, "actual_cost"), (int)Long(e, "sessions"), (int)Long(e, "api_calls")));
            if (daily.Count >= MaxRows) break;
        }
        var models = new List<ModelUsage>();
        foreach (var e in Items(r, "by_model"))
            if (DashboardRest.Str(e, "model") is { Length: > 0 } m)
                models.Add(new ModelUsage(m, Long(e, "input_tokens"), Long(e, "output_tokens"), Dbl(e, "estimated_cost"), (int)Long(e, "sessions"), (int)Long(e, "api_calls")));
        var tools = new List<ToolUsage>();
        foreach (var e in Items(r, "tools"))
            if (DashboardRest.Str(e, "tool") is { Length: > 0 } t) tools.Add(new ToolUsage(t, (int)Long(e, "count")));
        var skills = new List<SkillUsage>();
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("skills", out var sb))
            foreach (var e in Items(sb, "top_skills"))
                if (DashboardRest.Str(e, "skill") is { Length: > 0 } sk) skills.Add(new SkillUsage(sk, (int)Long(e, "total_count")));

        UsageTotals totals;
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("totals", out var tt) && tt.ValueKind == JsonValueKind.Object)
            totals = new UsageTotals(Long(tt, "total_input"), Long(tt, "total_output"), Long(tt, "total_cache_read"),
                Dbl(tt, "total_estimated_cost"), Dbl(tt, "total_actual_cost"), (int)Long(tt, "total_sessions"), (int)Long(tt, "total_api_calls"));
        else   // a gateway that omits totals: add the days up rather than show zeros
            totals = new UsageTotals(daily.Sum(d => d.Input), daily.Sum(d => d.Output), daily.Sum(d => d.CacheRead),
                daily.Sum(d => d.EstimatedCost), daily.Sum(d => d.ActualCost), daily.Sum(d => d.Sessions), daily.Sum(d => d.ApiCalls));
        var period = r.ValueKind == JsonValueKind.Object && DashboardRest.Int(r, "period_days") is > 0 and var p ? p : requestedDays;
        return new UsageReport(daily, models.OrderByDescending(m => m.Tokens).ToList(), totals, period,
            skills.OrderByDescending(s => s.Count).ToList(), tools.OrderByDescending(t => t.Count).ToList());
    }

    private static IEnumerable<JsonElement> Items(JsonElement root, string key)
    {
        var arr = root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array ? a : default;
        return arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray() : [];
    }

    private static long Long(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt64(out var n) ? n : (long)Math.Max(0, v.GetDouble());
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? Math.Max(0, p) : 0;
    }

    private static double Dbl(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return Math.Max(0, v.GetDouble());
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? Math.Max(0, p) : 0;
    }
}

public static class UsageFormat
{
    /// <summary>1,234 → "1.2k", 1,234,567 → "1.2M".</summary>
    public static string Tokens(long n) => n switch
    {
        >= 1_000_000_000 => (n / 1e9).ToString("0.#", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (n / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (n / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => n.ToString(CultureInfo.InvariantCulture)
    };

    public static string Cost(double usd) => "$" + usd.ToString(usd > 0 && usd < 0.01 ? "0.000" : "0.00", CultureInfo.InvariantCulture);
}
