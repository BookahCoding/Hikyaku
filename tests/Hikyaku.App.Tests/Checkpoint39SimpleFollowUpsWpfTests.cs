using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 39 — Simple view follow-ups through the real MainViewModel: approvals "Off" must not be invisible to someone in Simple (with a way back to
/// safe), the status line is plain and empty when connected, and the sign-in prompt appears only where it should. Fake gateway; selection chosen at construction.
/// Entering Simple reads the approvals mode on its own, so the rig configures the gateway first and waits for that read.
/// </summary>
[Collection("StaMath")]
public class Checkpoint39SimpleFollowUpsWpfTests
{
    private sealed class Gateway : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url, string Body)> Seen = new();
        public string ApprovalsMode = "off";
        public bool FailReads, FailWrites;
        public TaskCompletionSource? Hold;                                                // set after the rig is built: reads wait until it is released

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (Hold is { } hold) await hold.Task;
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            lock (Seen) Seen.Add((r.Method, r.RequestUri!.PathAndQuery, body));
            if (r.Method == HttpMethod.Put)
            {
                if (FailWrites) return Json("{}", HttpStatusCode.InternalServerError);
                ApprovalsMode = (string)JsonNode.Parse(body)!["config"]!["approvals"]!["mode"]!;
                return Json("{}");
            }
            if (FailReads) return Json("{}", HttpStatusCode.InternalServerError);
            return Json("{\"approvals\":{\"mode\":\"" + ApprovalsMode + "\"},\"model\":\"x\"}");
        }

        public int Reads { get { lock (Seen) return Seen.Count(s => s.Method == HttpMethod.Get); } }
        public IEnumerable<(HttpMethod Method, string Url, string Body)> Puts { get { lock (Seen) return Seen.Where(s => s.Method == HttpMethod.Put).ToList(); } }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Rig
    {
        public MainViewModel Vm;
        public Gateway Gw = new();
        public List<(string Title, string Message)> Asked = [];

        public Rig(bool advanced = false, bool confirm = true, bool handler = true, string status = "t",
            string mode = "off", bool failReads = false, IEnumerable<ProfileKey>? chosen = null)
        {
            Gw.ApprovalsMode = mode; Gw.FailReads = failReads;                            // the gateway is set up before the view model reads it
            var selection = new ProfileSelectionService(new MemPrefs39());
            selection.SetSelectedAsync(chosen ?? [new ProfileKey("gw", "default")]).GetAwaiter().GetResult();
            var auth = new DashboardAuth(new Uri("https://hermes.example/"), Gw);
            Vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => status,
                () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
            Vm.AttachCron(() => null, "gw");
            Vm.AttachTypedSettings(() => new ConfigApi(auth));
            if (handler) Vm.ConfirmConfig = (t, m) => { Asked.Add((t, m)); return confirm; };
            Vm.ApplyExperience(advanced);                                                 // entering Simple starts the read
            if (!advanced) WaitFor(() => Gw.Reads >= 1);
            Thread.Sleep(30);                                                             // let the answer land
        }

        public void Refresh() => Vm.RefreshApprovalsRiskAsync().GetAwaiter().GetResult();
    }

    private static void WaitFor(Func<bool> cond)
    {
        for (var i = 0; i < 300 && !cond(); i++) Thread.Sleep(10);
        Assert.True(cond(), "condition not reached");
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

    // ---- approvals Off is visible in Simple ----

    [Fact]
    public void Approvals_set_to_off_are_shown_in_simple_with_a_way_back_and_not_duplicated_in_advanced()
    {
        RunSta(() =>
        {
            var rig = new Rig();                                                          // entering Simple read the setting by itself
            Assert.Equal("/api/config?profile=default", rig.Gw.Seen[0].Url);
            Assert.Contains("Hermes is set to run commands without asking you first.", rig.Vm.RiskNotice);
            Assert.Equal(Visibility.Visible, rig.Vm.RiskNoticeVisibility);
            Assert.Equal(Visibility.Visible, rig.Vm.AskFirstVisibility);
            Assert.True(rig.Vm.AskFirstCommand!.CanExecute(null));

            rig.Vm.ApplyExperience(true);                                                  // Advanced has the setting on its own page
            Assert.Equal(Visibility.Collapsed, rig.Vm.RiskNoticeVisibility);
        });
    }

    [Fact]
    public void Manual_or_smart_approvals_and_a_failed_read_show_no_alarm()
    {
        RunSta(() =>
        {
            foreach (var mode in new[] { "manual", "smart" })
            {
                var rig = new Rig(mode: mode);
                rig.Refresh();
                Assert.Equal(Visibility.Collapsed, rig.Vm.RiskNoticeVisibility);
                Assert.False(rig.Vm.AskFirstCommand!.CanExecute(null));                    // nothing to undo, so the button cannot run
            }
            var broken = new Rig(failReads: true);
            broken.Refresh();                                                             // must not throw, must not guess
            Assert.Equal(Visibility.Collapsed, broken.Vm.RiskNoticeVisibility);
        });
    }

    [Fact]
    public void Ask_me_first_asks_in_words_then_sets_the_mode_back_to_manual_and_the_notice_goes()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.AskFirstCommand!.Execute(null);
            WaitFor(() => rig.Vm.AskFirstVisibility == Visibility.Collapsed);
            var (title, message) = Assert.Single(rig.Asked);
            Assert.Equal("Ask me first", title);
            Assert.Contains("everyone who uses it", message);
            var put = Assert.Single(rig.Gw.Puts);
            Assert.Equal("/api/config?profile=default", put.Url);
            var config = JsonNode.Parse(put.Body)!["config"]!.AsObject();
            Assert.Equal("manual", (string)config["approvals"]!["mode"]!);
            Assert.Single(config);                                                         // nothing but that one key
            Assert.Equal(Visibility.Collapsed, rig.Vm.RiskNoticeVisibility);
        });
    }

    [Fact]
    public void Without_a_yes_or_a_confirmation_handler_nothing_is_changed()
    {
        RunSta(() =>
        {
            var no = new Rig(confirm: false);
            no.Vm.AskFirstCommand!.Execute(null);
            Thread.Sleep(150);
            Assert.Empty(no.Gw.Puts);
            Assert.Equal(Visibility.Visible, no.Vm.RiskNoticeVisibility);

            var unset = new Rig(handler: false);
            unset.Vm.AskFirstCommand!.Execute(null);
            Thread.Sleep(150);
            Assert.Empty(unset.Gw.Puts);
        });
    }

    [Fact]
    public void A_failed_change_keeps_the_notice_and_says_who_to_ask()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Gw.FailWrites = true;
            rig.Vm.AskFirstCommand!.Execute(null);
            WaitFor(() => rig.Vm.RiskNotice.Contains("Ask the person"));
            Assert.Contains("Couldn't change it. Ask the person who looks after Hermes.", rig.Vm.RiskNotice);
            Assert.Equal(Visibility.Visible, rig.Vm.RiskNoticeVisibility);
            Assert.Equal(Visibility.Visible, rig.Vm.AskFirstVisibility);
        });
    }

    [Fact]
    public void Another_profiles_approvals_setting_is_never_shown_under_this_one()
    {
        RunSta(() =>
        {
            var rig = new Rig(chosen: [new ProfileKey("gw", "default"), new ProfileKey("gw2", "work")]);
            Assert.Equal(Visibility.Visible, rig.Vm.RiskNoticeVisibility);
            rig.Vm.AttachCron(() => null, "gw2");                                          // the active profile is now "work"
            rig.Gw.FailReads = true;                                                       // and its setting cannot be read
            rig.Refresh();
            Assert.Equal(Visibility.Collapsed, rig.Vm.RiskNoticeVisibility);              // not carried over from "default"
        });
    }

    [Fact]
    public void An_answer_that_arrives_after_the_profile_changed_is_dropped()
    {
        RunSta(() =>
        {
            var rig = new Rig(mode: "manual", chosen: [new ProfileKey("gw", "default"), new ProfileKey("gw2", "work")]);
            rig.Gw.ApprovalsMode = "off";
            rig.Gw.FailReads = false;
            rig.Gw.Hold = new TaskCompletionSource();
            var pending = rig.Vm.RefreshApprovalsRiskAsync();                              // asked for "default", answer held back
            rig.Vm.AttachCron(() => null, "gw2");                                          // the active profile becomes "work"
            rig.Gw.Hold.SetResult();
            pending.GetAwaiter().GetResult();
            Assert.Equal(Visibility.Collapsed, rig.Vm.RiskNoticeVisibility);               // default's "off" is not work's
        });
    }

    // ---- status line and sign-in prompt ----

    [Fact]
    public void The_status_line_is_empty_and_hidden_when_there_is_nothing_to_say_in_simple()
    {
        RunSta(() =>
        {
            var quiet = new Rig(status: "", mode: "manual");
            Assert.Equal("", quiet.Vm.StatusBanner);
            Assert.Equal(Visibility.Collapsed, quiet.Vm.StatusBannerVisibility);
            var talking = new Rig(status: "Connecting to Hermes…", mode: "manual");
            Assert.Equal(Visibility.Visible, talking.Vm.StatusBannerVisibility);
        });
    }

    [Fact]
    public void The_sign_in_prompt_shows_only_in_simple_and_only_when_the_window_says_signing_in_is_needed()
    {
        RunSta(() =>
        {
            var simple = new Rig(mode: "manual");
            Assert.Equal(Visibility.Collapsed, simple.Vm.SignInPromptVisibility);          // connected: nothing to offer
            simple.Vm.SignInOffered = () => true;
            Assert.Equal(Visibility.Visible, simple.Vm.SignInPromptVisibility);
            simple.Vm.ApplyExperience(true);
            Assert.Equal(Visibility.Collapsed, simple.Vm.SignInPromptVisibility);          // Advanced has the technical status line and Settings
        });
    }
}

file sealed class MemPrefs39 : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
