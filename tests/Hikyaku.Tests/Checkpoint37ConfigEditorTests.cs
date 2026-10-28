using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 37: the config.yaml editor and MCP server patch (Advanced only). Real-shaped payloads; no live gateway.</summary>
public class Checkpoint37ConfigEditorTests
{
    private sealed class Gateway : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url, string Body)> Seen = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Reply = _ => Json("{}");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Seen.Add((r.Method, r.RequestUri!.PathAndQuery, r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct)));
            return Reply(r);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (GatewayConfigEditor Api, Gateway Gw) Make()
    {
        var g = new Gateway();
        return (new GatewayConfigEditor(new DashboardAuth(new Uri("https://gw.example/"), g)), g);
    }

    // ---- API ----

    [Fact]
    public async Task Raw_reads_the_file_and_where_it_lives_for_the_named_profile()
    {
        var (api, gw) = Make();
        gw.Reply = _ => Json("""{"yaml":"model: x\nmcp_servers: {}\n","path":"/home/u/.hermes/config.yaml"}""");
        var raw = await api.RawAsync("work", CancellationToken.None);
        Assert.Equal(("model: x\nmcp_servers: {}\n", "/home/u/.hermes/config.yaml"), (raw.Yaml, raw.Path));
        Assert.Equal((HttpMethod.Get, "/api/config/raw?profile=work"), (gw.Seen.Single().Method, gw.Seen.Single().Url));
    }

    [Fact]
    public async Task Saving_raw_puts_the_whole_text_as_yaml_text_and_refuses_empty_or_huge_text_without_a_request()
    {
        var (api, gw) = Make();
        await api.SaveRawAsync("work", "model: y\n", CancellationToken.None);
        var put = gw.Seen.Single();
        Assert.Equal((HttpMethod.Put, "/api/config/raw?profile=work"), (put.Method, put.Url));
        Assert.Equal("model: y\n", (string)JsonNode.Parse(put.Body)!["yaml_text"]!);

        await Assert.ThrowsAsync<RestException>(() => api.SaveRawAsync("work", "  \n", CancellationToken.None));
        await Assert.ThrowsAsync<RestException>(() => api.SaveRawAsync("work", new string('a', GatewayConfigEditor.MaxYamlChars + 1), CancellationToken.None));
        Assert.Single(gw.Seen);
    }

    [Fact]
    public async Task Saving_a_patch_puts_only_the_nested_changed_keys_under_config()
    {
        var (api, gw) = Make();
        await api.SavePatchAsync("work", new JsonObject { ["voice"] = new JsonObject { ["enabled"] = true } }, CancellationToken.None);
        var put = gw.Seen.Single();
        Assert.Equal("/api/config?profile=work", put.Url);
        Assert.True((bool)JsonNode.Parse(put.Body)!["config"]!["voice"]!["enabled"]!);
        await Assert.ThrowsAsync<RestException>(() => api.SavePatchAsync("work", new JsonObject(), CancellationToken.None));
        Assert.Single(gw.Seen);
    }

    [Fact]
    public async Task Failures_are_typed()
    {
        var (api, gw) = Make();
        gw.Reply = _ => Json("{\"detail\":\"yaml_text must be a YAML mapping\"}", HttpStatusCode.BadRequest);
        var ex = await Assert.ThrowsAsync<RestException>(() => api.SaveRawAsync("w", "- a\n- b\n", CancellationToken.None));
        Assert.Equal(RestFailure.Invalid, ex.Failure);
        gw.Reply = _ => Json("{}", HttpStatusCode.Forbidden);
        Assert.Equal(RestFailure.Auth, (await Assert.ThrowsAsync<RestException>(() => api.RawAsync("w", CancellationToken.None))).Failure);
    }

    // ---- MCP spec ----

    private static McpServerSpec Local(string name = "files", string command = "npx", string[]? args = null,
        Dictionary<string, string>? env = null, int? timeout = null) =>
        new(name, McpTransport.Command, command, args ?? ["-y", "@modelcontextprotocol/server-filesystem", "/data"], env ?? new(), "", new Dictionary<string, string>(), true, timeout, null);

    private static McpServerSpec Remote(string url = "https://mcp.example.com/mcp", Dictionary<string, string>? headers = null) =>
        new("remote", McpTransport.Url, "", [], new Dictionary<string, string>(), url, headers ?? new(), true, null, 30);

    [Fact]
    public void A_local_server_becomes_a_patch_with_only_its_own_keys()
    {
        var patch = Local(env: new() { ["GITHUB_TOKEN"] = "${GITHUB_TOKEN}" }, timeout: 60).ToPatch();
        var s = patch["mcp_servers"]!["files"]!;
        Assert.Equal("npx", (string)s["command"]!);
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "/data"], s["args"]!.AsArray().Select(a => (string)a!));
        Assert.Equal("${GITHUB_TOKEN}", (string)s["env"]!["GITHUB_TOKEN"]!);
        Assert.True((bool)s["enabled"]!);
        Assert.Equal(60, (int)s["timeout"]!);
        Assert.Null(s["url"]);                                                        // a local server never carries remote fields
        Assert.Null(s["headers"]);
        Assert.Single(patch["mcp_servers"]!.AsObject());                             // it touches no other server
    }

    [Fact]
    public void A_remote_server_becomes_a_patch_with_its_url_headers_and_connection_timeout()
    {
        var s = Remote(headers: new() { ["Authorization"] = "Bearer ${MCP_TOKEN}" }).ToPatch()["mcp_servers"]!["remote"]!;
        Assert.Equal("https://mcp.example.com/mcp", (string)s["url"]!);
        Assert.Equal("Bearer ${MCP_TOKEN}", (string)s["headers"]!["Authorization"]!);
        Assert.Equal(30, (int)s["connect_timeout"]!);
        Assert.Null(s["command"]);
        Assert.Null(s["args"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-leading-dash")]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("a.b")]
    public void A_name_that_could_break_the_file_is_refused(string name)
    {
        Assert.NotEmpty(Local(name).Problems());
        Assert.Throws<InvalidOperationException>(() => Local(name).ToPatch());
    }

    [Fact]
    public void Commands_arguments_and_addresses_are_validated()
    {
        Assert.NotEmpty(Local(command: " ").Problems());
        Assert.NotEmpty(Local(command: "npx\nrm -rf").Problems());
        Assert.NotEmpty(Local(args: ["ok\nnot ok"]).Problems());
        Assert.Empty(Local().Problems());
        Assert.NotEmpty(Remote("not a url").Problems());
        Assert.NotEmpty(Remote("ftp://x/y").Problems());
        Assert.NotEmpty(Remote("http://mcp.example.com/mcp").Problems());              // plain http only for this computer
        Assert.Empty(Remote("http://localhost:8080/mcp").Problems());
        Assert.NotEmpty(Remote("https://user:pass" + "@" + "mcp.example.com/mcp").Problems());   // no credentials in the address
        Assert.NotEmpty(Local(timeout: 0).Problems());
        Assert.NotEmpty(Local(timeout: 99999).Problems());
    }

    [Fact]
    public void Environment_and_header_names_and_values_are_validated()
    {
        Assert.NotEmpty(Local(env: new() { ["bad name"] = "x" }).Problems());
        Assert.NotEmpty(Local(env: new() { ["OK"] = "line\nbreak" }).Problems());
        Assert.NotEmpty(Remote(headers: new() { ["Bad:Name"] = "x" }).Problems());
        Assert.Empty(Remote(headers: new() { ["X-Api-Version"] = "2" }).Problems());
    }

    [Fact]
    public void A_literal_secret_in_an_environment_variable_or_header_is_flagged_but_a_variable_reference_is_not()
    {
        Assert.Equal(["GITHUB_TOKEN"], Local(env: new() { ["GITHUB_TOKEN"] = "ghp_literal", ["REGION"] = "eu" }).PlaintextSecretKeys());
        Assert.Empty(Local(env: new() { ["GITHUB_TOKEN"] = "${GITHUB_TOKEN}" }).PlaintextSecretKeys());
        Assert.Equal(["Authorization"], Remote(headers: new() { ["Authorization"] = "Bearer abc123" }).PlaintextSecretKeys());
        Assert.Empty(Remote(headers: new() { ["Authorization"] = "${AUTH}" }).PlaintextSecretKeys());
        Assert.Empty(Local(env: new() { ["API_KEY"] = "" }).PlaintextSecretKeys());       // an empty value holds no secret
    }

    [Fact]
    public void Pairs_and_arguments_are_read_one_per_line()
    {
        var (env, problems) = McpServerSpec.ParsePairs("A=1\n\n B = two = three \r\nbad line", '=');
        Assert.Equal(("1", "two = three"), (env["A"], env["B"]));
        Assert.Single(problems);
        var (headers, none) = McpServerSpec.ParsePairs("Authorization: Bearer ${T}", ':');
        Assert.Equal("Bearer ${T}", headers["Authorization"]);
        Assert.Empty(none);
        Assert.Equal(["-y", "pkg", "/a b"], McpServerSpec.ParseArgs(" -y \n\npkg\r\n/a b "));
    }
}
