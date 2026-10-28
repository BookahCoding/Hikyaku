using System.Net;
using System.Text;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 35a: the skills hub, read-only (sources, official, search, preview, scan). Real-shaped payloads per BookahHermes' model; no live gateway.</summary>
public class Checkpoint35SkillsHubTests
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

    private static (SkillsHubApi Api, Rest Http) Make(Func<HttpRequestMessage, HttpResponseMessage> reply)
    {
        var rest = new Rest(reply);
        return (new SkillsHubApi(new DashboardAuth(new Uri("https://gw.example/"), rest)), rest);
    }

    [Fact]
    public async Task Sources_featured_and_installed_are_read_for_the_profile_and_nulls_read_as_defaults()
    {
        var (api, http) = Make(_ => Json("""
            {"sources":[{"id":"official","label":"Official"},{"id":"github","rate_limited":true,"available":false},{"label":"no id"}],
             "featured":[{"name":"deploy","description":null,"source":"official","identifier":"official/devops/deploy","trust_level":"builtin","tags":null,"installed":false},
                         {"name":"notes","identifier":"github/x/notes","trust_level":"community","tags":["writing"],"installed":true},
                         7, {"description":"nameless"}],
             "installed":{"github/x/notes":{"name":"notes","trust_level":"community","scan_verdict":"caution"}}}
            """));
        var s = await api.SourcesAsync("work", CancellationToken.None);
        var (method, url) = Assert.Single(http.Seen);
        Assert.Equal((HttpMethod.Get, "/api/skills/hub/sources?profile=work"), (method, url));
        Assert.Equal(["official", "github"], s.Sources.Select(x => x.Id));                    // a source with no id is dropped
        Assert.Equal(("github", true, false), (s.Sources[1].Label, s.Sources[1].RateLimited, s.Sources[1].Available));
        Assert.Equal(["deploy", "notes"], s.Featured.Select(x => x.Name));                     // junk and nameless entries dropped
        Assert.Equal("", s.Featured[0].Description);
        Assert.Empty(s.Featured[0].Tags);
        Assert.True(s.Featured[0].IsOfficial);
        Assert.Equal("built in", s.Featured[0].TrustLabel);
        Assert.Equal("caution", s.Installed["github/x/notes"].ScanVerdict);
        Assert.True(s.Featured[1].Installed);
        Assert.Equal("github/x/notes", s.Featured[1].Key);
    }

    [Fact]
    public async Task The_official_catalog_is_read_as_a_wrapped_object_or_a_bare_array()
    {
        var (wrapped, _) = Make(_ => Json("""{"skills":[{"name":"a","identifier":"official/a"}]}"""));
        Assert.Equal(["a"], (await wrapped.OfficialAsync("p", CancellationToken.None)).Select(x => x.Name));
        var (bare, _) = Make(_ => Json("""[{"name":"b"}]"""));
        var b = await bare.OfficialAsync("p", CancellationToken.None);
        Assert.Equal("b", b.Single().Key);                                                      // no identifier: the name is the key
    }

    [Fact]
    public async Task Search_sends_the_trimmed_query_all_sources_and_a_clamped_limit_and_reports_slow_sources()
    {
        var (api, http) = Make(_ => Json("""{"results":[{"name":"r","identifier":"x/r"}],"timed_out":["github"],"installed":{}}"""));
        var r = await api.SearchAsync("work", "  pdf tools & more  ", 5000, CancellationToken.None);
        var q = System.Web.HttpUtility.ParseQueryString(new Uri("https://x" + http.Seen.Single().Url).Query);
        Assert.Equal(("pdf tools & more", "all", "100", "work"), (q["q"], q["source"], q["limit"], q["profile"]));
        Assert.Equal(["github"], r.TimedOut);
        Assert.Single(r.Results);
    }

    [Fact]
    public async Task A_blank_search_asks_nothing()
    {
        var (api, http) = Make(_ => Json("{}"));
        Assert.Empty((await api.SearchAsync("p", "   ", 20, CancellationToken.None)).Results);
        Assert.Empty(http.Seen);
    }

    [Fact]
    public async Task Preview_reads_the_skill_text_without_its_front_matter_and_bounds_it()
    {
        var (api, http) = Make(_ => Json("""
            {"name":"deploy","description":"Ship it","source":"official","identifier":"official/devops/deploy","trust_level":"builtin",
             "tags":["ops"],"skill_md":"---\nname: deploy\ndescription: Ship it\n---\n\n# Deploy\n\nRun the script.","files":["SKILL.md","run.sh"],"repo":null}
            """));
        var p = await api.PreviewAsync("work", "official/devops/deploy", CancellationToken.None);
        var q = System.Web.HttpUtility.ParseQueryString(new Uri("https://x" + http.Seen.Single().Url).Query);
        Assert.Equal(("official/devops/deploy", "work"), (q["identifier"], q["profile"]));
        Assert.Equal("# Deploy\n\nRun the script.", p.Body);
        Assert.Equal(["SKILL.md", "run.sh"], p.Files);
        Assert.Null(p.Repo);

        var (big, _) = Make(_ => Json("{\"skill_md\":\"" + new string('a', SkillsHubApi.MaxSkillMdChars + 50) + "\"}"));
        Assert.EndsWith("… (cut off)", (await big.PreviewAsync("p", "x", CancellationToken.None)).SkillMd);
        // a rule later in a document with no front matter is kept
        Assert.Equal("intro\n\n---\n\nbody", new HubPreview("", "", "", "", "", null, [], "intro\n\n---\n\nbody", []).Body);
        Assert.Equal("no front matter", new HubPreview("", "", "", "", "", null, [], "no front matter", []).Body);
        Assert.Equal("---\nunclosed", new HubPreview("", "", "", "", "", null, [], "---\nunclosed", []).Body);
    }

    [Fact]
    public async Task The_safety_scan_reports_verdict_policy_and_each_finding()
    {
        var (api, http) = Make(_ => Json("""
            {"name":"notes","identifier":"github/x/notes","source":"github","trust_level":"community","verdict":"dangerous","summary":"2 high-risk patterns",
             "policy":"block","policy_reason":"community source with dangerous verdict",
             "findings":[{"severity":"high","category":"exfiltration","file":"run.sh","line":12,"description":"curl | sh"},"junk",{"severity":"low"}]}
            """));
        var s = await api.ScanAsync("work", "github/x/notes", CancellationToken.None);
        Assert.Equal("/api/skills/hub/scan?identifier=github%2Fx%2Fnotes&profile=work", http.Seen.Single().Url);
        Assert.Equal(("dangerous", "block"), (s.Verdict, s.Policy));
        Assert.Equal(2, s.Findings.Count);
        Assert.Equal(("high", "run.sh", 12, "curl | sh"), (s.Findings[0].Severity, s.Findings[0].File, s.Findings[0].Line, s.Findings[0].Description));
    }

    [Fact]
    public async Task An_identifier_with_query_characters_cannot_change_the_request_and_a_blank_one_sends_nothing()
    {
        var (api, http) = Make(_ => Json("{}"));
        await api.PreviewAsync("p", "a b&profile=other#x", CancellationToken.None);
        var q = System.Web.HttpUtility.ParseQueryString(new Uri("https://x" + http.Seen.Single().Url).Query);
        Assert.Equal(("a b&profile=other#x", "p"), (q["identifier"], q["profile"]));
        var ex = await Assert.ThrowsAsync<RestException>(() => api.ScanAsync("p", "  ", CancellationToken.None));
        Assert.Equal(RestFailure.Invalid, ex.Failure);
        Assert.Single(http.Seen);
    }

    [Fact]
    public async Task Only_reads_are_ever_sent_and_failures_are_typed()
    {
        var (api, http) = Make(_ => Json("{}"));
        await api.SourcesAsync("p", CancellationToken.None);
        await api.OfficialAsync("p", CancellationToken.None);
        await api.SearchAsync("p", "x", 10, CancellationToken.None);
        await api.PreviewAsync("p", "x", CancellationToken.None);
        await api.ScanAsync("p", "x", CancellationToken.None);
        Assert.All(http.Seen, s => Assert.Equal(HttpMethod.Get, s.Method));
        Assert.DoesNotContain(http.Seen, s => s.Url.Contains("install") || s.Url.Contains("uninstall") || s.Url.Contains("update"));

        var (missing, _) = Make(_ => Json("{}", HttpStatusCode.NotFound));
        Assert.Equal(RestFailure.Unsupported, (await Assert.ThrowsAsync<RestException>(() => missing.SourcesAsync("p", CancellationToken.None))).Failure);
        var (denied, _) = Make(_ => Json("{}", HttpStatusCode.Unauthorized));
        Assert.Equal(RestFailure.Auth, (await Assert.ThrowsAsync<RestException>(() => denied.SearchAsync("p", "x", 5, CancellationToken.None))).Failure);
    }

    [Fact]
    public async Task Results_are_bounded()
    {
        var many = "{\"results\":[" + string.Join(",", Enumerable.Range(0, SkillsHubApi.MaxResults + 40).Select(i => $"{{\"name\":\"s{i}\"}}")) + "]}";
        var (api, _) = Make(_ => Json(many));
        Assert.Equal(SkillsHubApi.MaxResults, (await api.SearchAsync("p", "x", 10, CancellationToken.None)).Results.Count);
    }
}
