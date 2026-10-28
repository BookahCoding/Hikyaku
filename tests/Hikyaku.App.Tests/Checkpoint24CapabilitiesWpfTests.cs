using System.Net;
using System.Net.Http;
using System.Text;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 24 — the Capabilities page through the real MainViewModel (fixture HTTP; no live gateway).</summary>
[Collection("StaMath")]
public class Checkpoint24CapabilitiesWpfTests
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

    private const string Skills = """[{"name":"b-skill","enabled":true,"provenance":"bundled"},{"name":"a-skill","enabled":false}]""";
    private const string Toolsets = """[{"name":"web","label":"Web","enabled":false,"configured":true},{"name":"browser","label":"Browser","enabled":false,"configured":false},{"name":"terminal","enabled":true,"configured":false}]""";
    private const string Mcp = """{"servers":[{"name":"git","transport":"stdio","command":"uvx","args":["mcp-git"],"enabled":false},{"name":"docs","transport":"http","url":"https://d.example/mcp","enabled":true,"plugin":"herald"}]}""";

    private sealed class Rig : IDisposable
    {
        public MainViewModel Vm;
        public List<Seen> Seen = [];
        public DashboardAuth Auth;
        public List<string> Asked = [];
        public List<(string Title, string Text)> Shown = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Reply;

        public Rig(bool confirm = true)
        {
            Reply = r => r.RequestUri!.AbsolutePath switch
            {
                "/api/skills" => Json(HttpStatusCode.OK, Skills),
                "/api/tools/toolsets" => Json(HttpStatusCode.OK, Toolsets),
                "/api/mcp/servers" => Json(HttpStatusCode.OK, Mcp),
                _ => Json(HttpStatusCode.OK, "{}")
            };
            var selection = new ProfileSelectionService(new MemPrefs24());
            selection.SetSelectedAsync(new[] { new ProfileKey("gw", "default"), new ProfileKey("gw", "work") }).GetAwaiter().GetResult();
            Auth = new DashboardAuth(new Uri("https://hermes.example/"), new Stub(Seen, r => Reply(r)));
            Vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
                () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
            Vm.AttachCron(() => null, "gw");
            Vm.AttachCapabilities(() => new CapabilitiesApi(Auth));
            Vm.ConfirmCapability = (t, m) => { Asked.Add(m); return confirm; };
            Vm.ShowCapabilityResult = (t, x) => Shown.Add((t, x));
        }

        public void Dispose() => Auth.Dispose();
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }

    [Fact]
    public void Refresh_loads_the_active_profiles_three_lists()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.RefreshCapabilitiesAsync().GetAwaiter().GetResult();
            Assert.All(rig.Seen, s => Assert.EndsWith("?profile=default", s.PathAndQuery));
            Assert.Equal(["a-skill", "b-skill"], rig.Vm.SkillRows.Select(r => r.Name));   // sorted
            Assert.Equal(3, rig.Vm.ToolsetRows.Count);
            Assert.Equal(2, rig.Vm.McpRows.Count);
            Assert.Contains("Profile: default", rig.Vm.CapabilitiesProfileLabel);
        });
    }

    [Fact]
    public void One_list_failing_does_not_hide_the_others()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            var inner = rig.Reply;
            rig.Reply = r => r.RequestUri!.AbsolutePath == "/api/mcp/servers" ? Json(HttpStatusCode.NotFound, "{}") : inner(r);
            rig.Vm.RefreshCapabilitiesAsync().GetAwaiter().GetResult();
            Assert.Equal(2, rig.Vm.SkillRows.Count);
            Assert.Empty(rig.Vm.McpRows);
            Assert.Contains("MCP servers: This gateway does not offer", rig.Vm.CapabilitiesStatus);
        });
    }

    [Fact]
    public void In_simple_view_a_capability_switch_or_mcp_test_sends_nothing_whoever_asks()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.RefreshCapabilitiesAsync().GetAwaiter().GetResult();
            var skill = rig.Vm.SkillRows.First(r => r.Name == "a-skill");
            rig.Vm.ApplyExperience(false);
            rig.Seen.Clear();
            rig.Vm.ToggleCapabilityAsync("default", skill).GetAwaiter().GetResult();
            rig.Vm.TestMcpAsync("default", rig.Vm.McpRows.First()).GetAwaiter().GetResult();
            Assert.Empty(rig.Seen);
        });
    }

    [Fact]
    public void A_skill_switch_needs_no_confirmation_and_flips_only_after_the_gateway_accepts()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.RefreshCapabilitiesAsync().GetAwaiter().GetResult();
            var skill = rig.Vm.SkillRows.First(r => r.Name == "a-skill");
            rig.Seen.Clear();

            rig.Reply = _ => Json(HttpStatusCode.InternalServerError, """{"detail":"config locked"}""");
            rig.Vm.ToggleCapabilityAsync("default", skill).GetAwaiter().GetResult();
            Assert.False(skill.Enabled);
            Assert.Equal("config locked", rig.Vm.CapabilitiesStatus);

            rig.Reply = _ => Json(HttpStatusCode.OK, "{}");
            rig.Vm.ToggleCapabilityAsync("default", skill).GetAwaiter().GetResult();
            Assert.True(skill.Enabled);
            Assert.Equal("""{"name":"a-skill","enabled":true}""", rig.Seen.Last().Body);
            Assert.Empty(rig.Asked);
            Assert.False(skill.Busy);
        });
    }

    [Fact]
    public void Turning_a_toolset_or_server_on_is_confirmed_and_turning_off_is_not()
    {
        RunSta(() =>
        {
            using var rig = new Rig(confirm: false);
            rig.Vm.RefreshCapabilitiesAsync().GetAwaiter().GetResult();
            var web = rig.Vm.ToolsetRows.First(r => r.Name == "web");
            var git = rig.Vm.McpRows.First(r => r.Name == "git");
            rig.Seen.Clear();

            rig.Vm.ToggleCapabilityAsync("default", web).GetAwaiter().GetResult();
            rig.Vm.ToggleCapabilityAsync("default", git).GetAwaiter().GetResult();
            Assert.Empty(rig.Seen);                              // declined: nothing sent
            Assert.False(web.Enabled);
            Assert.Contains("Web", rig.Asked[0]);
            Assert.Contains("uvx mcp-git", rig.Asked[1]);        // names what it connects to

            rig.Vm.ConfirmCapability = null;                     // no handler: also nothing
            rig.Vm.ToggleCapabilityAsync("default", web).GetAwaiter().GetResult();
            Assert.Empty(rig.Seen);

            rig.Vm.ConfirmCapability = (_, m) => { rig.Asked.Add(m); return true; };
            rig.Vm.ToggleCapabilityAsync("default", web).GetAwaiter().GetResult();
            Assert.True(web.Enabled);
            Assert.Equal("/api/tools/toolsets/web?profile=default", rig.Seen.Single().PathAndQuery);

            var asked = rig.Asked.Count;
            rig.Vm.ToggleCapabilityAsync("default", web).GetAwaiter().GetResult();    // off
            Assert.False(web.Enabled);
            Assert.Equal(asked, rig.Asked.Count);
        });
    }

    [Fact]
    public void An_unconfigured_toolset_cannot_be_turned_on_but_an_enabled_one_can_be_turned_off_and_plugin_servers_are_locked()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.RefreshCapabilitiesAsync().GetAwaiter().GetResult();
            var browser = rig.Vm.ToolsetRows.First(r => r.Name == "browser");
            var terminal = rig.Vm.ToolsetRows.First(r => r.Name == "terminal");
            var docs = rig.Vm.McpRows.First(r => r.Name == "docs");
            Assert.False(browser.ToggleCommand.CanExecute(null));
            Assert.Contains("Needs setup", browser.Note);
            Assert.True(terminal.ToggleCommand.CanExecute(null));
            Assert.False(docs.ToggleCommand.CanExecute(null));
            Assert.Contains("herald", docs.Note);
        });
    }

    [Fact]
    public void An_mcp_test_is_confirmed_and_reports_both_success_and_a_failed_connection()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.RefreshCapabilitiesAsync().GetAwaiter().GetResult();
            var git = rig.Vm.McpRows.First(r => r.Name == "git");
            rig.Seen.Clear();

            rig.Reply = _ => Json(HttpStatusCode.OK, """{"ok":false,"error":"spawn failed"}""");
            rig.Vm.TestMcpAsync("default", git).GetAwaiter().GetResult();
            Assert.Equal(HttpMethod.Post, rig.Seen.Single().Method);
            Assert.Equal("/api/mcp/servers/git/test?profile=default", rig.Seen.Single().PathAndQuery);
            Assert.Contains("spawn failed", rig.Shown.Single().Text);

            rig.Reply = _ => Json(HttpStatusCode.OK, """{"ok":true,"tools":[{"name":"commit"}]}""");
            rig.Vm.TestMcpAsync("default", git).GetAwaiter().GetResult();
            Assert.Contains("• commit", rig.Shown.Last().Text);

            rig.Seen.Clear();
            rig.Vm.ConfirmCapability = (_, _) => false;
            rig.Vm.TestMcpAsync("default", git).GetAwaiter().GetResult();
            Assert.Empty(rig.Seen);                              // the test starts a process on the gateway: asked first
        });
    }

    [Fact]
    public void An_unselected_profile_is_never_touched()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.RefreshCapabilitiesAsync().GetAwaiter().GetResult();
            var skill = rig.Vm.SkillRows[0];
            rig.Seen.Clear();
            rig.Vm.ToggleCapabilityAsync("private", skill).GetAwaiter().GetResult();
            rig.Vm.TestMcpAsync("private", rig.Vm.McpRows[0]).GetAwaiter().GetResult();
            Assert.Empty(rig.Seen);
        });
    }

    [Fact]
    public void The_palette_offers_capabilities_in_advanced_only_maintainer_2026_10_27()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            foreach (var advanced in new[] { true, false })
            {
                rig.Vm.ApplyExperience(advanced);
                rig.Vm.OpenPaletteCommand!.Execute(null);
                rig.Vm.PaletteQuery = "capabilities";
                Assert.Equal(advanced, rig.Vm.PaletteResults.Any(r => r.Id == "nav.capabilities"));   // Simple hides it (Desktop keeps it; the maintainer chose otherwise)
                rig.Vm.ClosePaletteCommand!.Execute(null);
            }
        });
    }
}

file sealed class MemPrefs24 : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
