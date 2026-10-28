using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 37 — config.yaml editor and MCP add / edit through the real MainViewModel. Advanced only; every write confirmed; fake gateway.</summary>
[Collection("StaMath")]
public class Checkpoint37ConfigEditorWpfTests
{
    private sealed class Gateway : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url, string Body)> Seen = new();
        public string Yaml = "model: x\nmcp_servers: {}\n";
        public bool RefuseRawSave;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.PathAndQuery;
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            Seen.Add((r.Method, url, body));
            if (url.StartsWith("/api/config/raw", StringComparison.Ordinal))
            {
                if (r.Method == HttpMethod.Put)
                {
                    if (RefuseRawSave) return Json("{\"detail\":\"yaml_text must be a YAML mapping\"}", HttpStatusCode.BadRequest);
                    Yaml = (string)JsonNode.Parse(body)!["yaml_text"]!;
                    return Json("{}");
                }
                return Json(new JsonObject { ["yaml"] = Yaml, ["path"] = "/home/u/.hermes/config.yaml" }.ToJsonString());
            }
            return Json("{}");
        }

        public IEnumerable<(HttpMethod Method, string Url, string Body)> Puts => Seen.Where(s => s.Method == HttpMethod.Put);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Rig
    {
        public MainViewModel Vm;
        public Gateway Gw = new();
        public List<(string Title, string Message)> Asked = [];
        public string BackupDir = Directory.CreateTempSubdirectory("hikyaku-cfg-").FullName;

        public Rig(bool advanced = true, bool confirm = true, bool handler = true, IEnumerable<ProfileKey>? chosen = null)
        {
            var selection = new ProfileSelectionService(new MemPrefs37());
            selection.SetSelectedAsync(chosen ?? [new ProfileKey("gw", "default")]).GetAwaiter().GetResult();
            var auth = new DashboardAuth(new Uri("https://hermes.example/"), Gw);
            Vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
                () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
            Vm.AttachCron(() => null, "gw");
            Vm.AttachConfigEditor(() => new GatewayConfigEditor(auth));
            Vm.ApplyExperience(advanced);
            Vm.ConfigBackupDirectory = BackupDir;
            if (handler) Vm.ConfirmConfig = (t, m) => { Asked.Add((t, m)); return confirm; };
        }

        public void Load() => Vm.LoadConfigAsync().GetAwaiter().GetResult();
        public void FillLocalServer(string name = "files", string env = "", bool listLoaded = true)
        {
            if (listLoaded) Vm.RememberMcpServers([]);                                     // the gateway's server list has been read (none yet)
            Vm.AddMcpServerCommand!.Execute(null);
            Vm.McpName = name; Vm.McpCommand = "npx"; Vm.McpArgs = "-y\n@modelcontextprotocol/server-filesystem\n/data"; Vm.McpEnv = env;
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

    // ---- Simple view never reaches it ----

    [Fact]
    public void In_simple_view_nothing_can_be_loaded_saved_or_added_whoever_asks()
    {
        RunSta(() =>
        {
            var rig = new Rig(advanced: false);
            Assert.Equal(Visibility.Collapsed, rig.Vm.ConfigEditorVisibility);
            Assert.Equal(Visibility.Collapsed, rig.Vm.McpEditVisibility);
            Assert.False(rig.Vm.AddMcpServerCommand!.CanExecute(null));
            rig.Vm.LoadConfigAsync().GetAwaiter().GetResult();
            rig.Vm.ConfigYaml = "changed";
            rig.Vm.SaveConfigAsync().GetAwaiter().GetResult();
            rig.Vm.AddMcpServerCommand.Execute(null);
            rig.Vm.SaveMcpServerAsync().GetAwaiter().GetResult();
            Assert.Empty(rig.Gw.Seen);
            Assert.False(rig.Vm.McpFormOpen);
            Assert.Empty(rig.Asked);

            var adv = new Rig(advanced: true);
            Assert.Equal(Visibility.Visible, adv.Vm.ConfigEditorVisibility);
            Assert.True(adv.Vm.AddMcpServerCommand!.CanExecute(null));
        });
    }

    // ---- raw config.yaml ----

    [Fact]
    public void Load_shows_the_file_and_where_it_lives_for_the_active_profile()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Load();
            Assert.Equal("/api/config/raw?profile=default", Assert.Single(rig.Gw.Seen).Url);
            Assert.Equal("model: x\nmcp_servers: {}\n", rig.Vm.ConfigYaml);
            Assert.Equal("/home/u/.hermes/config.yaml", rig.Vm.ConfigPath);
            Assert.False(rig.Vm.ConfigDirty);
            Assert.False(rig.Vm.SaveConfigCommand!.CanExecute(null));                     // nothing to save yet
        });
    }

    [Fact]
    public void Save_checks_the_file_is_unchanged_asks_keeps_a_copy_of_the_old_text_then_replaces_it()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Load();
            rig.Vm.ConfigYaml = "model: y\nmcp_servers: {}\n";
            Assert.True(rig.Vm.ConfigDirty);
            rig.Vm.SaveConfigAsync().GetAwaiter().GetResult();

            var (title, message) = Assert.Single(rig.Asked);
            Assert.Equal("Replace configuration", title);
            Assert.Contains("profile “default”", message);
            Assert.Contains("Everyone and every client using this profile is affected", message);
            var put = Assert.Single(rig.Gw.Puts);
            Assert.Equal("/api/config/raw?profile=default", put.Url);
            Assert.Equal("model: y\nmcp_servers: {}\n", (string)JsonNode.Parse(put.Body)!["yaml_text"]!);
            // a copy of the OLD text was kept on this PC before the write
            var backup = Assert.Single(Directory.GetFiles(rig.BackupDir));
            Assert.Equal("model: x\nmcp_servers: {}\n", File.ReadAllText(backup));
            Assert.StartsWith("default-", Path.GetFileName(backup));
            Assert.False(rig.Vm.ConfigDirty);                                             // re-read from the gateway
            Assert.Contains("Saved", rig.Vm.ConfigStatus);
            Assert.Contains(backup, rig.Vm.ConfigStatus);
        });
    }

    [Fact]
    public void A_stale_editor_never_overwrites_a_file_that_changed_on_the_gateway_meanwhile()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Load();
            rig.Gw.Yaml = "model: changed-elsewhere\n";                                   // someone else saved
            rig.Vm.ConfigYaml = "model: mine\n";
            rig.Vm.SaveConfigAsync().GetAwaiter().GetResult();
            Assert.Empty(rig.Gw.Puts);
            Assert.Empty(rig.Asked);                                                      // refused before even asking
            Assert.Empty(Directory.GetFiles(rig.BackupDir));
            Assert.Contains("changed on the gateway since you loaded it", rig.Vm.ConfigStatus);
            Assert.Equal("model: mine\n", rig.Vm.ConfigYaml);                             // the person's edit is not thrown away
        });
    }

    [Fact]
    public void Without_a_yes_or_a_confirmation_handler_nothing_is_written_and_no_copy_is_made()
    {
        RunSta(() =>
        {
            var no = new Rig(confirm: false);
            no.Load(); no.Vm.ConfigYaml = "x: 1\n";
            no.Vm.SaveConfigAsync().GetAwaiter().GetResult();
            Assert.Empty(no.Gw.Puts);
            Assert.Empty(Directory.GetFiles(no.BackupDir));
            Assert.Equal("Nothing was saved.", no.Vm.ConfigStatus);

            var unset = new Rig(handler: false);
            unset.Load(); unset.Vm.ConfigYaml = "x: 1\n";
            unset.Vm.SaveConfigAsync().GetAwaiter().GetResult();
            Assert.Empty(unset.Gw.Puts);
        });
    }

    [Fact]
    public void If_no_safety_copy_can_be_kept_nothing_is_changed()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            var blocker = Path.Combine(Path.GetTempPath(), "hikyaku-not-a-dir-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(blocker, "a file where the backup folder should be");
            rig.Vm.ConfigBackupDirectory = blocker;
            rig.Load(); rig.Vm.ConfigYaml = "x: 1\n";
            rig.Vm.SaveConfigAsync().GetAwaiter().GetResult();
            Assert.Empty(rig.Gw.Puts);
            Assert.Contains("so nothing was changed", rig.Vm.ConfigStatus);
        });
    }

    [Fact]
    public void A_refusal_from_the_gateway_is_shown_and_the_edit_stays_in_the_box()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Load();
            rig.Gw.RefuseRawSave = true;
            rig.Vm.ConfigYaml = "- not\n- a mapping\n";
            rig.Vm.SaveConfigAsync().GetAwaiter().GetResult();
            Assert.StartsWith("Not saved: The gateway refused it", rig.Vm.ConfigStatus);
            Assert.Equal("- not\n- a mapping\n", rig.Vm.ConfigYaml);
            Assert.True(rig.Vm.ConfigDirty);
            Assert.Equal("model: x\nmcp_servers: {}\n", rig.Gw.Yaml);                    // the gateway's file is untouched
        });
    }

    [Fact]
    public void Saving_before_loading_is_refused_so_there_is_always_a_copy_and_a_baseline()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.ConfigYaml = "x: 1\n";
            rig.Vm.SaveConfigAsync().GetAwaiter().GetResult();
            Assert.Empty(rig.Gw.Seen);
            Assert.Contains("Load the configuration first", rig.Vm.ConfigStatus);
        });
    }

    [Fact]
    public void Changing_profile_never_shows_one_profiles_file_under_another()
    {
        RunSta(() =>
        {
            var rig = new Rig(chosen: [new ProfileKey("gw", "default"), new ProfileKey("gw2", "work")]);
            rig.Load();
            Assert.NotEqual("", rig.Vm.ConfigYaml);
            rig.Vm.AttachCron(() => null, "gw2");
            rig.Vm.LoadConfigAsync().GetAwaiter().GetResult();
            Assert.Equal("/api/config/raw?profile=work", rig.Gw.Seen.Last().Url);
        });
    }

    // ---- MCP ----

    [Fact]
    public void A_bad_form_shows_its_problems_and_sends_nothing()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.RememberMcpServers([]);
            rig.Vm.AddMcpServerCommand!.Execute(null);
            Assert.True(rig.Vm.McpFormOpen);
            rig.Vm.McpName = "bad name!"; rig.Vm.McpCommand = ""; rig.Vm.McpTimeout = "soon";
            rig.Vm.SaveMcpServerAsync().GetAwaiter().GetResult();
            Assert.Contains("name must be", rig.Vm.McpProblems);
            Assert.Contains("needs a command", rig.Vm.McpProblems);
            Assert.Contains("whole number of seconds", rig.Vm.McpProblems);
            Assert.Empty(rig.Gw.Seen);
            Assert.Empty(rig.Asked);
        });
    }

    [Fact]
    public void Adding_a_server_asks_in_words_then_sends_only_that_servers_patch()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.FillLocalServer(env: "REGION=eu");
            rig.Vm.SaveMcpServerAsync().GetAwaiter().GetResult();
            var (title, message) = Assert.Single(rig.Asked);
            Assert.Equal("Add MCP server", title);
            Assert.Contains("“files” in profile “default”", message);
            Assert.Contains("Runs: npx -y @modelcontextprotocol/server-filesystem /data", message);
            Assert.Contains("runs on the gateway machine", message);
            Assert.DoesNotContain("plain text", message);                                 // no secret-looking value, no warning

            var put = Assert.Single(rig.Gw.Puts);
            Assert.Equal("/api/config?profile=default", put.Url);
            var server = JsonNode.Parse(put.Body)!["config"]!["mcp_servers"]!["files"]!;
            Assert.Equal("npx", (string)server["command"]!);
            Assert.Equal("eu", (string)server["env"]!["REGION"]!);
            Assert.Single(JsonNode.Parse(put.Body)!["config"]!.AsObject());               // nothing but mcp_servers
            Assert.False(rig.Vm.McpFormOpen);
            Assert.Contains("Added “files”", rig.Vm.ConfigStatus);
        });
    }

    [Fact]
    public void A_literal_secret_is_called_out_in_the_confirmation()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.FillLocalServer(env: "GITHUB_TOKEN=ghp_literal");
            rig.Vm.SaveMcpServerAsync().GetAwaiter().GetResult();
            var message = Assert.Single(rig.Asked).Message;
            Assert.Contains("GITHUB_TOKEN", message);
            Assert.Contains("plain text", message);
            Assert.Contains("${NAME}", message);

            var ok = new Rig();
            ok.FillLocalServer(env: "GITHUB_TOKEN=${GITHUB_TOKEN}");
            ok.Vm.SaveMcpServerAsync().GetAwaiter().GetResult();
            Assert.DoesNotContain("plain text", Assert.Single(ok.Asked).Message);
        });
    }

    [Fact]
    public void Without_a_yes_or_a_handler_no_server_is_written()
    {
        RunSta(() =>
        {
            var no = new Rig(confirm: false);
            no.FillLocalServer();
            no.Vm.SaveMcpServerAsync().GetAwaiter().GetResult();
            Assert.Empty(no.Gw.Puts);
            Assert.True(no.Vm.McpFormOpen);                                               // the form stays so nothing typed is lost

            var unset = new Rig(handler: false);
            unset.FillLocalServer();
            unset.Vm.SaveMcpServerAsync().GetAwaiter().GetResult();
            Assert.Empty(unset.Gw.Puts);
        });
    }

    [Fact]
    public void Editing_prefills_from_the_listed_server_locks_the_name_and_never_shows_secrets()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.RememberMcpServers([new McpServerInfo("remote", "http", "https://mcp.example.com/mcp", null, [], false, null)]);
            var row = new CapabilityRowVM("mcp", "remote", "remote", "https://mcp.example.com/mcp", "http", false, "", true, _ => { });
            Assert.True(rig.Vm.EditMcpServerCommand!.CanExecute(row));
            rig.Vm.EditMcpServerCommand.Execute(row);
            Assert.True(rig.Vm.McpFormOpen && rig.Vm.McpEditing && !rig.Vm.McpNameEditable);
            Assert.Equal(("remote", true, "https://mcp.example.com/mcp", false), (rig.Vm.McpName, rig.Vm.McpRemote, rig.Vm.McpUrl, rig.Vm.McpEnabled));
            Assert.Equal("", rig.Vm.McpHeaders);                                          // headers may hold secrets: never shown
            Assert.Equal("", rig.Vm.McpEnv);                                              // nor environment variables
            Assert.Contains("not shown", rig.Vm.McpNote);

            var locked = new CapabilityRowVM("mcp", "plugin-one", "plugin-one", "x", "http", true, "Provided by a plugin", false, _ => { });
            Assert.False(rig.Vm.EditMcpServerCommand.CanExecute(locked));                // a plugin's server cannot be edited here
        });
    }

    // ---- advisor review fixes ----

    [Fact]
    public void Adding_a_name_that_already_exists_is_refused_instead_of_silently_merging_into_that_server()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.RememberMcpServers([new McpServerInfo("files", "stdio", null, "npx", ["-y", "x"], true, null)]);
            rig.Vm.AddMcpServerCommand!.Execute(null);
            rig.Vm.McpName = "files"; rig.Vm.McpCommand = "other";
            rig.Vm.SaveMcpServerAsync().GetAwaiter().GetResult();
            Assert.Contains("already exists. Use Edit", rig.Vm.McpProblems);
            Assert.Empty(rig.Gw.Puts);
            Assert.Empty(rig.Asked);
        });
    }

    [Fact]
    public void A_server_list_read_for_another_profile_is_not_used_to_check_a_save()
    {
        RunSta(() =>
        {
            var rig = new Rig(chosen: [new ProfileKey("gw", "default"), new ProfileKey("gw2", "work")]);
            rig.FillLocalServer();                                                        // the list was read while "default" was active
            rig.Vm.AttachCron(() => null, "gw2");                                         // now "work"
            rig.Vm.SaveMcpServerAsync().GetAwaiter().GetResult();
            Assert.Contains("profile changed", rig.Vm.McpProblems);
            Assert.Empty(rig.Gw.Puts);
        });
    }

    [Fact]
    public void Safety_copies_are_pruned_per_profile_and_never_touch_a_profile_whose_name_starts_the_same()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            var other = rig.Vm.WriteConfigBackup("work-2", "a: 1 ");
            var dot = rig.Vm.WriteConfigBackup("a.b", "a: 1 ");
            var underscore = rig.Vm.WriteConfigBackup("a_b", "a: 1 ");
            for (var i = 0; i < MainViewModel.ConfigBackupsKept + 3; i++) rig.Vm.WriteConfigBackup("work", "a: 1 ");
            Assert.True(File.Exists(other));
            Assert.True(File.Exists(dot));
            Assert.True(File.Exists(underscore));                                         // same cleaned name, still its own budget
            Assert.Equal(MainViewModel.ConfigBackupsKept, Directory.GetFiles(rig.Vm.ConfigBackupDirectory, "work-*.yaml").Count(f => !Path.GetFileName(f).StartsWith("work-2-", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public void Adding_before_the_server_list_has_been_loaded_is_refused_rather_than_guessing()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.FillLocalServer(listLoaded: false);
            rig.Vm.SaveMcpServerAsync().GetAwaiter().GetResult();
            Assert.Contains("has not been loaded yet", rig.Vm.McpProblems);
            Assert.Empty(rig.Gw.Puts);
        });
    }

    [Fact]
    public void Editing_cannot_switch_a_server_between_a_command_and_an_address_and_the_transport_is_locked()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.RememberMcpServers([new McpServerInfo("files", "stdio", null, "npx", ["-y", "x"], true, null)]);
            var row = new CapabilityRowVM("mcp", "files", "files", "npx -y x", "stdio", true, "", true, _ => { });
            rig.Vm.EditMcpServerCommand!.Execute(row);
            Assert.False(rig.Vm.McpTransportEditable);                                    // the radios are locked while editing
            rig.Vm.McpRemote = true;                                                      // forced anyway (not through the locked radios)
            rig.Vm.McpUrl = "https://mcp.example.com/mcp";
            rig.Vm.SaveMcpServerAsync().GetAwaiter().GetResult();
            Assert.Contains("Changing between a local command and a remote address is not done here", rig.Vm.McpProblems);
            Assert.Empty(rig.Gw.Puts);
            Assert.Empty(rig.Asked);

            rig.Vm.McpRemote = false;                                                     // keeping its own transport works
            rig.Vm.SaveMcpServerAsync().GetAwaiter().GetResult();
            Assert.Single(rig.Gw.Puts);
        });
    }

    [Fact]
    public void Editing_a_server_that_vanished_from_the_list_is_refused()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.RememberMcpServers([new McpServerInfo("files", "stdio", null, "npx", [], true, null)]);
            rig.Vm.EditMcpServerCommand!.Execute(new CapabilityRowVM("mcp", "files", "files", "", "stdio", true, "", true, _ => { }));
            rig.Vm.RememberMcpServers([]);                                                // removed elsewhere meanwhile
            rig.Vm.SaveMcpServerAsync().GetAwaiter().GetResult();
            Assert.Contains("no longer in the server list", rig.Vm.McpProblems);
            Assert.Empty(rig.Gw.Puts);
        });
    }

    [Fact]
    public void A_file_that_changes_while_the_confirmation_is_open_is_not_overwritten()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Load();
            rig.Vm.ConfirmConfig = (t, m) => { rig.Asked.Add((t, m)); rig.Gw.Yaml = "model: changed-while-deciding\n"; return true; };   // someone saves while the dialog is open
            rig.Vm.ConfigYaml = "model: mine\n";
            rig.Vm.SaveConfigAsync().GetAwaiter().GetResult();
            Assert.Single(rig.Asked);
            Assert.Empty(rig.Gw.Puts);                                                    // not written
            Assert.Contains("changed on the gateway while you were deciding", rig.Vm.ConfigStatus);
            Assert.Equal("model: mine\n", rig.Vm.ConfigYaml);
            Assert.Equal("model: changed-while-deciding\n", rig.Gw.Yaml);
        });
    }

    [Fact]
    public void Safety_copies_have_unique_names_are_kept_per_profile_and_only_the_newest_ten_survive()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            var other = rig.Vm.WriteConfigBackup("work", "other profile");
            var names = new HashSet<string>();
            for (var i = 0; i < MainViewModel.ConfigBackupsKept + 5; i++)
            {
                var path = rig.Vm.WriteConfigBackup("default", "version " + i);
                Assert.True(names.Add(path), "two copies written in the same instant must not overwrite each other");
            }
            var kept = Directory.GetFiles(rig.BackupDir, "default-*.yaml");
            Assert.Equal(MainViewModel.ConfigBackupsKept, kept.Length);
            Assert.Contains(names.Last(), kept);                                          // the newest is kept
            Assert.DoesNotContain(names.First(), kept);                                   // the oldest is gone
            Assert.True(File.Exists(other));                                              // another profile's copy is untouched
        });
    }

    [Fact]
    public void The_confirmation_says_the_copy_is_plain_text_that_may_hold_secrets()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Load(); rig.Vm.ConfigYaml = "x: 1\n";
            rig.Vm.SaveConfigAsync().GetAwaiter().GetResult();
            Assert.Contains("plain text", Assert.Single(rig.Asked).Message);
            Assert.Contains("secrets", rig.Asked[0].Message);
        });
    }
}

file sealed class MemPrefs37 : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
