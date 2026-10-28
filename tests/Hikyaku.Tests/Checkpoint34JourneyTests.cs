using System.Net;
using System.Text;
using System.Text.Json;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 34: the Journey page's data — the learning graph and node text, read-only. Real-shaped payloads from BookahHermes' fixtures; no live gateway.</summary>
public class Checkpoint34JourneyTests
{
    private sealed class Rest(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url)> Seen = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Seen.Add((r.Method, r.RequestUri!.PathAndQuery));
            return Task.FromResult(reply(r));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (JourneyApi Api, Rest Http) Make(Func<HttpRequestMessage, HttpResponseMessage> reply)
    {
        var rest = new Rest(reply);
        return (new JourneyApi(new DashboardAuth(new Uri("https://gw.example/"), rest)), rest);
    }

    private const string Graph = """
        {"nodes":[
          {"id":"deploy","label":"deploy","kind":"skill","timestamp":1759000000,"category":"devops","useCount":3,"extra":1},
          {"id":"memory:MEMORY.md:0:ab12","label":"Prefers metric units <!-- observed: 2026-09-01 -->:","kind":"memory","timestamp":1761000000,"memorySource":"/home/u/.hermes/memories/MEMORY.md"},
          {"id":"notes","label":"notes","kind":"skill","timestamp":1756000000,"category":"uncategorized","use_count":1,"pinned":true},
          {"id":"old","label":"old-skill","kind":"skill","state":"archived"},
          {"id":"deploy","label":"duplicate id"},
          7,
          {"label":"no id"}
        ]}
        """;

    // ---- reading ----

    [Fact]
    public async Task The_graph_is_read_for_the_named_profile_and_parsed_tolerantly()
    {
        var (api, http) = Make(_ => Json(Graph));
        var g = await api.GraphAsync("work", CancellationToken.None);
        var (method, url) = Assert.Single(http.Seen);
        Assert.Equal(HttpMethod.Get, method);
        Assert.Equal("/api/learning/graph?profile=work", url);
        Assert.Equal(["deploy", "memory:MEMORY.md:0:ab12", "notes", "old"], g.Nodes.Select(n => n.Id));   // duplicate, non-object and id-less entries dropped
        Assert.Equal((2, 1), (g.Skills - 1, g.Memories));
        Assert.Equal(3, g.Nodes[0].UseCount);
        Assert.Equal(1, g.Nodes[2].UseCount);                                                              // snake_case spelling accepted
        Assert.True(g.Nodes[2].Pinned);
        Assert.Equal("skill", g.Nodes[3].Kind);                                                            // kind defaults to skill
        Assert.Null(g.Nodes[3].Timestamp);
    }

    [Fact]
    public async Task A_bare_array_numeric_ids_and_odd_numbers_do_not_break_the_graph()
    {
        var (api, _) = Make(_ => Json("""[{"id":5,"label":"n","timestamp":"1759000000.7","useCount":"2"},{"id":"x","timestamp":-4},{"id":"y","timestamp":1e30}]"""));
        var g = await api.GraphAsync("p", CancellationToken.None);
        Assert.Equal(["5", "x", "y"], g.Nodes.Select(n => n.Id));
        Assert.Equal((1759000000L, 2), (g.Nodes[0].Timestamp, g.Nodes[0].UseCount));
        Assert.Null(g.Nodes[1].Timestamp);
        Assert.Null(g.Nodes[2].Timestamp);
        Assert.Equal("x", g.Nodes[1].Label);                                                                // no label: the id stands in
    }

    [Fact]
    public async Task The_graph_is_bounded()
    {
        var many = "{\"nodes\":[" + string.Join(",", Enumerable.Range(0, JourneyApi.MaxNodes + 50).Select(i => $"{{\"id\":\"n{i}\"}}")) + "]}";
        var (api, _) = Make(_ => Json(many));
        Assert.Equal(JourneyApi.MaxNodes, (await api.GraphAsync("p", CancellationToken.None)).Nodes.Count);
    }

    [Fact]
    public async Task A_nodes_text_is_read_by_its_exact_id_escaped_and_clipped()
    {
        var (api, http) = Make(_ => Json("{\"ok\":true,\"kind\":\"memory\",\"label\":\"m\",\"content\":\"" + new string('a', JourneyApi.MaxContentChars + 10) + "\"}"));
        var d = await api.NodeAsync("work", "memory:MEMORY.md:0:ab12", CancellationToken.None);
        var q = System.Web.HttpUtility.ParseQueryString(new Uri("https://x" + http.Seen.Single().Url).Query);
        Assert.Equal(("memory:MEMORY.md:0:ab12", "work"), (q["id"], q["profile"]));                       // passed back exactly as listed
        Assert.True(d.Ok);
        Assert.EndsWith("… (cut off)", d.Content);
        Assert.True(d.Content!.Length < JourneyApi.MaxContentChars + 30);
    }

