using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 35b: hub install / uninstall / update-all as background actions on the gateway. Fixtures only.</summary>
public class Checkpoint35SkillsHubActionTests
{
    private sealed class Gateway : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url, string Body)> Seen = new();
        public Func<HttpRequestMessage, string, HttpResponseMessage> Reply = (_, _) => Json("{}");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            Seen.Add((r.Method, r.RequestUri!.PathAndQuery, body));
            return Reply(r, body);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static SkillsHubApi Api(Gateway g, TimeSpan? timeout = null) =>
        new(new DashboardAuth(new Uri("https://gw.example/"), g), TimeSpan.FromSeconds(1), timeout, (_, _) => Task.CompletedTask);

    /// <summary>Starts an action called "act-1"; the status answers from <paramref name="statuses"/> in turn (the last one repeats).</summary>
    private static Gateway Actions(params string[] statuses)
    {
        var g = new Gateway();
        var i = 0;
        g.Reply = (r, _) => r.Method == HttpMethod.Post
            ? Json("""{"name":"act-1","ok":true}""")
            : Json(statuses[Math.Min(i++, statuses.Length - 1)]);
        return g;
    }

    [Fact]
    public async Task Install_posts_the_identifier_and_profile_in_the_body_then_polls_until_the_process_ends()
    {
        var g = Actions("""{"running":true,"lines":["cloning"]}""", """{"running":false,"exit_code":0,"lines":["Installed deploy"]}""");
        var r = await Api(g).InstallAsync("work", "official/devops/deploy", CancellationToken.None);
        var done = Assert.IsType<HubActionResult.Done>(r);
        Assert.Equal(["Installed deploy"], done.Lines);

        var post = g.Seen[0];
        Assert.Equal((HttpMethod.Post, "/api/skills/hub/install"), (post.Method, post.Url));                // the profile is in the body, not the query
        var body = JsonNode.Parse(post.Body)!;
        Assert.Equal(("official/devops/deploy", "work"), ((string)body["identifier"]!, (string)body["profile"]!));
        Assert.Equal(2, g.Seen.Count(s => s.Method == HttpMethod.Get));
        Assert.Equal("/api/actions/act-1/status?lines=200&profile=work", g.Seen[1].Url);
    }

    [Fact]
    public async Task Uninstall_and_update_all_send_their_documented_bodies()
    {
        var g = Actions("""{"running":false,"exit_code":0,"lines":[]}""");
        Assert.IsType<HubActionResult.Done>(await Api(g).UninstallAsync("work", "notes", CancellationToken.None));
        Assert.IsType<HubActionResult.Done>(await Api(g).UpdateAllAsync("work", CancellationToken.None));
        var posts = g.Seen.Where(s => s.Method == HttpMethod.Post).ToList();
        Assert.Equal(["/api/skills/hub/uninstall", "/api/skills/hub/update"], posts.Select(p => p.Url));
        Assert.Equal(("notes", "work"), ((string)JsonNode.Parse(posts[0].Body)!["name"]!, (string)JsonNode.Parse(posts[0].Body)!["profile"]!));
        Assert.Equal("work", (string)JsonNode.Parse(posts[1].Body)!["profile"]!);
        Assert.Null(JsonNode.Parse(posts[1].Body)!["identifier"]);
    }

    [Fact]
    public async Task A_failed_exit_shows_the_last_three_log_lines()
    {
        var g = Actions("""{"running":false,"exit_code":1,"lines":["a","b","c","d","error: no network"]}""");
        var f = Assert.IsType<HubActionResult.Failed>(await Api(g).InstallAsync("p", "x/y", CancellationToken.None));
        Assert.Equal(["c", "d", "error: no network"], f.Lines);
    }

    [Theory]
    [InlineData("Not installed: the security scan found 3 high-risk patterns", 3)]
    [InlineData("Not installed: the security scan found high-risk patterns", null)]
    [InlineData("Installation blocked: x (community source + dangerous verdict, 2 findings)", 2)]
    public async Task A_scan_refusal_in_the_log_is_reported_as_blocked_not_as_a_plain_failure(string line, int? findings)
    {
        var g = Actions("{\"running\":false,\"exit_code\":1,\"lines\":[\"scanning\",\"" + line + "\"]}");
        var b = Assert.IsType<HubActionResult.Blocked>(await Api(g).InstallAsync("p", "x/y", CancellationToken.None));
        Assert.Equal(findings, b.Findings);
    }

    [Fact]
    public async Task A_start_that_is_not_ok_or_has_no_action_name_fails_without_polling()
    {
        var notOk = new Gateway { Reply = (_, _) => Json("""{"ok":false,"error":"already installed"}""") };
        var f = Assert.IsType<HubActionResult.Failed>(await Api(notOk).InstallAsync("p", "x", CancellationToken.None));
        Assert.Equal(["already installed"], f.Lines);
        Assert.Single(notOk.Seen);

        var nameless = new Gateway { Reply = (_, _) => Json("""{"ok":true}""") };
        Assert.IsType<HubActionResult.Failed>(await Api(nameless).InstallAsync("p", "x", CancellationToken.None));
        Assert.Single(nameless.Seen);
    }

    [Fact]
    public async Task An_action_the_gateway_no_longer_knows_is_reported_as_lost()
    {
        var g = new Gateway();
        g.Reply = (r, _) => r.Method == HttpMethod.Post ? Json("""{"name":"act-1","ok":true}""") : Json("{}", HttpStatusCode.NotFound);
        var f = Assert.IsType<HubActionResult.Failed>(await Api(g).InstallAsync("p", "x", CancellationToken.None));
        Assert.Equal(["Lost track of the install on the gateway."], f.Lines);
    }

    [Fact]
    public async Task A_brief_outage_while_polling_is_ridden_out_but_a_longer_one_is_an_error()
    {
        var g = new Gateway();
        var gets = 0;
        g.Reply = (r, _) =>
        {
            if (r.Method == HttpMethod.Post) return Json("""{"name":"act-1","ok":true}""");
            if (++gets <= 2) throw new HttpRequestException("blip");
            return Json("""{"running":false,"exit_code":0,"lines":["ok"]}""");
        };
        Assert.IsType<HubActionResult.Done>(await Api(g).InstallAsync("p", "x", CancellationToken.None));

        // exactly three failures in a row is one more than is ridden out
        var three = new Gateway();
        var threeGets = 0;
        three.Reply = (r, _) =>
        {
            if (r.Method == HttpMethod.Post) return Json("""{"name":"act-1","ok":true}""");
            if (++threeGets <= 3) throw new HttpRequestException("blip");
            return Json("""{"running":false,"exit_code":0,"lines":[]}""");
        };
        await Assert.ThrowsAsync<HttpRequestException>(() => Api(three).InstallAsync("p", "x", CancellationToken.None));

        var down = new Gateway();
        down.Reply = (r, _) => r.Method == HttpMethod.Post ? Json("""{"name":"act-1","ok":true}""") : throw new HttpRequestException("down");
        await Assert.ThrowsAsync<HttpRequestException>(() => Api(down).InstallAsync("p", "x", CancellationToken.None));
    }

    [Fact]
    public async Task An_action_that_never_ends_is_left_running_on_the_gateway_with_a_plain_message()
    {
        var g = Actions("""{"running":true,"lines":[]}""");
        var s = Assert.IsType<HubActionResult.StillRunning>(await Api(g, TimeSpan.FromSeconds(5)).UninstallAsync("p", "notes", CancellationToken.None));
        Assert.Contains("still running on the gateway", s.Lines.Single());
        Assert.Contains("removal", s.Lines.Single());
    }

    [Fact]
    public async Task A_blank_identifier_or_name_sends_nothing_and_an_action_name_is_escaped()
    {
        var g = Actions("""{"running":false,"exit_code":0,"lines":[]}""");
        await Assert.ThrowsAsync<RestException>(() => Api(g).InstallAsync("p", "  ", CancellationToken.None));
        await Assert.ThrowsAsync<RestException>(() => Api(g).UninstallAsync("p", "", CancellationToken.None));
        Assert.Empty(g.Seen);

        var odd = new Gateway();
        odd.Reply = (r, _) => r.Method == HttpMethod.Post ? Json("""{"name":"a/b c?x","ok":true}""") : Json("""{"running":false,"exit_code":0}""");
        await Api(odd).InstallAsync("p", "x", CancellationToken.None);
        Assert.StartsWith("/api/actions/a%2Fb%20c%3Fx/status", odd.Seen[1].Url);
    }

    [Fact]
    public async Task Polling_can_be_cancelled()
    {
        var g = Actions("""{"running":true}""");
        using var cts = new CancellationTokenSource();
        var api = new SkillsHubApi(new DashboardAuth(new Uri("https://gw.example/"), g), TimeSpan.FromSeconds(1), null, (_, _) => { cts.Cancel(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.InstallAsync("p", "x", cts.Token));
    }
}
