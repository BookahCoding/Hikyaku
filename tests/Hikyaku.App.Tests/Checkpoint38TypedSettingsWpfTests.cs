using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 38 — the typed settings editor through the real MainViewModel (Advanced only; every change confirmed; fake gateway).</summary>
[Collection("StaMath")]
public class Checkpoint38TypedSettingsWpfTests
{
    private const string Schema = """
        {"fields":{
          "model":{"type":"string","description":"The model","category":"general"},
          "approvals.mode":{"type":"select","description":"How dangerous commands are approved","category":"approvals","options":["manual","smart","off"]},
          "voice.enabled":{"type":"boolean","description":"Speak replies","category":"voice"},
          "agent.max_turns":{"type":"number","description":"Turn limit","category":"agent"},
          "tools.disabled":{"type":"list","description":"Disabled tools","category":"tools"},
          "openai.api_key":{"type":"string","description":"Provider key","category":"openai"}
        }}
        """;

    private sealed class Gateway : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url, string Body)> Seen = new();
        public JsonObject Values = JsonNode.Parse("""{"model":"big","approvals":{"mode":"manual"},"voice":{"enabled":true},"agent":{"max_turns":30},"openai":{"api_key":"sk-SECRET"}}""")!.AsObject();
        public bool Refuse;
        public bool FailSchema;
        public TaskCompletionSource? HoldPut;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.PathAndQuery;
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            Seen.Add((r.Method, url, body));
            if (url.StartsWith("/api/config/schema", StringComparison.Ordinal)) return FailSchema ? Json("{}", HttpStatusCode.InternalServerError) : Json(Schema);
            if (r.Method == HttpMethod.Put)
            {
                if (HoldPut is { } h) await h.Task;
                if (Refuse) return Json("""{"detail":"voice.enabled is read-only here"}""", HttpStatusCode.BadRequest);
                Merge(Values, JsonNode.Parse(body)!["config"]!.AsObject());
                return Json("{}");
            }
            return Json(Values.ToJsonString());
        }

        private static void Merge(JsonObject into, JsonObject patch)
        {
            foreach (var (k, v) in patch.ToList())
            {
                if (v is JsonObject o && into[k] is JsonObject existing) Merge(existing, o);
                else into[k] = v?.DeepClone();
            }
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

        public Rig(bool advanced = true, bool confirm = true, bool handler = true, IEnumerable<ProfileKey>? chosen = null)
        {
            var selection = new ProfileSelectionService(new MemPrefs38());
            selection.SetSelectedAsync(chosen ?? [new ProfileKey("gw", "default")]).GetAwaiter().GetResult();
            var auth = new DashboardAuth(new Uri("https://hermes.example/"), Gw);
            Vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
                () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
            Vm.AttachCron(() => null, "gw");
            Vm.AttachConfigEditor(() => new GatewayConfigEditor(auth));
            Vm.AttachTypedSettings(() => new ConfigApi(auth));
            Vm.ApplyExperience(advanced);
            if (handler) Vm.ConfirmConfig = (t, m) => { Asked.Add((t, m)); return confirm; };
        }

        public void Load() => Vm.LoadSettingsAsync().GetAwaiter().GetResult();
        public SettingRowVM Row(string path) => Vm.SettingRows.Single(r => r.Path == path);
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
    public void In_simple_view_nothing_is_loaded_or_saved_whoever_asks()
    {
        RunSta(() =>
        {
            var rig = new Rig(advanced: false);
            Thread.Sleep(100);                                                            // entering Simple reads the approvals mode (one value), by design
            rig.Load();
            Assert.Empty(rig.Vm.SettingRows);
            // the editor itself sent nothing: no schema, no write; the only request is that one deliberate read of /api/config
            Assert.DoesNotContain(rig.Gw.Seen, s => s.Url.Contains("/schema") || s.Method == HttpMethod.Put);
            Assert.True(rig.Gw.Seen.Count <= 1);

            var adv = new Rig();
            adv.Load();
            var row = adv.Row("voice.enabled");
            adv.Vm.ApplyExperience(false);                                                // switched to Simple with a row still in memory
            row.EditBool = false;
            adv.Vm.SaveSettingAsync(row).GetAwaiter().GetResult();
            Assert.Empty(adv.Gw.Puts);
            Assert.Empty(adv.Asked);
        });
    }

    [Fact]
    public void Load_lists_the_settings_with_their_values_hides_secrets_and_leaves_lists_to_the_config_tab()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Load();
            Assert.Equal(["/api/config/schema?profile=default", "/api/config?profile=default"], rig.Gw.Seen.Select(s => s.Url));
            Assert.Equal(["model", "agent.max_turns", "approvals.mode", "openai.api_key", "tools.disabled", "voice.enabled"], rig.Vm.SettingRows.Select(r => r.Path));
            Assert.Equal("big", rig.Row("model").EditText);
            Assert.True(rig.Row("voice.enabled").EditBool);
            Assert.Equal("30", rig.Row("agent.max_turns").EditText);
            var secret = rig.Row("openai.api_key");
            Assert.Equal("", secret.EditText);                                            // the value is not even held by the row
            Assert.DoesNotContain("sk-SECRET", secret.ReadOnlyText);
            Assert.Equal(Visibility.Collapsed, secret.TextVisibility);
            Assert.Equal(Visibility.Visible, secret.ReadOnlyVisibility);
            Assert.Equal(Visibility.Collapsed, rig.Row("tools.disabled").TextVisibility);
            Assert.Contains("settings for profile default", rig.Vm.SettingsStatus);
        });
    }

    [Fact]
    public void Changing_a_switch_asks_in_words_then_sends_only_that_one_key_and_reads_it_back()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Load();
            var row = rig.Row("voice.enabled");
            row.EditBool = false;
            Assert.True(row.Dirty);
            Assert.Equal(Visibility.Visible, row.SaveVisibility);
            rig.Vm.SaveSettingAsync(row).GetAwaiter().GetResult();

            var (title, message) = Assert.Single(rig.Asked);
            Assert.Equal("Change setting", title);
            Assert.Contains("“voice.enabled” in profile “default”", message);
            Assert.Contains("From: true", message);
            Assert.Contains("To: false", message);
            Assert.DoesNotContain("affects what the agent may do", message);               // not a risky setting
            var put = Assert.Single(rig.Gw.Puts);
            Assert.Equal("/api/config?profile=default", put.Url);
            var config = JsonNode.Parse(put.Body)!["config"]!.AsObject();
            Assert.False((bool)config["voice"]!["enabled"]!);
            Assert.Single(config);                                                         // nothing but the one key
            Assert.False(row.Dirty);                                                       // the gateway's value is the new baseline
            Assert.False(row.EditBool);
            Assert.Equal("Saved voice.enabled. It applies from the next chat.", rig.Vm.SettingsStatus);
            Assert.Equal(2, rig.Gw.Seen.Count(s => s.Url.StartsWith("/api/config?", StringComparison.Ordinal) && s.Method == HttpMethod.Get));   // load + read back
        });
    }

    [Fact]
    public void A_row_loaded_for_one_profile_is_never_saved_under_another()
    {
        RunSta(() =>
        {
            var rig = new Rig(chosen: [new ProfileKey("gw", "default"), new ProfileKey("gw2", "work")]);
            rig.Load();
            var row = rig.Row("voice.enabled");
            row.EditBool = false;
            rig.Vm.AttachCron(() => null, "gw2");                                        // the active profile becomes "work" while the old row is still there
            rig.Vm.SaveSettingAsync(row).GetAwaiter().GetResult();
            rig.Vm.SaveSettingAsync(row).GetAwaiter().GetResult();                       // the refusal cleared the list; asking again must not slip through
            Assert.Empty(rig.Gw.Puts);
            Assert.Empty(rig.Asked);
            Assert.Empty(rig.Vm.SettingRows);
        });
    }

    [Fact]
    public void A_setting_that_governs_what_the_agent_may_do_carries_an_extra_warning()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Load();
            var row = rig.Row("approvals.mode");
            row.EditText = "off";
            rig.Vm.SaveSettingAsync(row).GetAwaiter().GetResult();
            Assert.Contains("affects what the agent may do or reach", Assert.Single(rig.Asked).Message);
            Assert.Equal("off", (string)JsonNode.Parse(Assert.Single(rig.Gw.Puts).Body)!["config"]!["approvals"]!["mode"]!);
        });
    }

    [Fact]
    public void An_invalid_or_unchanged_value_sends_nothing_and_does_not_even_ask()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Load();
            var turns = rig.Row("agent.max_turns");
            turns.EditText = "lots";
            rig.Vm.SaveSettingAsync(turns).GetAwaiter().GetResult();
            Assert.Equal("Enter a number.", turns.Error);

            var mode = rig.Row("approvals.mode");
            mode.EditText = "bogus";
            rig.Vm.SaveSettingAsync(mode).GetAwaiter().GetResult();
            Assert.Equal("Choose one of the listed options.", mode.Error);

            var model = rig.Row("model");
            model.EditText = "big";                                                        // same as now
            rig.Vm.SaveSettingAsync(model).GetAwaiter().GetResult();
            Assert.Equal("No change.", rig.Vm.SettingsStatus);

            Assert.Empty(rig.Gw.Puts);
            Assert.Empty(rig.Asked);
        });
    }

    [Fact]
    public void Without_a_yes_or_a_confirmation_handler_nothing_is_sent_and_the_edit_stays()
    {
        RunSta(() =>
        {
            var no = new Rig(confirm: false);
            no.Load();
            var row = no.Row("model");
            row.EditText = "small";
            no.Vm.SaveSettingAsync(row).GetAwaiter().GetResult();
            Assert.Empty(no.Gw.Puts);
            Assert.True(row.Dirty);
            Assert.Equal("Nothing was saved.", no.Vm.SettingsStatus);

            var unset = new Rig(handler: false);
            unset.Load();
            var r2 = unset.Row("model");
            r2.EditText = "small";
            unset.Vm.SaveSettingAsync(r2).GetAwaiter().GetResult();
            Assert.Empty(unset.Gw.Puts);
        });
    }

    [Fact]
    public void A_refusal_from_the_gateway_is_shown_on_the_row_and_the_edit_is_kept()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Load();
            rig.Gw.Refuse = true;
            var row = rig.Row("voice.enabled");
            row.EditBool = false;
            rig.Vm.SaveSettingAsync(row).GetAwaiter().GetResult();
            Assert.Contains("voice.enabled is read-only here", row.Error);
            Assert.True(row.Dirty);                                                        // still the person's edit, baseline unchanged
            Assert.True((bool)rig.Gw.Values["voice"]!["enabled"]!);
            Assert.Equal("Couldn't save voice.enabled.", rig.Vm.SettingsStatus);
        });
    }

    [Fact]
    public void A_secret_or_a_list_can_never_be_saved_even_if_asked_directly()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Load();
            foreach (var path in new[] { "openai.api_key", "tools.disabled" })
            {
                var row = rig.Row(path);
                row.EditText = "x";
                rig.Vm.SaveSettingAsync(row).GetAwaiter().GetResult();
            }
            Assert.Empty(rig.Gw.Puts);
            Assert.Empty(rig.Asked);
        });
    }

    [Fact]
    public void The_search_narrows_the_list_and_only_one_save_runs_at_a_time()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Load();
            rig.Vm.SettingsQuery = "speak";
            Assert.Equal(["voice.enabled"], rig.Vm.SettingRows.Select(r => r.Path));
            rig.Vm.SettingsQuery = "";
            Assert.Equal(6, rig.Vm.SettingRows.Count);

            rig.Gw.HoldPut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var a = rig.Row("voice.enabled"); a.EditBool = false;
            var b = rig.Row("model"); b.EditText = "small";
            var first = rig.Vm.SaveSettingAsync(a);                                        // running on the gateway
            Assert.True(rig.Vm.SettingsBusy);
            Assert.False(b.SaveCommand.CanExecute(null));
            rig.Vm.SaveSettingAsync(b).GetAwaiter().GetResult();                           // a second one is ignored
            Assert.Single(rig.Asked);
            rig.Gw.HoldPut.SetResult();
            first.GetAwaiter().GetResult();
            Assert.Single(rig.Gw.Puts);
        });
    }

    [Fact]
    public void Changing_profile_never_shows_one_profiles_settings_under_another()
    {
        RunSta(() =>
        {
            var rig = new Rig(chosen: [new ProfileKey("gw", "default"), new ProfileKey("gw2", "work")]);
            rig.Load();
            Assert.NotEmpty(rig.Vm.SettingRows);
            rig.Vm.AttachCron(() => null, "gw2");
            rig.Gw.FailSchema = true;                                                      // the new profile's load fails: nothing of "default" may remain on screen
            rig.Vm.LoadSettingsAsync().GetAwaiter().GetResult();
            Assert.Contains(rig.Gw.Seen, s => s.Url == "/api/config/schema?profile=work");
            Assert.Empty(rig.Vm.SettingRows);
            rig.Gw.FailSchema = false;
            rig.Vm.LoadSettingsAsync().GetAwaiter().GetResult();
            Assert.Contains("profile work", rig.Vm.SettingsStatus);
        });
    }
}

file sealed class MemPrefs38 : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
