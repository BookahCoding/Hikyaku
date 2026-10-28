using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 35b — installing, removing and updating hub skills through the real MainViewModel: scan first, confirm in words, never override a block.</summary>
[Collection("StaMath")]
public class Checkpoint35SkillsHubWriteWpfTests
{
    private const string Sources = """
        {"sources":[{"id":"official","label":"Official"}],
         "featured":[{"name":"deploy","description":"Ship it","source":"official","identifier":"official/devops/deploy","trust_level":"builtin"},
                     {"name":"notes","source":"github","identifier":"github/x/notes","trust_level":"community"},
                     {"name":"pdf-tools","source":"github","identifier":"github/y/pdf-tools","trust_level":"community","installed":true}],
         "installed":{"github/y/pdf-tools":{"name":"pdf-tools"}}}
        """;

    private sealed class Gateway : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url, string Body)> Seen = new();
        public string Scan = """{"verdict":"safe","policy":"allow","summary":"no findings","trust_level":"community","source":"github","findings":[]}""";
        public bool ScanFails;
        public string Status = """{"running":false,"exit_code":0,"lines":["done"]}""";
        public TaskCompletionSource? HoldStatus;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.PathAndQuery;
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            Seen.Add((r.Method, url, body));
            if (r.Method == HttpMethod.Post) return Json("""{"name":"act-1","ok":true}""");
            if (url.Contains("/hub/sources")) return Json(Sources);
            if (url.Contains("/hub/scan")) return ScanFails ? Json("{}", HttpStatusCode.InternalServerError) : Json(Scan);
            if (url.Contains("/api/actions/"))
            {
                if (HoldStatus is { } h) await h.Task;
                return Json(Status);
            }
            return Json("{}", HttpStatusCode.NotFound);
        }

        public IEnumerable<(HttpMethod Method, string Url, string Body)> Posts => Seen.Where(s => s.Method == HttpMethod.Post);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Rig
    {
        public MainViewModel Vm;
        public Gateway Gw = new();
        public List<(string Title, string Message)> Asked = [];

        public Rig(bool confirm = true, bool chooseProfile = true, bool handler = true, IEnumerable<ProfileKey>? chosen = null, bool browse = true)
        {
            var selection = new ProfileSelectionService(new MemPrefs35w());
            selection.SetSelectedAsync(chosen ?? (chooseProfile ? [new ProfileKey("gw", "default")] : [])).GetAwaiter().GetResult();
            var auth = new DashboardAuth(new Uri("https://hermes.example/"), Gw);
            Vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
                () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
            Vm.AttachCron(() => null, "gw");
            Vm.AttachSkillsHub(() => new SkillsHubApi(auth));
            if (handler) Vm.ConfirmHub = (t, m) => { Asked.Add((t, m)); return confirm; };
            if (browse) Vm.BrowseHubAsync().GetAwaiter().GetResult();
        }

        public HubRowVM Row(string name) => Vm.HubRows.Single(r => r.Name == name);
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
    public void Install_scans_first_asks_in_words_then_posts_the_identifier_and_marks_it_installed()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Gw.Scan = """{"verdict":"caution","policy":"ask","policy_reason":"community source","summary":"1 pattern","trust_level":"community","source":"github","findings":[{"severity":"medium","category":"network","file":"run.sh","line":4,"description":"curl | sh"}]}""";
            var row = rig.Row("notes");
            Assert.True(row.InstallCommand.CanExecute(null));
            rig.Vm.InstallHubSkillAsync(row).GetAwaiter().GetResult();

            var (title, message) = Assert.Single(rig.Asked);
            Assert.Equal("Install skill", title);
            Assert.Contains("Install “notes” on your gateway?", message);
            Assert.Contains("Source: github · trust: community", message);
            Assert.Contains("Safety check: caution — the gateway would ask before installing it (community source)", message);
            Assert.Contains("medium in run.sh:4: curl | sh", message);
            Assert.Contains("run its files", message);

            var post = Assert.Single(rig.Gw.Posts);
            Assert.Equal("/api/skills/hub/install", post.Url);
            var body = JsonNode.Parse(post.Body)!;
            Assert.Equal(("github/x/notes", "default"), ((string)body["identifier"]!, (string)body["profile"]!));
            Assert.True(row.Installed);
            Assert.Equal("Installed notes. It applies from the next chat.", rig.Vm.HubMessage);
            // the scan went before the install
            Assert.True(rig.Gw.Seen.FindIndex(s => s.Url.Contains("/hub/scan")) < rig.Gw.Seen.FindIndex(s => s.Method == HttpMethod.Post));
        });
    }

    [Fact]
    public void Even_an_official_skill_is_scanned_and_confirmed()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.InstallHubSkillAsync(rig.Row("deploy")).GetAwaiter().GetResult();
            Assert.Single(rig.Asked);
            Assert.Contains(rig.Gw.Seen, s => s.Url.Contains("/hub/scan"));
            Assert.Contains("Source: official", rig.Asked[0].Message);
            Assert.Single(rig.Gw.Posts);
        });
    }

    [Fact]
    public void Without_a_yes_or_without_a_confirmation_handler_nothing_is_sent()
    {
        RunSta(() =>
        {
            var no = new Rig(confirm: false);
            no.Vm.InstallHubSkillAsync(no.Row("notes")).GetAwaiter().GetResult();
            Assert.Empty(no.Gw.Posts);
            Assert.False(no.Row("notes").Installed);
            Assert.Equal("notes was not installed.", no.Vm.HubMessage);

            var unset = new Rig(handler: false);
            unset.Vm.InstallHubSkillAsync(unset.Row("notes")).GetAwaiter().GetResult();
            unset.Vm.UninstallHubSkillAsync(unset.Row("pdf-tools")).GetAwaiter().GetResult();
            unset.Vm.UpdateAllHubAsync().GetAwaiter().GetResult();
            Assert.Empty(unset.Gw.Posts);
        });
    }

    [Fact]
    public void A_block_policy_is_never_overridden_the_person_is_not_even_asked()
    {
        RunSta(() =>
        {
            var rig = new Rig(confirm: true);                                           // a handler that would say yes to anything
            rig.Gw.Scan = """{"verdict":"dangerous","policy":"block","policy_reason":"dangerous verdict","summary":"2 high-risk patterns","findings":[{"severity":"high","category":"exfiltration","file":"run.sh","line":1,"description":"curl | sh"}]}""";
            var row = rig.Row("notes");
            rig.Vm.InstallHubSkillAsync(row).GetAwaiter().GetResult();
            Assert.Empty(rig.Asked);
            Assert.Empty(rig.Gw.Posts);
            Assert.False(row.Installed);
            Assert.Contains("the gateway would refuse it", rig.Vm.HubMessage);
            Assert.Contains("the gateway would refuse to install it", rig.Vm.HubScanVerdict);   // and the scan is shown
            Assert.Single(rig.Vm.HubFindings);
        });
    }

    [Fact]
    public void If_the_safety_check_itself_fails_nothing_is_installed()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Gw.ScanFails = true;
            rig.Vm.InstallHubSkillAsync(rig.Row("notes")).GetAwaiter().GetResult();
            Assert.Empty(rig.Asked);
            Assert.Empty(rig.Gw.Posts);
            Assert.Contains("so it was not installed", rig.Vm.HubMessage);
        });
    }

    [Fact]
    public void A_scan_refusal_during_the_install_is_reported_as_blocked_and_a_plain_failure_shows_the_last_log_line()
    {
        RunSta(() =>
        {
            var blocked = new Rig();
            blocked.Gw.Status = """{"running":false,"exit_code":1,"lines":["Not installed: the security scan found 3 high-risk patterns"]}""";
            blocked.Vm.InstallHubSkillAsync(blocked.Row("notes")).GetAwaiter().GetResult();
            Assert.False(blocked.Row("notes").Installed);
            Assert.Contains("blocked by the security scan (3 high-risk finding(s))", blocked.Vm.HubMessage);

            var failed = new Rig();
            failed.Gw.Status = """{"running":false,"exit_code":2,"lines":["fetching","error: repository not found"]}""";
            failed.Vm.InstallHubSkillAsync(failed.Row("notes")).GetAwaiter().GetResult();
            Assert.False(failed.Row("notes").Installed);
            Assert.Equal("Couldn't install notes: error: repository not found", failed.Vm.HubMessage);
        });
    }

    [Fact]
    public void Remove_names_the_skill_asks_first_and_posts_its_name()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            var row = rig.Row("pdf-tools");
            Assert.True(row.UninstallCommand.CanExecute(null));
            Assert.False(row.InstallCommand.CanExecute(null));                          // an installed skill cannot be installed again
            rig.Vm.UninstallHubSkillAsync(row).GetAwaiter().GetResult();
            var (title, message) = Assert.Single(rig.Asked);
            Assert.Equal("Remove skill", title);
            Assert.Contains("“pdf-tools”", message);
            Assert.Contains("deletes its files", message);
            var post = Assert.Single(rig.Gw.Posts);
            Assert.Equal(("/api/skills/hub/uninstall", "pdf-tools"), (post.Url, (string)JsonNode.Parse(post.Body)!["name"]!));
            Assert.False(row.Installed);
            Assert.Equal("Removed pdf-tools. It applies from the next chat.", rig.Vm.HubMessage);

            var no = new Rig(confirm: false);
            no.Vm.UninstallHubSkillAsync(no.Row("pdf-tools")).GetAwaiter().GetResult();
            Assert.Empty(no.Gw.Posts);
            Assert.True(no.Row("pdf-tools").Installed);
        });
    }

    [Fact]
    public void In_simple_view_hub_installs_removals_and_updates_send_nothing_whoever_asks()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            var installed = rig.Row("pdf-tools");
            var fresh = rig.Row("notes");
            rig.Vm.ApplyExperience(false);
            rig.Vm.InstallHubSkillAsync(fresh).GetAwaiter().GetResult();
            rig.Vm.UninstallHubSkillAsync(installed).GetAwaiter().GetResult();
            rig.Vm.UpdateAllHubAsync().GetAwaiter().GetResult();
            Assert.Empty(rig.Gw.Posts);
            Assert.Empty(rig.Asked);
        });
    }

    [Fact]
    public void Update_all_works_the_first_time_even_before_the_hub_was_browsed()
    {
        RunSta(() =>
        {
            var rig = new Rig(browse: false);
            rig.Vm.UpdateAllHubAsync().GetAwaiter().GetResult();
            Assert.Single(rig.Asked);                                                    // it asked, rather than refusing for a "profile change"
            Assert.DoesNotContain("profile changed", rig.Vm.HubStatus);
        });
    }

    [Fact]
    public void A_row_listed_for_one_profile_is_never_acted_on_under_another()
    {
        RunSta(() =>
        {
            var rig = new Rig(chosen: [new ProfileKey("gw", "default"), new ProfileKey("gw2", "work")]);
            var installed = rig.Row("pdf-tools");
            var fresh = rig.Row("notes");
            rig.Vm.AttachCron(() => null, "gw2");                                        // the active profile is now "work"; the old rows are still in the list
            rig.Vm.UninstallHubSkillAsync(installed).GetAwaiter().GetResult();
            rig.Vm.InstallHubSkillAsync(fresh).GetAwaiter().GetResult();
            rig.Vm.UninstallHubSkillAsync(installed).GetAwaiter().GetResult();          // asking again after the list was cleared must not slip through
            Assert.Empty(rig.Gw.Posts);
            Assert.Empty(rig.Asked);                                                     // not even asked
            Assert.Empty(rig.Vm.HubRows);
            Assert.Contains("profile changed", rig.Vm.HubStatus);
        });
    }

    [Fact]
    public void Update_all_asks_first_and_posts_only_the_profile()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.UpdateAllHubAsync().GetAwaiter().GetResult();
            Assert.Contains("third parties", Assert.Single(rig.Asked).Message);
            var post = Assert.Single(rig.Gw.Posts);
            Assert.Equal("/api/skills/hub/update", post.Url);
            Assert.Equal("default", (string)JsonNode.Parse(post.Body)!["profile"]!);
            Assert.Equal("Skills updated. It applies from the next chat.", rig.Vm.HubMessage);

            var no = new Rig(confirm: false);
            no.Vm.UpdateAllHubAsync().GetAwaiter().GetResult();
            Assert.Empty(no.Gw.Posts);
        });
    }

    [Fact]
    public void Only_one_write_runs_at_a_time()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Gw.HoldStatus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = rig.Vm.InstallHubSkillAsync(rig.Row("notes"));                  // running on the gateway
            Assert.True(rig.Vm.HubActing);
            Assert.False(rig.Row("pdf-tools").UninstallCommand.CanExecute(null));
            Assert.False(rig.Vm.UpdateAllHubCommand!.CanExecute(null));
            rig.Vm.UpdateAllHubAsync().GetAwaiter().GetResult();                       // a second write is ignored, not queued
            rig.Vm.UninstallHubSkillAsync(rig.Row("pdf-tools")).GetAwaiter().GetResult();
            Assert.Single(rig.Gw.Posts);

            rig.Gw.HoldStatus.SetResult();
            first.GetAwaiter().GetResult();
            Assert.False(rig.Vm.HubActing);
            Assert.True(rig.Vm.UpdateAllHubCommand.CanExecute(null));
        });
    }

    [Fact]
    public void Installing_what_is_installed_or_removing_what_is_not_does_nothing_even_when_called_directly()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            var scansBefore = rig.Gw.Seen.Count;
            rig.Vm.InstallHubSkillAsync(rig.Row("pdf-tools")).GetAwaiter().GetResult();      // already installed
            rig.Vm.UninstallHubSkillAsync(rig.Row("notes")).GetAwaiter().GetResult();        // not installed
            Assert.Equal(scansBefore, rig.Gw.Seen.Count);                                    // no scan, no write
            Assert.Empty(rig.Asked);
        });
    }

    [Fact]
    public void A_second_write_while_one_is_running_is_ignored_for_removal_and_update_too()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Gw.HoldStatus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = rig.Vm.UpdateAllHubAsync();                                          // running on the gateway
            rig.Vm.UpdateAllHubAsync().GetAwaiter().GetResult();
            rig.Vm.UninstallHubSkillAsync(rig.Row("pdf-tools")).GetAwaiter().GetResult();
            rig.Vm.InstallHubSkillAsync(rig.Row("notes")).GetAwaiter().GetResult();
            Assert.Single(rig.Gw.Posts);
            Assert.Single(rig.Asked);                                                        // the ignored ones did not even ask
            rig.Gw.HoldStatus.SetResult();
            first.GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void With_no_chosen_profile_nothing_is_scanned_or_sent()
    {
        RunSta(() =>
        {
            var rig = new Rig(chooseProfile: false);
            Assert.Empty(rig.Vm.HubRows);
            Assert.Empty(rig.Gw.Seen);
            rig.Vm.UpdateAllHubAsync().GetAwaiter().GetResult();
            Assert.Empty(rig.Gw.Seen);
            Assert.Empty(rig.Asked);
        });
    }
}

file sealed class MemPrefs35w : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
