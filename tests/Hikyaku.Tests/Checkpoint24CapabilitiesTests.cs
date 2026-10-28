using System.Net;
using System.Net.Http;
using System.Text;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 24 — capabilities (skills, toolsets, MCP servers). Wire contract: BookahHermes CapabilitiesApi.kt @ 5f00986b (the dashboard
/// routes Hermes Desktop's settings use). Fixtures only; no live gateway.
/// </summary>
public class Checkpoint24CapabilitiesTests
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

    private static (CapabilitiesApi Api, List<Seen> Seen, DashboardAuth Auth) Make(Func<HttpRequestMessage, HttpResponseMessage> reply)
    {
        var seen = new List<Seen>();
        var auth = new DashboardAuth(new Uri("https://hermes.example/"), new Stub(seen, reply));
        return (new CapabilitiesApi(auth), seen, auth);
    }

    [Fact]
    public async Task Skills_parse_real_shapes_including_nulls_and_disabled_ones()
    {
        var (api, seen, auth) = Make(_ => Json(HttpStatusCode.OK, """
            [{"name":"web-search","description":null,"category":"research","enabled":true,"provenance":"bundled","usage":12},
             {"name":"my-skill","description":"Mine","enabled":"false","usage":"3"},
             {"description":"no name is dropped"}, "junk"]
            """));
        using var _a = auth;
        var skills = await api.SkillsAsync("work", CancellationToken.None);
        Assert.Equal("/api/skills?profile=work", seen.Single().PathAndQuery);
        Assert.Equal(2, skills.Count);
        Assert.Equal("", skills[0].Description);
        Assert.Equal("bundled", skills[0].Provenance);
        Assert.False(skills[1].Enabled);
        Assert.Equal(3, skills[1].Usage);
        Assert.Equal("agent", skills[1].Provenance);   // default when absent
    }

    [Fact]
    public async Task Toolsets_and_mcp_servers_parse_including_an_object_wrapper_and_null_args()
    {
        var (api, _, auth) = Make(r => r.RequestUri!.AbsolutePath.Contains("toolsets")
            ? Json(HttpStatusCode.OK, """[{"name":"web","label":"Web","enabled":true,"configured":false,"tools":["search","fetch"]},{"name":"terminal"}]""")
            : Json(HttpStatusCode.OK, """{"servers":[{"name":"git","transport":"stdio","command":"uvx","args":["mcp-git","--x"],"enabled":true},{"name":"docs","transport":"http","url":"https://docs.example/mcp","args":null,"plugin":"herald"}]}"""));
        using var _a = auth;
        var ts = await api.ToolsetsAsync("p", CancellationToken.None);
        Assert.Equal("Web", ts[0].Label);
        Assert.False(ts[0].Configured);
        Assert.Equal(["search", "fetch"], ts[0].Tools);
        Assert.Equal("terminal", ts[1].Label);          // label defaults to the name
        Assert.False(ts[1].Enabled);
        Assert.True(ts[1].Configured);

        var servers = await api.McpServersAsync("p", CancellationToken.None);
        Assert.Equal("uvx mcp-git --x", servers[0].Target);
        Assert.False(servers[0].Locked);
        Assert.Equal("https://docs.example/mcp", servers[1].Target);
        Assert.True(servers[1].Locked);                  // plugin-provided: cannot be switched here
    }

    [Fact]
    public async Task Switches_use_the_documented_verbs_paths_profile_and_bodies()
    {
        var (api, seen, auth) = Make(_ => Json(HttpStatusCode.OK, "{}"));
        using var _a = auth;
        await api.SetSkillEnabledAsync("work", "web search", false, CancellationToken.None);
        await api.SetToolsetEnabledAsync("work", "web", true, CancellationToken.None);
        await api.SetMcpServerEnabledAsync("work", "git/x", true, CancellationToken.None);
        await api.TestMcpServerAsync("work", "git", CancellationToken.None);

        Assert.Collection(seen,
            s => { Assert.Equal(HttpMethod.Put, s.Method); Assert.Equal("/api/skills/toggle?profile=work", s.PathAndQuery); Assert.Equal("""{"name":"web search","enabled":false}""", s.Body); },
            s => { Assert.Equal(HttpMethod.Put, s.Method); Assert.Equal("/api/tools/toolsets/web?profile=work", s.PathAndQuery); Assert.Equal("""{"enabled":true}""", s.Body); },
            s => { Assert.Equal(HttpMethod.Put, s.Method); Assert.Equal("/api/mcp/servers/git%2Fx/enabled?profile=work", s.PathAndQuery); Assert.Equal("""{"enabled":true}""", s.Body); },
            s => { Assert.Equal(HttpMethod.Post, s.Method); Assert.Equal("/api/mcp/servers/git/test?profile=work", s.PathAndQuery); });
    }

    [Fact]
    public async Task A_failed_mcp_connection_is_a_reply_not_an_exception()
    {
        var (api, _, auth) = Make(_ => Json(HttpStatusCode.OK, """{"ok":false,"error":"connection refused","tools":[]}"""));
        using var _a = auth;
        var r = await api.TestMcpServerAsync("p", "git", CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal("connection refused", r.Error);

        var (api2, _, auth2) = Make(_ => Json(HttpStatusCode.OK, """{"ok":true,"tools":[{"name":"commit","description":"Commit"},{"description":"x"}]}"""));
        using var _b = auth2;
        var ok = await api2.TestMcpServerAsync("p", "git", CancellationToken.None);
        Assert.True(ok.Ok);
        Assert.Equal([("commit", "Commit")], ok.Tools);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, RestFailure.Auth)]
    [InlineData(HttpStatusCode.Forbidden, RestFailure.Auth)]
    [InlineData(HttpStatusCode.BadRequest, RestFailure.Invalid)]
    [InlineData(HttpStatusCode.Conflict, RestFailure.Conflict)]
    [InlineData(HttpStatusCode.InternalServerError, RestFailure.Failed)]
    public async Task Errors_are_typed_and_carry_the_gateways_sanitised_reason(HttpStatusCode code, RestFailure expected)
    {
        var (api, _, auth) = Make(_ => Json(code, "{\"detail\":\"no such\\u0007 thing\"}"));
        using var _a = auth;
        var ex = await Assert.ThrowsAsync<RestException>(() => api.SetSkillEnabledAsync("p", "x", true, CancellationToken.None));
        Assert.Equal(expected, ex.Failure);
        Assert.Equal("no such thing", ex.Message);
    }

    [Fact]
    public async Task A_missing_collection_is_unsupported_but_a_missing_item_is_not_found()
    {
        var (api, _, auth) = Make(_ => Json(HttpStatusCode.NotFound, "{}"));
        using var _a = auth;
        Assert.Equal(RestFailure.Unsupported, (await Assert.ThrowsAsync<RestException>(() => api.SkillsAsync("p", CancellationToken.None))).Failure);
        Assert.Equal(RestFailure.NotFound, (await Assert.ThrowsAsync<RestException>(() => api.SetToolsetEnabledAsync("p", "x", true, CancellationToken.None))).Failure);
    }

    [Fact]
    public async Task An_oversized_response_is_refused()
    {
        var (api, _, auth) = Make(_ => Json(HttpStatusCode.OK, "[\"" + new string('x', DashboardRest.MaxResponseBytes + 10) + "\"]"));
        using var _a = auth;
        var ex = await Assert.ThrowsAsync<RestException>(() => api.SkillsAsync("p", CancellationToken.None));
        Assert.Equal(RestFailure.Failed, ex.Failure);
    }
}
