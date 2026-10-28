using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

public class InboxStoreTests
{
    private static readonly ProfileKey A = new("g1", "default");

    [Fact]
    public async Task CacheIdentity_DropsDeselectedAndUnknownProfiles()
    {
        var prefs = new MemoryStore();
        var selection = new ProfileSelectionService(prefs);
        await selection.SetSelectedAsync(new[] { A });
        await selection.LoadAsync();
        var gate = new NotificationGate(selection);
        var store = new InboxStore(new MemoryStore(), (url, bot) =>
            gate.Allow(Surface.Inbox, new ProfileKey("g1", bot)));

        await store.BindAsync("https://gw.example/", 1_700_000_000);
        await store.ReplaceAsync("https://gw.example/", new[]
        {
            new InboxRun("default", "job-a", "2026-10-12_08-00-00", Status: "ok", At: 100, Text: "a"),
            new InboxRun("other", "job-b", "2026-10-12_09-00-00", Status: "failed", At: 200, Text: "b"),
            new InboxRun("unknown", "job-c", "2026-10-12_10-00-00", Status: "ok", At: 300, Text: "c"),
        }, complete: true);

        Assert.Single(store.Snapshot.Runs);
        Assert.Equal("job-a", store.Snapshot.Runs[0].Job);

        await selection.SetSelectedAsync(Array.Empty<ProfileKey>());
        await store.EnforceAsync("https://gw.example/");
        Assert.Empty(store.Snapshot.Runs);
    }

    [Fact]
    public async Task DuplicateRuns_MergeKeepsLongerText_UnreadChanges()
    {
        var store = new InboxStore(new MemoryStore(), (_, _) => true);
        await store.BindAsync("https://gw.example/", 50);
        var run = new InboxRun("default", "j", "2026-10-12_08-00-00", At: 100, Text: "short");
        await store.AddAsync("https://gw.example/", run);
        await store.AddAsync("https://gw.example/", run with { Text = "much longer body text" });
        Assert.Single(store.Snapshot.Runs);
        Assert.Equal("much longer body text", store.Snapshot.Runs[0].Text);
        Assert.True(store.Snapshot.IsUnread(store.Snapshot.Runs[0]));
        await store.MarkReadAsync("https://gw.example/", store.Snapshot.Runs[0]);
        Assert.False(store.Snapshot.IsUnread(store.Snapshot.Runs[0]));
        Assert.Equal(0, store.Snapshot.Unread);
    }

    [Fact]
    public void MergeListed_PreservesNewerPushWhenListLags()
    {
        var kept = new[] { new InboxRun("default", "j", "2026-10-12_11-00-00", At: 500, Text: "push") };
        var listed = new[] { new InboxRun("default", "j", "2026-10-12_08-00-00", At: 100, Text: "old") };
        var merged = InboxStore.MergeListed(kept, listed, complete: true);
        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, r => r.At == 500);
    }
}

public class InboxApiFixtureTests
{
    private static readonly Uri Base = new("https://gateway.example.invalid/");

