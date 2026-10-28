using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>One day's bar in the usage chart. Height is 0–100 relative to the busiest day.</summary>
public sealed record UsageBarVM(double BarHeight, string Tooltip, string Label);

/// <summary>A "name … value" line (a model, a tool, a skill).</summary>
public sealed record InsightLineVM(string Title, string Value);

/// <summary>
/// Insights (Hermes Desktop's usage analytics): tokens, sessions, cost, cache share, a daily chart, and the top models, tools and
/// skills for the active profile over 7 / 30 / 90 days. Read-only; one request per refresh. Cost is labelled "billed" only when
/// the provider reported it, otherwise "estimated".
/// </summary>
public sealed partial class MainViewModel
{
    private Func<InsightsApi?> _insightsApi = () => null;
    private int _insightsDays = 30;
    private bool _insightsLoading;
    private int _insightsRefreshId;
    private string _insightsStatus = "";
    private string _insightsProfile = "";
    private string _insightsSummary = "";

    public ObservableCollection<UsageBarVM> UsageBars { get; } = new();
    public ObservableCollection<InsightLineVM> ModelLines { get; } = new();
    public ObservableCollection<InsightLineVM> ToolLines { get; } = new();
    public ObservableCollection<InsightLineVM> SkillLines { get; } = new();

    public ICommand? ShowInsightsCommand { get; private set; }
    public ICommand? RefreshInsightsCommand { get; private set; }
    public ICommand? SetInsightsPeriodCommand { get; private set; }

    /// <summary>Today in the client's clock, for the chart axis. Replaceable in tests.</summary>
    public Func<DateOnly> InsightsToday { get; set; } = () => DateOnly.FromDateTime(DateTime.Now);

    public void AttachInsights(Func<InsightsApi?> api) => _insightsApi = api;

    public int InsightsDays => _insightsDays;
    public string InsightsStatus { get => _insightsStatus; private set { Set(ref _insightsStatus, value); Raise(nameof(InsightsStatusVisibility)); } }
    public Visibility InsightsStatusVisibility => _insightsStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string InsightsSummary { get => _insightsSummary; private set => Set(ref _insightsSummary, value); }
    public string InsightsProfileLabel => _insightsProfile.Length > 0 ? $"Profile: {_insightsProfile} · last {_insightsDays} days" : "";
    public bool InsightsLoading { get => _insightsLoading; private set { Set(ref _insightsLoading, value); (RefreshInsightsCommand as RelayCommand)?.Refresh(); } }
    public Visibility InsightsVisibility =>
        ChatShellVisibility == Visibility.Visible && Nav == MainNav.Insights ? Visibility.Visible : Visibility.Collapsed;

    private void InitInsights()
    {
        ShowInsightsCommand = new RelayCommand(_ => { Nav = MainNav.Insights; _ = RefreshInsightsAsync(); });
        RefreshInsightsCommand = new RelayCommand(_ => _ = RefreshInsightsAsync(), _ => !_insightsLoading);
        SetInsightsPeriodCommand = new RelayCommand(p =>
        {
            if (!int.TryParse(p?.ToString(), out var days) || days is not (7 or 30 or 90)) return;
            _insightsDays = days;
            Raise(nameof(InsightsDays));
            _ = RefreshInsightsAsync();
        });
    }

    public async Task RefreshInsightsAsync()
    {
        if (_insightsApi() is not { } api) { InsightsStatus = "Connect to a gateway to see its usage."; return; }
        var profile = ActiveProfileId();
        if (profile is null || !CronAllowed(profile)) { InsightsStatus = "Choose a profile first."; return; }
        var id = ++_insightsRefreshId;
        _insightsProfile = profile;
        Raise(nameof(InsightsProfileLabel));
        InsightsLoading = true;
        InsightsStatus = "";
        try
        {
            var report = await api.UsageAsync(profile, _insightsDays, CancellationToken.None).ConfigureAwait(true);
            if (id != _insightsRefreshId) return;
            Apply(report);
        }
        catch (RestException ex)
        {
            if (id == _insightsRefreshId)
                InsightsStatus = ex.Failure switch
                {
                    RestFailure.Auth => "Sign in again to see usage.",
                    RestFailure.Unsupported => "This gateway does not offer usage analytics through the dashboard.",
                    _ => ex.Message
                };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        {
            if (id == _insightsRefreshId) InsightsStatus = "Couldn't reach the gateway: " + ex.Message;
        }
        finally { if (id == _insightsRefreshId) InsightsLoading = false; }
    }

    private void Apply(UsageReport r)
    {
        var t = r.Totals;
        InsightsSummaryParts(t);
        var series = r.DailySeries(InsightsToday());
        var max = series.Max(d => d.Tokens);
        UsageBars.Clear();
        foreach (var d in series)
            UsageBars.Add(new UsageBarVM(max > 0 ? Math.Max(d.Tokens > 0 ? 2 : 0, 100.0 * d.Tokens / max) : 0,
                $"{d.Day:ddd d MMM}: {UsageFormat.Tokens(d.Tokens)} tokens, {d.Sessions} chat(s), {UsageFormat.Cost(d.Cost)}", d.Day.ToString("d/M")));
        Fill(ModelLines, r.ByModel.Take(8).Select(m => new InsightLineVM(m.Model, $"{UsageFormat.Tokens(m.Tokens)} tokens · {m.Sessions} chat(s)")));
        Fill(ToolLines, r.Tools.Take(8).Select(x => new InsightLineVM(x.Tool, x.Count + "×")));
        Fill(SkillLines, r.TopSkills.Take(8).Select(x => new InsightLineVM(x.Skill, x.Count + "×")));
        if (t.Sessions == 0 && t.Input + t.Output == 0 && r.Daily.Count == 0) InsightsStatus = "No usage recorded in this period.";
    }

    private void InsightsSummaryParts(UsageTotals t)
    {
        var parts = new List<string>
        {
            $"{t.Sessions} chat(s) · {t.ApiCalls} model calls",
            $"{UsageFormat.Tokens(t.Input)} in · {UsageFormat.Tokens(t.Output)} out",
            $"{UsageFormat.Cost(t.Cost)} {(t.CostIsBilled ? "billed" : "estimated")}"
        };
        if (t.CachePercent is { } c) parts.Add($"{c}% of prompt tokens from cache");
        InsightsSummary = string.Join("\n", parts);
    }

    private static void Fill(ObservableCollection<InsightLineVM> target, IEnumerable<InsightLineVM> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }
}
