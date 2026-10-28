using System.Net;
using System.Text;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 18a — paging stored history with an explicit chat profile (not the sidebar's active profile) and the profile gate.
/// Synthetic REST only.
/// </summary>
public class Checkpoint18HistoryPagingTests
{
    private sealed class Rest : HttpMessageHandler
    {
        public readonly List<string> Queries = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Queries.Add(r.RequestUri!.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"session_id":"s","messages":[{"id":5,"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json") });
        }
    }

    private static (SessionCatalog Catalog, Rest Http, ProfileSelectionService Selection) Make(params ProfileKey[] selected)
    {
        var rest = new Rest();
        var selection = new ProfileSelectionService(new MemPrefs());
        selection.SetSelectedAsync(selected).GetAwaiter().GetResult();
        var api = new SessionsApi(new DashboardAuth(new Uri("https://gw.example/"), rest));
        var catalog = new SessionCatalog(new NotificationGate(selection), () => api);
        return (catalog, rest, selection);
    }

    [Fact]
    public async Task A_page_is_read_with_the_chats_own_profile_offset_and_limit_even_when_the_sidebar_shows_another()
    {
        var a = new ProfileKey("gw", "alpha");
        var b = new ProfileKey("gw", "beta");
        var (catalog, rest, _) = Make(a, b);
        catalog.SetActiveProfile(a);                                  // sidebar shows alpha
        var page = await catalog.LoadMessagesPageAsync(b, "sess-1", offset: 120, limit: 60);
        Assert.Single(page.Messages);
        var q = System.Web.HttpUtility.ParseQueryString(Assert.Single(rest.Queries));
        Assert.Equal("beta", q["profile"]);                           // not "alpha"
        Assert.Equal("120", q["offset"]);
        Assert.Equal("60", q["limit"]);
        Assert.Equal("latest", q["order"]);
    }

    [Fact]
    public async Task A_deselected_profile_is_refused_before_any_request()
    {
        var a = new ProfileKey("gw", "alpha");
        var b = new ProfileKey("gw", "beta");
        var (catalog, rest, _) = Make(a);                              // beta is not selected
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.LoadMessagesPageAsync(b, "sess-1", 0, 60));
        Assert.Empty(rest.Queries);
    }

    [Fact]
    public async Task The_initial_load_can_name_the_chats_profile_too()
    {
        var a = new ProfileKey("gw", "alpha");
        var b = new ProfileKey("gw", "beta");
        var (catalog, rest, _) = Make(a, b);
        catalog.SetActiveProfile(a);
        await catalog.LoadMessagesAsync("sess-1", b);
        Assert.Equal("beta", System.Web.HttpUtility.ParseQueryString(Assert.Single(rest.Queries))["profile"]);
    }

    [Fact]
    public async Task Without_an_explicit_profile_the_active_one_is_still_used_as_before()
    {
        var a = new ProfileKey("gw", "alpha");
        var (catalog, rest, _) = Make(a);
        catalog.SetActiveProfile(a);
        await catalog.LoadMessagesAsync("sess-1");
        Assert.Equal("alpha", System.Web.HttpUtility.ParseQueryString(Assert.Single(rest.Queries))["profile"]);
    }

    [Theory]
    [InlineData(-5, 60, 0, 60)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 9999, 0, SessionsApi.HistoryPageMax)]
    public async Task Offset_and_limit_are_clamped_to_sane_bounds(int offset, int limit, int expectedOffset, int expectedLimit)
    {
        var a = new ProfileKey("gw", "alpha");
        var (catalog, rest, _) = Make(a);
        await catalog.LoadMessagesPageAsync(a, "sess-1", offset, limit);
        var q = System.Web.HttpUtility.ParseQueryString(Assert.Single(rest.Queries));
        Assert.Equal(expectedLimit.ToString(), q["limit"]);
        Assert.Equal(expectedOffset == 0 ? null : expectedOffset.ToString(), q["offset"]);
    }
}

file sealed class MemPrefs : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
