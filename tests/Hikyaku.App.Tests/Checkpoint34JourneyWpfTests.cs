using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 34 — the Journey page through the real MainViewModel (fixture HTTP; no live gateway).</summary>
[Collection("StaMath")]
public class Checkpoint34JourneyWpfTests
{
    private const string Graph = """
        {"nodes":[
          {"id":"deploy","label":"deploy","kind":"skill","timestamp":1759000000,"category":"devops","useCount":3},
          {"id":"memory:MEMORY.md:0:ab12","label":"Prefers metric units <!-- observed -->:","kind":"memory","timestamp":1761000000,"memorySource":"MEMORY.md"},
          {"id":"notes","label":"notes","kind":"skill","timestamp":1756000000}
        ]}
        """;

    private sealed class Gateway : HttpMessageHandler
    {
        public readonly List<string> Seen = new();
        public Func<string, HttpResponseMessage> Reply = url => url.Contains("/api/learning/node")
            ? Json(HttpStatusCode.OK, """{"ok":true,"kind":"skill","label":"deploy","content":"# Deploy\n\nRun the release script."}""")
            : Json(HttpStatusCode.OK, Graph);
        public TaskCompletionSource? HoldNode;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.PathAndQuery;
            Seen.Add(request.Method + " " + url);
            if (url.Contains("/api/learning/node") && HoldNode is { } h) await h.Task;
            return Reply(url);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Rig
    {
        public MainViewModel Vm;
        public Gateway Gw = new();
        public ProfileSelectionService Selection;

        public Rig() : this([new ProfileKey("gw", "default")]) { }

        /// <summary>The selection is chosen up front: changing it later would raise an event that marshals through Application.Current (unsafe in a full run).</summary>
        public Rig(IEnumerable<ProfileKey> chosen)
        {
            Selection = new ProfileSelectionService(new MemPrefs34());
            Selection.SetSelectedAsync(chosen).GetAwaiter().GetResult();
            var auth = new DashboardAuth(new Uri("https://hermes.example/"), Gw);
            Vm = new MainViewModel(Selection, new NotificationGate(Selection), () => new DemoTransport(), () => "t",
                () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
            Vm.AttachCron(() => null, "gw");
            Vm.AttachJourney(() => new JourneyApi(auth));
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
    public void Refresh_reads_the_active_profiles_graph_and_shows_a_summary_and_months_newest_first()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.RefreshJourneyAsync().GetAwaiter().GetResult();
            Assert.Equal("GET /api/learning/graph?profile=default", Assert.Single(rig.Gw.Seen));
            Assert.Equal("2 skills and 1 memory this profile's agent has picked up.", rig.Vm.JourneySummary);
            Assert.Equal(["October 2025", "September 2025", "August 2025"], rig.Vm.JourneyMonths.Select(m => m.Label));
            Assert.Equal("Prefers metric units", rig.Vm.JourneyMonths[0].Nodes.Single().Title);
            Assert.Equal("Profile: default", rig.Vm.JourneyProfileLabel);
        });
    }

    [Fact]
    public void The_filter_shows_used_skills_or_the_rest_and_says_so_when_nothing_matches()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.RefreshJourneyAsync().GetAwaiter().GetResult();
            rig.Vm.SetJourneyFilter(JourneyFilter.Used);
            Assert.Equal(["deploy"], rig.Vm.JourneyMonths.SelectMany(m => m.Nodes).Select(n => n.Title));
            rig.Vm.SetJourneyFilter(JourneyFilter.Learned);
            Assert.Equal(2, rig.Vm.JourneyMonths.SelectMany(m => m.Nodes).Count());
            Assert.Single(rig.Gw.Seen);                                                  // filtering never asks the gateway again

