using System.Net;
using System.Text;
using System.Text.Json;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 16b — Branch (BookahHermes ChatSession.branch / branchFrom / branchKeeping @ 5f00986b): row counting as
/// <c>session.branch</c> counts, paged read of every stored row, and the branch_whole → branch fallback.
/// Synthetic payloads only; no live gateway.
/// </summary>
public class Checkpoint16BranchTests
{
    private static SessionMessage Row(long id, string role, string content, string? display = null) =>
        new(id.ToString(), role, content, display);

    // u1(1) a1(2) tool(3) a1b(4,empty) a1c(5) | u2(6) a2(7) | u3(8)
    private static readonly SessionMessage[] Rows =
    [
        Row(1, "user", "q1"), Row(2, "assistant", "calling tool"), Row(3, "tool", "output"),
        Row(4, "assistant", ""), Row(5, "assistant", "answer 1"),
        Row(6, "user", "q2"), Row(7, "assistant", "answer 2"),
        Row(8, "user", "q3")
    ];

    // ---- planner ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 1)]   // user prompt: just up to and including it
    [InlineData(2, 3)]   // a reply keeps all its steps: u1, a1, a1c (tool + empty assistant do not count)
    [InlineData(5, 3)]
    [InlineData(6, 4)]   // u1 a1 a1c u2
    [InlineData(7, 5)]
    [InlineData(8, 6)]   // everything, last prompt included
    public void Count_follows_branch_history_rules(long target, int expected) =>
        Assert.Equal(expected, BranchPlanner.CountThrough(Rows, target));

    [Fact]
    public void Unknown_rows_and_empty_copies_have_no_count()
    {
        Assert.Null(BranchPlanner.CountThrough(Rows, 999));
        Assert.Null(BranchPlanner.CountThrough([Row(1, "tool", "x")], 1));
        Assert.Null(BranchPlanner.CountThrough([], 1));
    }

    [Fact]
    public void Display_content_decides_whether_a_row_has_text()
    {
        var rows = new[] { Row(1, "user", "", display: "/skill shown"), Row(2, "assistant", "ok") };
        Assert.Equal(2, BranchPlanner.CountThrough(rows, 2));
        Assert.True(BranchPlanner.Counts(rows[0]));
        Assert.False(BranchPlanner.Counts(Row(3, "user", "   ")));
    }

    // ---- paged read of all rows ------------------------------------------------------------------

    private sealed class Pager(int total, Func<int, int, string>? body = null) : HttpMessageHandler
    {
        public readonly List<(int Offset, int Limit)> Calls = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var q = System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query);
            var limit = int.Parse(q["limit"]!);
            var offset = int.Parse(q["offset"] ?? "0");
            Assert.Equal("latest", q["order"]);
            Calls.Add((offset, limit));
            // newest-first window, rows chronological inside it (what order=latest returns)
            var end = Math.Max(0, total - offset);
            var start = Math.Max(0, end - limit);
            var json = body?.Invoke(start, end) ?? "{\"session_id\":\"s\",\"messages\":["
                + string.Join(",", Enumerable.Range(start + 1, end - start).Select(i =>
                    $"{{\"id\":{i},\"role\":\"{(i % 2 == 1 ? "user" : "assistant")}\",\"content\":\"m{i}\"}}")) + "]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task All_rows_are_read_oldest_first_across_pages_counted_from_the_newest()
    {
        var pager = new Pager(1100);
        var api = new SessionsApi(new DashboardAuth(new Uri("https://gw.example/"), pager));
        var rows = await api.AllMessagesAsync("s", "p");
        Assert.True(rows.Complete);
        Assert.Equal(Enumerable.Range(1, 1100).Select(i => i.ToString()), rows.Rows.Select(r => r.Id));
        Assert.Equal(new[] { 0, 500, 1000 }, pager.Calls.Select(c => c.Offset));
    }

    [Fact]
    public async Task A_chat_that_fits_one_page_makes_one_request()
    {
        var pager = new Pager(40);
        var api = new SessionsApi(new DashboardAuth(new Uri("https://gw.example/"), pager));
        var rows = await api.AllMessagesAsync("s", "p");
        Assert.True(rows.Complete);
        Assert.Equal(40, rows.Rows.Count);
        Assert.Single(pager.Calls);
    }

    [Fact]
    public async Task Hitting_the_page_cap_reports_incomplete_instead_of_guessing()
    {
        var pager = new Pager(SessionsApi.HistoryPageMax * (SessionsApi.MaxHistoryPages + 5));
        var api = new SessionsApi(new DashboardAuth(new Uri("https://gw.example/"), pager));
        var rows = await api.AllMessagesAsync("s", "p");
        Assert.False(rows.Complete);
        Assert.Equal(SessionsApi.MaxHistoryPages, pager.Calls.Count);
    }

