using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 43 — live finding: an Inbox result (a scheduled run's stored chat) opened as "Could not resume or load this session.
/// Check connection." Opening resumed the chat and loaded its history in one step, so a refused resume hid a readable history.
/// </summary>
[Collection("StaMath")]
public class Checkpoint43RunChatWpfTests
{
    private sealed class Gateway : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (path.EndsWith("/auth/password-login", StringComparison.Ordinal))
            {
                var r = Json("""{"ok":true}""");
                r.Headers.TryAddWithoutValidation("Set-Cookie", "hermes_session_at=AT; Path=/; HttpOnly");
                return Task.FromResult(r);
            }
            if (path.EndsWith("/api/auth/ws-ticket", StringComparison.Ordinal)) return Task.FromResult(Json("""{"ticket":"T","ttl_seconds":30}"""));
            if (path.Contains("/api/sessions/cron_a1_20261027_090000/messages"))
                return Task.FromResult(Json("""{"session_id":"cron_a1_20261027_090000","messages":[{"id":1,"role":"user","content":"Summarise my inbox"},{"id":2,"role":"assistant","content":"Synthetic: three new mails."}]}"""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class MemPrefs : IPreferenceStore
    {
        private string? _c;
        public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_c);
        public Task SaveAsync(string content, CancellationToken ct = default) { _c = content; return Task.CompletedTask; }
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
    public void A_scheduled_runs_chat_opens_with_its_history_even_when_the_gateway_refuses_to_resume_it()
    {
        RunSta(() =>
        {
            var auth = new DashboardAuth(new Uri("https://hermes.example/"), new Gateway());
            auth.SignInAsync("basic", "u", "p", default).GetAwaiter().GetResult();
            FakeRpcTransport? sock = null;
            var transport = new HermesDashboardTransport(auth, "gw", (_, _) =>
            {
                sock = new FakeRpcTransport();
                sock.Push(FakeRpcTransport.ReadyFrame);
                return Task.FromResult<IRpcTransport>(sock);
            }, ownsAuth: true);
            using var stop = new CancellationTokenSource();
            _ = Task.Run(async () =>                                                       // the gateway refuses every resume
            {
                var seen = 0;
                while (!stop.IsCancellationRequested)
                {
                    if (sock is { } s)
                        for (; seen < s.Sent.Count; seen++)
                        {
                            using var doc = JsonDocument.Parse(s.Sent[seen]);
                            if (!doc.RootElement.TryGetProperty("method", out var m)) continue;
                            var id = doc.RootElement.GetProperty("id").GetString();
                            s.Push(m.GetString() == "session.resume"
                                ? "{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"error\":{\"code\":4004,\"message\":\"synthetic: this session cannot be resumed\"}}"
                                : "{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":{}}");
                        }
                    await Task.Delay(5);
                }
            });

            var selection = new ProfileSelectionService(new MemPrefs());
            selection.SetSelectedAsync([new ProfileKey("gw", "default")]).GetAwaiter().GetResult();
            var gate = new NotificationGate(selection);
            var catalog = new SessionCatalog(gate, () => new SessionsApi(auth));
            catalog.SetActiveProfile(new ProfileKey("gw", "default"));
            var vm = new MainViewModel(selection, gate, () => transport, () => "t",
                () => null, () => null, () => catalog, () => null, () => null, () => null, () => null, () => null);

            vm.OpenSessionCommand.Execute(new SessionRowVM(new SessionSummary("cron_a1_20261027_090000", "Morning summary")));
            SpinWait.SpinUntil(() => vm.SelectedConversation?.StoredSessionId == "cron_a1_20261027_090000", 5000);
            stop.Cancel();

            var chat = vm.SelectedConversation!;
            Assert.Contains(chat.History, m => m.Text == "Synthetic: three new mails.");     // readable, not "Could not resume or load"
            Assert.DoesNotContain(chat.History, m => m.Text.Contains("Could not resume"));
            Assert.Null(chat.SessionId);                                                    // not attached; a reply will try to resume again
        });
    }
}