    private sealed class AuthHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/password-login", StringComparison.Ordinal))
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json") };
                resp.Headers.TryAddWithoutValidation("Set-Cookie", "hermes_session_at=AT; Path=/; HttpOnly");
                return Task.FromResult(resp);
            }
            if (path.EndsWith("/api/auth/ws-ticket", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"ticket":"T","ttl_seconds":30}""", Encoding.UTF8, "application/json") });
            throw new InvalidOperationException(path);
        }
    }

    private static async Task AnswerLoop(FakeRpcTransport transport, Func<string, string, string?> answer, CancellationToken ct)
    {
        var seen = 0;
        while (!ct.IsCancellationRequested)
        {
            var sent = transport.Sent;
            for (; seen < sent.Count; seen++)
            {
                using var doc = JsonDocument.Parse(sent[seen]);
                if (!doc.RootElement.TryGetProperty("method", out var m)) continue;
                var method = m.GetString()!;
                var id = doc.RootElement.GetProperty("id").GetString()!;
                var body = answer(method, sent[seen]);
                if (body is null) continue;
                transport.Push("{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":" + body + "}");
            }
            await Task.Delay(5, ct);
        }
    }

    [Fact]
    public async Task MissingPlugin_IsSetupRequired_NotFakeRuns()
    {
        FakeRpcTransport? sock = null;
        using var auth = new DashboardAuth(Base, new AuthHandler());
        await auth.SignInAsync("basic", "u", "p", default);
        await using var transport = new HermesDashboardTransport(auth, "gw1", (_, _) =>
        {
            sock = new FakeRpcTransport();
            sock.Push(FakeRpcTransport.ReadyFrame);
            return Task.FromResult<IRpcTransport>(sock);
        }, ownsAuth: false);
        await transport.EnsureConnectedAsync(default);

        using var answerCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (sock is null) await Task.Delay(5);
            await AnswerLoop(sock, (method, _) => method switch
            {
                "client.capabilities" => "{}",
                "command.dispatch" => null, // hang then we inject error via code path — actually throw via error frame
                _ => null
            }, answerCts.Token);
        });

        // Answer command.dispatch with RPC 4018
        _ = Task.Run(async () =>
        {
            while (sock is null) await Task.Delay(5);
            for (var i = 0; i < 200; i++)
            {
                var raw = sock.Sent.LastOrDefault(s => s.Contains("command.dispatch"));
                if (raw is not null)
                {
                    using var doc = JsonDocument.Parse(raw);
                    var id = doc.RootElement.GetProperty("id").GetString();
                    sock.Push("{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"error\":{\"code\":4018,\"message\":\"no such command\"}}");
                    return;
                }
                await Task.Delay(10);
            }
        });

        var prefs = new MemoryStore();
        var selection = new ProfileSelectionService(prefs);
        await selection.SetSelectedAsync(new[] { new ProfileKey("gw1", "default") });
        await selection.LoadAsync();
        var gate = new NotificationGate(selection);
        var store = new InboxStore(new MemoryStore(), (url, bot) => gate.Allow(Surface.Inbox, new ProfileKey("gw1", bot)));
        var svc = new InboxService(store, gate, "https://gateway.example.invalid/", "gw1",
            () => transport.Client, "desktop-device-1");

        await svc.RefreshAsync();
        answerCts.Cancel();
        Assert.Equal(InboxConnectionState.SetupRequired, svc.State);
        Assert.Contains("herald-push", svc.StatusDetail ?? "");
        Assert.Empty(svc.Snapshot.Runs);
    }

    [Fact]
    public async Task LoadsRuns_FiltersFailuresAndDeselected()
    {
        FakeRpcTransport? sock = null;
        using var auth = new DashboardAuth(Base, new AuthHandler());
        await auth.SignInAsync("basic", "u", "p", default);
        await using var transport = new HermesDashboardTransport(auth, "gw1", (_, _) =>
        {
            sock = new FakeRpcTransport();
            sock.Push(FakeRpcTransport.ReadyFrame);
            return Task.FromResult<IRpcTransport>(sock);
        }, ownsAuth: false);
        await transport.EnsureConnectedAsync(default);

        using var answerCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (sock is null) await Task.Delay(5);
            await AnswerLoop(sock, (method, raw) =>
            {
                if (method == "client.capabilities") return "{}";
                if (method != "command.dispatch") return null;
                Assert.Contains("herald-push", raw);
                Assert.Contains("cron", raw);
                if (raw.Contains("following"))
                    return """{"type":"plugin","output":"{\"ok\":true,\"jobs\":{\"job-a\":\"notify\"},\"failures\":true,\"default\":\"off\"}"}""";
                if (raw.Contains("inbox"))
                    return """{"type":"plugin","output":"{\"ok\":true,\"runs\":[{\"bot\":\"default\",\"job\":\"job-a\",\"run\":\"2026-10-12_08-00-00\",\"status\":\"ok\",\"at\":100,\"text\":\"hi\"},{\"bot\":\"default\",\"job\":\"job-a\",\"run\":\"2026-10-12_09-00-00\",\"status\":\"failed\",\"at\":200,\"text\":\"boom\"},{\"bot\":\"other\",\"job\":\"job-b\",\"run\":\"2026-10-12_10-00-00\",\"status\":\"ok\",\"at\":300,\"text\":\"nope\"}]}"}""";
                return null;
            }, answerCts.Token);
        });

        var selection = new ProfileSelectionService(new MemoryStore());
        await selection.SetSelectedAsync(new[] { new ProfileKey("gw1", "default") });
        await selection.LoadAsync();
        var gate = new NotificationGate(selection);
        var store = new InboxStore(new MemoryStore(), (_, bot) => gate.Allow(Surface.Inbox, new ProfileKey("gw1", bot)));
        var svc = new InboxService(store, gate, "https://gateway.example.invalid/", "gw1",
            () => transport.Client, "desktop-device-1");

        await svc.RefreshAsync();
        answerCts.Cancel();
        Assert.Equal(InboxConnectionState.Ready, svc.State);
        Assert.Equal(2, svc.Snapshot.Runs.Count);
        Assert.DoesNotContain(svc.Snapshot.Runs, r => r.Bot == "other");
        Assert.Single(svc.Filter(failuresOnly: true));
        Assert.Equal("notify", FollowModeWire.ToWire(svc.Following.Of("job-a")));
    }

    [Fact]
    public async Task Disconnected_WhenNoClient()
    {
        var selection = new ProfileSelectionService(new MemoryStore());
        await selection.SetSelectedAsync(new[] { new ProfileKey("gw1", "default") });
        await selection.LoadAsync();
        var gate = new NotificationGate(selection);
        var store = new InboxStore(new MemoryStore(), (_, _) => true);
        var svc = new InboxService(store, gate, "https://gw/", "gw1", () => null, "dev");
        await svc.RefreshAsync();
        Assert.Equal(InboxConnectionState.Disconnected, svc.State);
    }
}

public class NeedsYouTests
{
    [Fact]
    public async Task ApprovalDisplayAndAnswer_Scoped_ClearsOnWithdraw()
    {
        var selection = new ProfileSelectionService(new MemoryStore());
        await selection.SetSelectedAsync(new[] { new ProfileKey("gw1", "default") });
        await selection.LoadAsync();
        var gate = new NotificationGate(selection);
        var sock = new FakeRpcTransport();
        await using var client = new JsonRpcClient(sock);
        sock.Push(FakeRpcTransport.ReadyFrame);
        await client.Ready.WaitAsync(TimeSpan.FromSeconds(2));

        var needs = new NeedsYouService(gate, selection, "gw1", () => client);
        var ev = new GatewayEvent("server.request:approval",
            JsonNode.Parse("""{"command":"rm -rf /tmp/x","description":"delete","profile":"default"}""") as JsonObject,
            "sess-1", null, "srq-1");
        Assert.True(needs.HandleGatewayEvent(ev));
        Assert.Single(needs.Pending);
        Assert.IsType<ApprovalRequest>(needs.Pending[0]);

        await needs.AnswerApprovalAsync("srq-1", ApprovalChoice.Deny);
        Assert.Empty(needs.Pending);
        Assert.Contains(sock.Sent, s => s.Contains("\"id\":\"srq-1\"") && s.Contains("deny") && !s.Contains("method"));
    }

    [Fact]
    public async Task SecretAnswer_NotCachedInPending_AndDeselectedDenied()
    {
        var store = new MemoryStore();
        var selection = new ProfileSelectionService(store);
        await selection.SetSelectedAsync(new[] { new ProfileKey("gw1", "default") });
        await selection.LoadAsync();
        var gate = new NotificationGate(selection);
        var sock = new FakeRpcTransport();
        await using var client = new JsonRpcClient(sock);
        sock.Push(FakeRpcTransport.ReadyFrame);
        await client.Ready.WaitAsync(TimeSpan.FromSeconds(2));
        var needs = new NeedsYouService(gate, selection, "gw1", () => client);

        Assert.True(needs.HandleGatewayEvent(new GatewayEvent("server.request:secret",
            JsonNode.Parse("""{"env_var":"TOKEN","profile":"default"}""") as JsonObject,
            null, null, "srq-secret")));
        await needs.AnswerSecretAsync("srq-secret", "super-secret-value-never-store");
        Assert.Empty(needs.Pending);
        Assert.DoesNotContain("super-secret-value-never-store", store.Content ?? "");

        // After deselect, new secret for that profile is denied.
        await selection.SetSelectedAsync(Array.Empty<ProfileKey>());
        Assert.False(needs.HandleGatewayEvent(new GatewayEvent("server.request:secret",
            JsonNode.Parse("""{"env_var":"TOKEN","profile":"default"}""") as JsonObject,
            null, null, "srq-2")));
    }

    [Fact]
    public async Task RequestCancel_ClearsPending_NoReplay()
    {
        var selection = new ProfileSelectionService(new MemoryStore());
        await selection.SetSelectedAsync(new[] { new ProfileKey("gw1", "default") });
        await selection.LoadAsync();
        var needs = new NeedsYouService(new NotificationGate(selection), selection, "gw1", () => null);
        needs.HandleGatewayEvent(new GatewayEvent("server.request:clarify",
            JsonNode.Parse("""{"question":"Ship it?","profile":"default"}""") as JsonObject,
            "s1", null, "srq-c"));
        Assert.Single(needs.Pending);
        needs.HandleGatewayEvent(new GatewayEvent("request.cancel",
            JsonNode.Parse("""{"id":"srq-c"}""") as JsonObject, "s1", null));
        Assert.Empty(needs.Pending);
        // Replay of cancel is a no-op; request stays gone.
        needs.HandleGatewayEvent(new GatewayEvent("request.cancel",
            JsonNode.Parse("""{"id":"srq-c"}""") as JsonObject, "s1", null));
        Assert.Empty(needs.Pending);
    }

    [Fact]
    public void ColdStart_DeniesNeedsYou()
    {
        var selection = new ProfileSelectionService(new MemoryStore());
        // not loaded
        var needs = new NeedsYouService(new NotificationGate(selection), selection, "gw1", () => null);
        Assert.False(needs.HandleGatewayEvent(new GatewayEvent("server.request:approval",
            JsonNode.Parse("""{"command":"x","profile":"default"}""") as JsonObject,
            null, null, "srq-x")));
    }
}