            rig.Gw.Reply = _ => Json(HttpStatusCode.OK, """{"nodes":[{"id":"a","label":"a"}]}""");
            rig.Vm.RefreshJourneyAsync().GetAwaiter().GetResult();
            rig.Vm.SetJourneyFilter(JourneyFilter.Used);
            Assert.Empty(rig.Vm.JourneyMonths);
            Assert.Contains("No skill has been used yet", rig.Vm.JourneyStatus);
        });
    }

    [Fact]
    public void An_empty_graph_a_missing_route_and_an_unchosen_profile_each_say_so()
    {
        RunSta(() =>
        {
            var empty = new Rig();
            empty.Gw.Reply = _ => Json(HttpStatusCode.OK, """{"nodes":[]}""");
            empty.Vm.RefreshJourneyAsync().GetAwaiter().GetResult();
            Assert.Contains("Nothing learned yet", empty.Vm.JourneyStatus);

            var missing = new Rig();
            missing.Gw.Reply = _ => Json(HttpStatusCode.NotFound, "{}");
            missing.Vm.RefreshJourneyAsync().GetAwaiter().GetResult();
            Assert.Contains("does not offer the learning graph", missing.Vm.JourneyStatus);

            var none = new Rig([]);
            none.Vm.RefreshJourneyAsync().GetAwaiter().GetResult();
            Assert.Contains("Choose a profile", none.Vm.JourneyStatus);
            Assert.Empty(none.Gw.Seen);
        });
    }

    [Fact]
    public void Changing_profile_never_shows_the_old_profiles_learning_under_the_new_one()
    {
        RunSta(() =>
        {
            // Two chosen profiles on two gateways; re-attaching with the other gateway id switches the active profile without
            // raising the selection event (which marshals through Application.Current: unsafe in a full test run).
            var rig = new Rig([new ProfileKey("gw", "default"), new ProfileKey("gw2", "work")]);
            rig.Vm.RefreshJourneyAsync().GetAwaiter().GetResult();
            Assert.Equal(3, rig.Vm.JourneyMonths.SelectMany(m => m.Nodes).Count());

            rig.Vm.AttachCron(() => null, "gw2");
            rig.Gw.Reply = _ => Json(HttpStatusCode.InternalServerError, "{}");
            rig.Vm.RefreshJourneyAsync().GetAwaiter().GetResult();
            Assert.Equal("GET /api/learning/graph?profile=work", rig.Gw.Seen.Last());
            Assert.Empty(rig.Vm.JourneyMonths);                                           // the failure leaves nothing of "default" on screen
            Assert.Equal("Profile: work", rig.Vm.JourneyProfileLabel);
        });
    }

    [Fact]
    public void Opening_a_node_reads_its_text_by_exact_id_and_back_returns_to_the_timeline()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.RefreshJourneyAsync().GetAwaiter().GetResult();
            var memory = rig.Vm.JourneyMonths[0].Nodes.Single();
            rig.Vm.OpenJourneyNodeAsync(memory).GetAwaiter().GetResult();
            var q = System.Web.HttpUtility.ParseQueryString(new Uri("https://x" + rig.Gw.Seen.Last().Split(' ')[1]).Query);
            Assert.Equal(("memory:MEMORY.md:0:ab12", "default"), (q["id"], q["profile"]));
            Assert.Contains("Run the release script.", rig.Vm.JourneyDetailText);
            Assert.Equal(Visibility.Visible, rig.Vm.JourneyDetailVisibility);
            Assert.Equal(Visibility.Collapsed, rig.Vm.JourneyListVisibility);
            Assert.Equal("Prefers metric units", rig.Vm.JourneyDetailTitle);

            rig.Vm.CloseJourneyNodeCommand!.Execute(null);
            Assert.Equal(Visibility.Visible, rig.Vm.JourneyListVisibility);
            Assert.Equal("", rig.Vm.JourneyDetailText);
        });
    }

    [Fact]
    public void A_node_the_gateway_cannot_read_says_so_and_a_late_answer_for_a_closed_node_is_dropped()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.RefreshJourneyAsync().GetAwaiter().GetResult();
            var node = rig.Vm.JourneyMonths[0].Nodes.Single();

            rig.Gw.Reply = u => u.Contains("/api/learning/node") ? Json(HttpStatusCode.OK, """{"ok":false,"content":"leftover text"}""") : Json(HttpStatusCode.OK, Graph);
            rig.Vm.OpenJourneyNodeAsync(node).GetAwaiter().GetResult();
            Assert.Equal("Couldn't read this one.", rig.Vm.JourneyDetailNote);

            rig.Gw.Reply = _ => Json(HttpStatusCode.OK, """{"ok":true,"content":"late words"}""");
            rig.Gw.HoldNode = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = rig.Vm.OpenJourneyNodeAsync(node);                              // in flight
            rig.Vm.CloseJourneyNodeCommand!.Execute(null);                                 // the person goes back meanwhile
            rig.Gw.HoldNode.SetResult();
            pending.GetAwaiter().GetResult();
            Assert.Equal("", rig.Vm.JourneyDetailText);
        });
    }

    [Fact]
    public void Nothing_but_reads_is_ever_sent()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.RefreshJourneyAsync().GetAwaiter().GetResult();
            rig.Vm.OpenJourneyNodeAsync(rig.Vm.JourneyMonths[0].Nodes.Single()).GetAwaiter().GetResult();
            Assert.All(rig.Gw.Seen, s => Assert.StartsWith("GET ", s));
        });
    }
}

file sealed class MemPrefs34 : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
