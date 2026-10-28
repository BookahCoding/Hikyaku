using System.Net;
using System.Net.Http;
using System.Text;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 27 — chat actions in the sidebar list through the real MainViewModel (fixture HTTP; no live gateway).</summary>
[Collection("StaMath")]
public class Checkpoint27SessionActionsWpfTests
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

    private sealed class Rig : IDisposable
    {
        public MainViewModel Vm;
        public List<Seen> Seen = [];
        public DashboardAuth Auth;
        public List<string> Asked = [];
        public string? Prompted = "New title";
        public bool Confirm = true;
        public Func<HttpRequestMessage, HttpResponseMessage> Reply;

        public Rig()
        {
            Reply = r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("mutation-snapshot")
                ? Json(HttpStatusCode.OK, """{"exists":true,"runtime_revision":1,"runtime_generation":1}""")
                : Json(HttpStatusCode.OK, "{}");
            var selection = new ProfileSelectionService(new MemPrefs27());
            selection.SetSelectedAsync(new[] { new ProfileKey("gw", "default") }).GetAwaiter().GetResult();
            var gate = new NotificationGate(selection);
            Auth = new DashboardAuth(new Uri("https://hermes.example/"), new Stub(Seen, r => Reply(r)));
            var catalog = new SessionCatalog(gate, () => null);
            catalog.SetActiveProfile(new ProfileKey("gw", "default"));
            Vm = new MainViewModel(selection, gate, () => new DemoTransport(), () => "t",
                () => null, () => null, () => catalog, () => null, () => null, () => null, () => null, () => null);
            Vm.AttachSessionActions(() => new SessionActionsApi(Auth));
            Vm.PromptText = (_, _) => Prompted;
            Vm.ConfirmSessionAction = (t, m) => { Asked.Add(m); return Confirm; };
            Vm.SessionRows.Add(Row("s1", "First", pinned: false, unread: true));
            Vm.SessionRows.Add(Row("s2", "Second", pinned: true, unread: false));
        }

        public static SessionRowVM Row(string id, string title, bool pinned, bool unread) =>
            new(new SessionSummary(id, title, Pinned: pinned, Unread: unread));

        public IEnumerable<Seen> Writes => Seen.Where(s => s.Method != HttpMethod.Get);
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
    public void A_row_listed_for_another_profile_is_never_acted_on()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            var row = rig.Vm.SessionRows[0];
            row.LoadedFor = new ProfileKey("gw", "somebody-else");
            var before = rig.Writes.Count();
            rig.Vm.RunSessionActionAsync(row, "pin").GetAwaiter().GetResult();
            rig.Vm.RunSessionActionAsync(row, "delete").GetAwaiter().GetResult();
            Assert.Equal(before, rig.Writes.Count());
            Assert.Contains("profile changed", rig.Vm.SessionActionStatus);
        });
    }

    [Fact]
    public void The_menu_offers_labels_that_follow_the_rows_state()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            Assert.Equal(["Rename…", "Pin", "Archive", "Mark as read", "Delete…"], rig.Vm.SessionMenuFor(rig.Vm.SessionRows[0]).Select(m => m.Label));
            Assert.Equal(["Rename…", "Unpin", "Archive", "Mark as unread", "Delete…"], rig.Vm.SessionMenuFor(rig.Vm.SessionRows[1]).Select(m => m.Label));
            Assert.Equal("Unarchive", rig.Vm.SessionMenuFor(new SessionRowVM(new SessionSummary("x"), archivedView: true))[2].Label);
        });
    }

    [Fact]
    public void Rename_pin_and_unread_change_the_row_only_after_the_gateway_accepts()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            var row = rig.Vm.SessionRows[0];

            rig.Reply = r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, """{"exists":true,"runtime_generation":1}""") : Json(HttpStatusCode.BadRequest, """{"detail":"Title already in use"}""");
            rig.Vm.RunSessionActionAsync(row, "rename").GetAwaiter().GetResult();
            Assert.Equal("First", row.Title);                                       // refused: unchanged
            Assert.Equal("Title already in use", rig.Vm.SessionActionStatus);

            rig.Reply = r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, """{"exists":true,"runtime_generation":1}""") : Json(HttpStatusCode.OK, "{}");
            rig.Vm.RunSessionActionAsync(row, "rename").GetAwaiter().GetResult();
            Assert.Equal("New title", row.Title);
            rig.Vm.RunSessionActionAsync(row, "pin").GetAwaiter().GetResult();
            Assert.True(row.IsPinned);
            rig.Vm.RunSessionActionAsync(row, "unread").GetAwaiter().GetResult();   // it was unread → mark read
            Assert.False(row.IsUnread);
            Assert.Empty(rig.Asked);                                                // none of these needs a confirmation
            Assert.Contains("\"profile\":\"default\"", rig.Writes.Last().Body);
        });
    }

    [Fact]
    public void A_refused_pin_or_read_change_leaves_the_row_as_it_was()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            var row = rig.Vm.SessionRows[0];                                        // not pinned, unread
            rig.Reply = r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, """{"exists":true,"runtime_generation":1}""") : Json(HttpStatusCode.BadRequest, """{"detail":"session_busy"}""");
            rig.Vm.RunSessionActionAsync(row, "pin").GetAwaiter().GetResult();
            rig.Vm.RunSessionActionAsync(row, "unread").GetAwaiter().GetResult();
            Assert.False(row.IsPinned);
            Assert.True(row.IsUnread);
            Assert.Equal("The chat is busy right now. Try again in a moment.", rig.Vm.SessionActionStatus);
        });
    }

    [Fact]
    public void A_cancelled_or_unchanged_rename_sends_nothing()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Prompted = null;
            rig.Vm.RunSessionActionAsync(rig.Vm.SessionRows[0], "rename").GetAwaiter().GetResult();
            rig.Prompted = "First";
            rig.Vm.RunSessionActionAsync(rig.Vm.SessionRows[0], "rename").GetAwaiter().GetResult();
            rig.Prompted = new string('x', 201);
            rig.Vm.RunSessionActionAsync(rig.Vm.SessionRows[0], "rename").GetAwaiter().GetResult();
            Assert.Empty(rig.Seen);
            Assert.Contains("200 characters", rig.Vm.SessionActionStatus);
        });
    }

    [Fact]
    public void Archive_moves_the_row_out_of_the_list_and_a_refusal_keeps_it()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            var row = rig.Vm.SessionRows[0];
            rig.Reply = r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, """{"exists":true,"runtime_generation":1}""") : Json(HttpStatusCode.BadRequest, """{"detail":"permission_denied"}""");
            rig.Vm.RunSessionActionAsync(row, "archive").GetAwaiter().GetResult();
            Assert.Contains(row, rig.Vm.SessionRows);
            Assert.Equal("The gateway didn't allow this change to the chat.", rig.Vm.SessionActionStatus);

            rig.Reply = r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, """{"exists":true,"runtime_generation":1}""") : Json(HttpStatusCode.OK, "{}");
            rig.Vm.RunSessionActionAsync(row, "archive").GetAwaiter().GetResult();
            Assert.DoesNotContain(row, rig.Vm.SessionRows);
            Assert.Contains("\"archived\":true", rig.Writes.Last().Body);
        });
    }

    [Fact]
    public void Delete_is_confirmed_in_words_and_nothing_is_sent_without_a_yes()
    {
        RunSta(() =>
        {
            using var rig = new Rig { Confirm = false };
            var row = rig.Vm.SessionRows[0];
            rig.Vm.RunSessionActionAsync(row, "delete").GetAwaiter().GetResult();
            Assert.Empty(rig.Seen);
            Assert.Contains("First", rig.Asked.Single());
            Assert.Contains("can't be undone", rig.Asked.Single());

            rig.Vm.ConfirmSessionAction = null;
            rig.Vm.RunSessionActionAsync(row, "delete").GetAwaiter().GetResult();
            Assert.Empty(rig.Seen);
            Assert.Contains(row, rig.Vm.SessionRows);

            rig.Vm.ConfirmSessionAction = (_, _) => true;
            rig.Vm.RunSessionActionAsync(row, "delete").GetAwaiter().GetResult();
            Assert.DoesNotContain(row, rig.Vm.SessionRows);
            Assert.Equal(HttpMethod.Delete, rig.Writes.Single().Method);
        });
    }

    [Fact]
    public void A_chat_removed_elsewhere_disappears_from_the_list()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            var row = rig.Vm.SessionRows[0];
            rig.Reply = _ => Json(HttpStatusCode.OK, """{"exists":false}""");
            rig.Vm.RunSessionActionAsync(row, "pin").GetAwaiter().GetResult();
            Assert.DoesNotContain(row, rig.Vm.SessionRows);
            Assert.Equal(SessionActionsApi.GoneMessage, rig.Vm.SessionActionStatus);
        });
    }

    [Fact]
    public void Opening_a_chat_marks_it_read_best_effort_and_a_failure_changes_nothing()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            var unread = rig.Vm.SessionRows[0];
            var read = rig.Vm.SessionRows[1];
            rig.Vm.MarkReadOnOpenAsync(read).GetAwaiter().GetResult();
            Assert.Empty(rig.Seen);                                                 // already read: no write

            rig.Reply = _ => Json(HttpStatusCode.InternalServerError, """{"detail":"x"}""");
            rig.Vm.MarkReadOnOpenAsync(unread).GetAwaiter().GetResult();
            Assert.True(unread.IsUnread);                                           // the failed write left the flag
            Assert.Equal("", rig.Vm.SessionActionStatus);                           // and did not nag

            rig.Reply = r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, """{"exists":true,"runtime_generation":1}""") : Json(HttpStatusCode.OK, "{}");
            rig.Vm.MarkReadOnOpenAsync(unread).GetAwaiter().GetResult();
            Assert.False(unread.IsUnread);
            Assert.Contains("\"unread\":false", rig.Writes.Last().Body);
        });
    }

    [Fact]
    public void Nothing_is_sent_without_a_connection()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.AttachSessionActions(() => null);
            rig.Vm.RunSessionActionAsync(rig.Vm.SessionRows[0], "pin").GetAwaiter().GetResult();
            Assert.Contains("Connect to a gateway", rig.Vm.SessionActionStatus);
            Assert.Empty(rig.Seen);
        });
    }

    [Fact]
    public void Nothing_is_sent_for_a_sidebar_profile_that_is_not_selected()
    {
        RunSta(() =>
        {
            var selection = new ProfileSelectionService(new MemPrefs27());
            selection.SetSelectedAsync(new[] { new ProfileKey("gw", "default") }).GetAwaiter().GetResult();
            var gate = new NotificationGate(selection);
            var seen = new List<Seen>();
            using var auth = new DashboardAuth(new Uri("https://hermes.example/"), new Stub(seen, _ => Json(HttpStatusCode.OK, "{}")));
            var catalog = new SessionCatalog(gate, () => null);
            catalog.SetActiveProfile(new ProfileKey("gw", "private"));           // not selected here
            var vm = new MainViewModel(selection, gate, () => new DemoTransport(), () => "t",
                () => null, () => null, () => catalog, () => null, () => null, () => null, () => null, () => null);
            vm.AttachSessionActions(() => new SessionActionsApi(auth));
            vm.RunSessionActionAsync(Rig.Row("s1", "x", false, true), "pin").GetAwaiter().GetResult();
            Assert.Empty(seen);
            Assert.Contains("Choose a profile", vm.SessionActionStatus);
        });
    }

    [Fact]
    public void The_archived_view_toggles_and_is_in_the_palette()
    {
        RunSta(() =>
        {
            using var rig = new Rig();
            Assert.False(rig.Vm.ArchivedView);
            rig.Vm.ToggleArchivedViewCommand!.Execute(null);
            Assert.True(rig.Vm.ArchivedView);
            Assert.Equal("Archived ✓", rig.Vm.ArchivedViewLabel);
            rig.Vm.ToggleArchivedViewCommand.Execute(null);
            Assert.False(rig.Vm.ArchivedView);
            rig.Vm.OpenPaletteCommand!.Execute(null);
            rig.Vm.PaletteQuery = "archived";
            Assert.Contains(rig.Vm.PaletteResults, r => r.Id == "sessions.archived");
        });
    }
}

file sealed class MemPrefs27 : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