    [Fact]
    public async Task Rows_repeated_because_the_chat_grew_between_pages_are_dropped()
    {
        // second page repeats the oldest row of the first (the chat grew by one meanwhile)
        var call = 0;
        var pager = new Pager(0, (_, _) =>
        {
            call++;
            return call == 1
                ? "{\"messages\":[" + string.Join(",", Enumerable.Range(501, 500).Select(i => $"{{\"id\":{i},\"role\":\"user\",\"content\":\"x\"}}")) + "]}"
                : "{\"messages\":[" + string.Join(",", Enumerable.Range(1, 501).Select(i => $"{{\"id\":{i},\"role\":\"user\",\"content\":\"x\"}}")) + "]}";
        });
        var api = new SessionsApi(new DashboardAuth(new Uri("https://gw.example/"), pager));
        var rows = await api.AllMessagesAsync("s", "p");
        Assert.Equal(1000, rows.Rows.Count);
        Assert.Equal(1000, rows.Rows.Select(r => r.Id).Distinct().Count());
    }

    // ---- transport -------------------------------------------------------------------------------

    private static JsonElement ParamsOf(FakeRpcTransport s, string method)
    {
        using var doc = JsonDocument.Parse(s.FirstCall(method)!);
        return doc.RootElement.GetProperty("params").Clone();
    }

    [Fact]
    public async Task Whole_chat_branch_uses_branch_whole_without_a_count()
    {
        var (t, s, stop) = await Checkpoint16RewindTests.Start((m, _) =>
            m == "session.branch_whole" ? """{"stored_session_id":"new-1","title":"Copy of chat"}""" : null);
        await using var _t = t;
        var r = await t.BranchSessionAsync(new ProfileKey("gw1", "default"), "rt-1", null, default);
        stop.Cancel();
        Assert.Equal("new-1", r.StoredSessionId);
        Assert.Equal("Copy of chat", r.Title);
        var p = ParamsOf(s, "session.branch_whole");
        Assert.Equal("rt-1", p.GetProperty("session_id").GetString());
        Assert.False(p.TryGetProperty("count", out _));
        Assert.Equal(0, s.CountCalls("session.branch"));
    }

    [Fact]
    public async Task Branch_from_here_sends_the_row_count_to_session_branch()
    {
        var (t, s, stop) = await Checkpoint16RewindTests.Start((m, _) =>
            m == "session.branch" ? """{"stored_session_id":"new-2","title":"  "}""" : null);
        await using var _t = t;
        var r = await t.BranchSessionAsync(new ProfileKey("gw1", "default"), "rt-1", 6, default);
        stop.Cancel();
        Assert.Equal("new-2", r.StoredSessionId);
        Assert.Null(r.Title);                                 // blank title is not a title
        Assert.Equal(6, ParamsOf(s, "session.branch").GetProperty("count").GetInt32());
        Assert.Equal(0, s.CountCalls("session.branch_whole"));
    }

    [Fact]
    public async Task Older_gateways_without_branch_whole_fall_back_to_session_branch_once()
    {
        var (t, s, stop) = await Checkpoint16RewindTests.Start((m, _) => m switch
        {
            "session.branch_whole" => "ERR:-32601:method not found",
            "session.branch" => """{"stored_session_id":"new-3"}""",
            _ => null
        });
        await using var _t = t;
        var r = await t.BranchSessionAsync(new ProfileKey("gw1", "default"), "rt-1", null, default);
        stop.Cancel();
        Assert.Equal("new-3", r.StoredSessionId);
        Assert.Equal(1, s.CountCalls("session.branch_whole"));
        Assert.Equal(1, s.CountCalls("session.branch"));
    }

    [Fact]
    public async Task Other_errors_and_missing_destination_are_surfaced_not_retried()
    {
        var (t, s, stop) = await Checkpoint16RewindTests.Start((m, _) => m switch
        {
            "session.branch_whole" => "ERR:4009:busy",
            "session.branch" => "{}",
            _ => null
        });
        await using var _t = t;
        var busy = await Assert.ThrowsAsync<RpcException>(() => t.BranchSessionAsync(new ProfileKey("gw1", "default"), "rt-1", null, default));
        Assert.Equal(4009, busy.Code);
        Assert.Equal(0, s.CountCalls("session.branch"));      // only METHOD_NOT_FOUND falls back
        var missing = await Assert.ThrowsAsync<RpcException>(() => t.BranchSessionAsync(new ProfileKey("gw1", "default"), "rt-1", 3, default));
        Assert.Contains("didn't say", missing.Message);
        stop.Cancel();
    }

    [Fact]
    public async Task Cross_profile_branch_is_refused_before_any_request()
    {
        var (t, s, stop) = await Checkpoint16RewindTests.Start((m, _) => m == "prompt.submit" ? """{"status":"streaming"}""" : null);
        await using var _t = t;
        await foreach (var _ in t.StreamChatAsync(new ChatRequest(new ProfileKey("gw1", "default"),
            new[] { new ChatMessage("user", "hi") }, "s1"), default)) { }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            t.BranchSessionAsync(new ProfileKey("gw1", "other"), "s1", null, default));
        stop.Cancel();
        Assert.Equal(0, s.CountCalls("session.branch_whole"));
        Assert.Equal(0, s.CountCalls("session.branch"));
    }
}
