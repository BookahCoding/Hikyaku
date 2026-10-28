using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 35a — the skills hub tab through the real MainViewModel (fixture HTTP; read-only; no live gateway).</summary>
[Collection("StaMath")]
public class Checkpoint35SkillsHubWpfTests
{
    private const string Sources = """
        {"sources":[{"id":"official","label":"Official"},{"id":"github","label":"GitHub","rate_limited":true}],
         "featured":[{"name":"deploy","description":"Ship it","source":"official","identifier":"official/devops/deploy","trust_level":"builtin"},
                     {"name":"notes","description":"Notes","source":"github","identifier":"github/x/notes","trust_level":"community"}],
         "installed":{"github/x/notes":{"name":"notes"}}}
        """;

    private sealed class Gateway : HttpMessageHandler
    {
        public readonly List<string> Seen = new();
        public Func<string, HttpResponseMessage> Reply = url =>
            url.Contains("/hub/sources") ? Json(HttpStatusCode.OK, Sources)
            : url.Contains("/hub/search") ? Json(HttpStatusCode.OK, """{"results":[{"name":"pdf-tools","identifier":"github/y/pdf-tools"}],"timed_out":["clawhub"],"installed":{}}""")
            : url.Contains("/hub/preview") ? Json(HttpStatusCode.OK, """{"name":"deploy","identifier":"official/devops/deploy","tags":["ops"],"skill_md":"---\nname: deploy\n---\n# Deploy\nRun it.","files":["SKILL.md","run.sh"]}""")
            : url.Contains("/hub/scan") ? Json(HttpStatusCode.OK, """{"verdict":"caution","policy":"ask","policy_reason":"community source","summary":"1 pattern","findings":[{"severity":"medium","category":"network","file":"run.sh","line":4,"description":"curl | sh"}]}""")
            : Json(HttpStatusCode.NotFound, "{}");
        public TaskCompletionSource? HoldPreview;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.PathAndQuery;
            Seen.Add(request.Method + " " + url);
            if (url.Contains("/hub/preview") && HoldPreview is { } h) await h.Task;
            return Reply(url);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Rig
    {
        public MainViewModel Vm;
        public Gateway Gw = new();

        public Rig() : this([new ProfileKey("gw", "default")]) { }

        public Rig(IEnumerable<ProfileKey> chosen)
        {
            var selection = new ProfileSelectionService(new MemPrefs35());
            selection.SetSelectedAsync(chosen).GetAwaiter().GetResult();
            var auth = new DashboardAuth(new Uri("https://hermes.example/"), Gw);
            Vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
                () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
            Vm.AttachCron(() => null, "gw");
            Vm.AttachSkillsHub(() => new SkillsHubApi(auth));
        }
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
    public void Browse_lists_the_featured_skills_marks_the_installed_one_and_names_a_source_that_is_not_answering()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.BrowseHubAsync().GetAwaiter().GetResult();
            Assert.Equal("GET /api/skills/hub/sources?profile=default", Assert.Single(rig.Gw.Seen));
            Assert.Equal(["deploy", "notes"], rig.Vm.HubRows.Select(r => r.Name));
            Assert.Equal("official · built in", rig.Vm.HubRows[0].Meta);
            Assert.Equal("github · community · installed", rig.Vm.HubRows[1].Meta);          // marked from the profile's installed list
            Assert.Contains("GitHub", rig.Vm.HubNote);
            Assert.Equal("2 featured skill(s).", rig.Vm.HubStatus);
        });
    }

    [Fact]
    public void Search_needs_text_sends_it_and_reports_slow_sources()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            Assert.False(rig.Vm.SearchHubCommand!.CanExecute(null));
            rig.Vm.HubQuery = "  pdf ";
            Assert.True(rig.Vm.SearchHubCommand.CanExecute(null));
            rig.Vm.SearchHubAsync().GetAwaiter().GetResult();
            var q = System.Web.HttpUtility.ParseQueryString(new Uri("https://x" + rig.Gw.Seen.Single().Split(' ')[1]).Query);
            Assert.Equal(("pdf", "default"), (q["q"], q["profile"]));
            Assert.Equal(["pdf-tools"], rig.Vm.HubRows.Select(r => r.Name));
            Assert.Contains("clawhub", rig.Vm.HubNote);
            Assert.Contains("1 result(s) for “pdf”", rig.Vm.HubStatus);
        });
    }

    [Fact]
    public void Read_shows_the_skill_text_without_front_matter_and_its_files_and_back_returns_to_the_list()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.BrowseHubAsync().GetAwaiter().GetResult();
            rig.Vm.PreviewHubSkillAsync(rig.Vm.HubRows[0]).GetAwaiter().GetResult();
            var q = System.Web.HttpUtility.ParseQueryString(new Uri("https://x" + rig.Gw.Seen.Last().Split(' ')[1]).Query);
            Assert.Equal(("official/devops/deploy", "default"), (q["identifier"], q["profile"]));
            Assert.Equal("# Deploy\nRun it.", rig.Vm.HubDetailBody);
            Assert.Equal("Files: SKILL.md, run.sh", rig.Vm.HubDetailFiles);
            Assert.Contains("ops", rig.Vm.HubDetailMeta);
            Assert.Equal(Visibility.Visible, rig.Vm.HubDetailVisibility);
            Assert.Equal(Visibility.Collapsed, rig.Vm.HubListVisibility);

            rig.Vm.CloseHubDetailCommand!.Execute(null);
            Assert.Equal(Visibility.Visible, rig.Vm.HubListVisibility);
            Assert.Equal("", rig.Vm.HubDetailBody);
        });
    }

    [Fact]
    public void Safety_check_shows_the_verdict_what_the_gateway_would_do_and_each_finding()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.BrowseHubAsync().GetAwaiter().GetResult();
            rig.Vm.CheckHubSkillAsync(rig.Vm.HubRows[1]).GetAwaiter().GetResult();
            Assert.Contains("Safety check: caution", rig.Vm.HubScanVerdict);
            Assert.Contains("the gateway would ask before installing it", rig.Vm.HubScanVerdict);
            Assert.Contains("community source", rig.Vm.HubScanVerdict);
            var f = Assert.Single(rig.Vm.HubFindings);
            Assert.Equal(("medium · network · run.sh:4", "curl | sh"), (f.Header, f.Description));
            Assert.Equal(Visibility.Visible, rig.Vm.HubScanVisibility);
        });
    }

    [Fact]
    public void A_late_answer_for_a_skill_that_was_closed_is_dropped()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.BrowseHubAsync().GetAwaiter().GetResult();
            rig.Gw.HoldPreview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = rig.Vm.PreviewHubSkillAsync(rig.Vm.HubRows[0]);
            rig.Vm.CloseHubDetailCommand!.Execute(null);
            rig.Gw.HoldPreview.SetResult();
            pending.GetAwaiter().GetResult();
            Assert.Equal("", rig.Vm.HubDetailBody);
            Assert.Equal(Visibility.Visible, rig.Vm.HubListVisibility);
        });
    }

    [Fact]
    public void A_gateway_without_the_hub_a_missing_connection_and_an_unchosen_profile_each_say_so()
    {
        RunSta(() =>
        {
            var missing = new Rig();
            missing.Gw.Reply = _ => Json(HttpStatusCode.NotFound, "{}");
            missing.Vm.BrowseHubAsync().GetAwaiter().GetResult();
            Assert.Contains("does not offer the skills hub", missing.Vm.HubStatus);

            var none = new Rig([]);
            none.Vm.BrowseHubAsync().GetAwaiter().GetResult();
            Assert.Contains("Choose a profile", none.Vm.HubStatus);
            Assert.Empty(none.Gw.Seen);

            var offline = new Rig();
            offline.Vm.AttachSkillsHub(() => null);
            offline.Vm.BrowseHubAsync().GetAwaiter().GetResult();
            Assert.Contains("Connect to a gateway", offline.Vm.HubStatus);
        });
    }

    [Fact]
    public void Changing_profile_drops_the_old_profiles_installed_marks_before_anything_new_is_shown()
    {
        RunSta(() =>
        {
            var rig = new Rig([new ProfileKey("gw", "default"), new ProfileKey("gw2", "work")]);
            rig.Vm.BrowseHubAsync().GetAwaiter().GetResult();
            Assert.Equal(2, rig.Vm.HubRows.Count);
            rig.Vm.AttachCron(() => null, "gw2");
            rig.Gw.Reply = _ => Json(HttpStatusCode.InternalServerError, "{}");
            rig.Vm.BrowseHubAsync().GetAwaiter().GetResult();
            Assert.Equal("GET /api/skills/hub/sources?profile=work", rig.Gw.Seen.Last());
            Assert.Empty(rig.Vm.HubRows);
        });
    }

    [Fact]
    public void Nothing_but_reads_is_ever_sent_and_no_install_remove_or_update_route_is_touched()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.BrowseHubAsync().GetAwaiter().GetResult();
            rig.Vm.HubQuery = "x";
            rig.Vm.SearchHubAsync().GetAwaiter().GetResult();
            rig.Vm.PreviewHubSkillAsync(rig.Vm.HubRows[0]).GetAwaiter().GetResult();
            rig.Vm.CheckHubSkillAsync(rig.Vm.HubRows[0]).GetAwaiter().GetResult();
            Assert.All(rig.Gw.Seen, s => Assert.StartsWith("GET ", s));
            Assert.DoesNotContain(rig.Gw.Seen, s => s.Contains("/install") || s.Contains("/uninstall") || s.Contains("/update"));
        });
    }
}

file sealed class MemPrefs35 : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