    [Fact]
    public async Task An_id_with_query_characters_cannot_change_the_request()
    {
        var (api, http) = Make(_ => Json("""{"ok":true,"content":"x"}"""));
        await api.NodeAsync("p", "a b&profile=other#frag", CancellationToken.None);
        var q = System.Web.HttpUtility.ParseQueryString(new Uri("https://x" + http.Seen.Single().Url).Query);
        Assert.Equal("a b&profile=other#frag", q["id"]);
        Assert.Equal("p", q["profile"]);
    }

    [Fact]
    public async Task A_node_the_gateway_reports_as_not_ok_or_an_empty_id_is_not_ok()
    {
        var (api, http) = Make(_ => Json("""{"ok":false}"""));
        Assert.False((await api.NodeAsync("p", "x", CancellationToken.None)).Ok);
        Assert.False((await api.NodeAsync("p", "  ", CancellationToken.None)).Ok);
        Assert.Single(http.Seen);                                                                          // the blank id never reached the gateway
    }

    [Fact]
    public async Task A_gateway_without_the_route_or_a_sign_in_problem_is_typed()
    {
        var (missing, _) = Make(_ => Json("{}", HttpStatusCode.NotFound));
        Assert.Equal(RestFailure.Unsupported, (await Assert.ThrowsAsync<RestException>(() => missing.GraphAsync("p", CancellationToken.None))).Failure);
        var (denied, _) = Make(_ => Json("{}", HttpStatusCode.Forbidden));
        Assert.Equal(RestFailure.Auth, (await Assert.ThrowsAsync<RestException>(() => denied.GraphAsync("p", CancellationToken.None))).Failure);
    }

    [Fact]
    public async Task Nothing_but_GET_is_ever_sent()
    {
        var (api, http) = Make(_ => Json(Graph));
        await api.GraphAsync("p", CancellationToken.None);
        await api.NodeAsync("p", "deploy", CancellationToken.None);
        Assert.All(http.Seen, s => Assert.Equal(HttpMethod.Get, s.Method));
    }

    // ---- display rules ----

    [Fact]
    public void A_memory_title_loses_its_bookkeeping_comment_and_a_skill_reads_its_use_count()
    {
        var g = JourneyApi.ParseGraph(JsonDocument.Parse(Graph).RootElement);
        Assert.Equal("Prefers metric units", g.Nodes[1].Title);
        Assert.Equal("Memory · MEMORY.md", g.Nodes[1].DetailLine);
        Assert.Equal("Skill · devops · used 3 times", g.Nodes[0].DetailLine);
        Assert.Equal("Skill · used once", g.Nodes[2].DetailLine);                                         // "uncategorized" is not shown
        Assert.Equal("Skill · archived", g.Nodes[3].DetailLine);
        var onlyComment = new JourneyNode("i", "<!-- observed -->", "memory", null, null, 0, null, false, null);
        Assert.Equal("<!-- observed -->", onlyComment.Title);                                              // never an empty title
    }

    [Fact]
    public void The_timeline_is_newest_first_by_month_with_undated_last_and_the_filter_splits_used_from_learned()
    {
        var g = JourneyApi.ParseGraph(JsonDocument.Parse(Graph).RootElement);
        var all = JourneyTimeline.Group(g.Nodes, JourneyFilter.All);
        Assert.Equal(["October 2025", "September 2025", "August 2025", "Undated"], all.Select(m => m.Label));
        Assert.Equal("Prefers metric units", all[0].Nodes.Single().Title);
        Assert.Equal(["old-skill"], all[^1].Nodes.Select(n => n.Title));

        var used = JourneyTimeline.Group(g.Nodes, JourneyFilter.Used).SelectMany(m => m.Nodes).Select(n => n.Id).Order().ToList();
        Assert.Equal(["deploy", "notes"], used);
        var learned = JourneyTimeline.Group(g.Nodes, JourneyFilter.Learned).SelectMany(m => m.Nodes).Select(n => n.Id).Order().ToList();
        Assert.Equal(["memory:MEMORY.md:0:ab12", "old"], learned);
        Assert.Equal("Undated", JourneyTimeline.MonthLabel(null));
        Assert.Equal("Undated", JourneyTimeline.MonthLabel(0));
        Assert.Equal("January 1970", JourneyTimeline.MonthLabel(86_400));
    }
}
