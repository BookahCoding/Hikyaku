using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

public class SessionsApiTests
{
    private static readonly Uri Base = new("https://gateway.example.invalid/");
    private static readonly ProfileKey Profile = new("gw", "default");

    private sealed class SeqHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _f;
        public SeqHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> f) => _f = f;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => _f(r);
    }

    private static DashboardAuth Authed(Func<HttpRequestMessage, Task<HttpResponseMessage>> afterLogin)
    {
        var handler = new SeqHandler(async req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/password-login", StringComparison.Ordinal))
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json") };
                resp.Headers.TryAddWithoutValidation("Set-Cookie", "hermes_session_at=AT; Path=/; HttpOnly");
                return resp;
            }
            return await afterLogin(req);
        });
        var auth = new DashboardAuth(Base, handler);
        auth.SignInAsync("basic", "u", "p", default).GetAwaiter().GetResult();
        return auth;
    }

    [Fact]
    public async Task List_SendsProfileLimitOffsetAndExcludeSources()
    {
        string? query = null;
        using var auth = Authed(req =>
        {
            Assert.EndsWith("/api/sessions", req.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            query = req.RequestUri.Query;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"sessions":[{"id":"s1","title":"Hello","preview":"hi","source":"desktop","last_active":100}],"total":1,"limit":50,"offset":0}""",
                    Encoding.UTF8, "application/json")
            });
        });
        var api = new SessionsApi(auth);
        var page = await api.ListAsync("default");
        Assert.Single(page.Sessions);
        Assert.Equal("s1", page.Sessions[0].Id);
        Assert.Contains("profile=default", query);
        Assert.Contains("archived=exclude", query);
        Assert.Contains("exclude_sources=", query);
        Assert.Contains("cron", query);
        Assert.Contains("order=recent", query);
    }

    [Fact]
    public async Task Search_StripsMatchMarkers_AndRequiresProfileQuery()
    {
        string? query = null;
        using var auth = Authed(req =>
        {
            Assert.Contains("/api/sessions/search", req.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            query = req.RequestUri.Query;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"results":[{"id":"s2","snippet":">>>match<<< here"}]}""",
                    Encoding.UTF8, "application/json")
            });
        });
        var api = new SessionsApi(auth);
        var results = await api.SearchAsync("default", "match");
        Assert.Equal("match here", results[0].Snippet);
        Assert.Contains("q=match", query);
        Assert.Contains("profile=default", query);
    }

    [Fact]
    public async Task Messages_UsesLatestOrderAndIncludeCompacted()
    {
        string? query = null;
        using var auth = Authed(req =>
        {
            Assert.Contains("/api/sessions/s1/messages", req.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            query = req.RequestUri.Query;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"session_id":"s1","messages":[{"id":"m1","role":"user","content":"hi"}]}""",
                    Encoding.UTF8, "application/json")
            });
        });
        var api = new SessionsApi(auth);
        var page = await api.MessagesAsync("s1", "default");
        Assert.Equal("hi", page.Messages[0].Content);
        Assert.Contains("order=latest", query);
        Assert.Contains("include_compacted=true", query);
        Assert.Contains("profile=default", query);
    }

    [Fact]
    public async Task Catalog_SearchDeniedForDeselectedProfile_ClearsOnSwitch()
    {
        var prefs = new MemoryStore();
        var selection = new ProfileSelectionService(prefs);
        await selection.SetSelectedAsync(new[] { Profile });
        await selection.LoadAsync();
        var gate = new NotificationGate(selection);
        SessionsApi? api = null;
        using var auth = Authed(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/search", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"results":[{"id":"x"}]}""", Encoding.UTF8, "application/json") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"sessions":[{"id":"a"}],"total":1,"limit":50,"offset":0}""",
                    Encoding.UTF8, "application/json")
            });
        });
        api = new SessionsApi(auth);
        var catalog = new SessionCatalog(gate, () => api);
        catalog.SetActiveProfile(Profile);
        var listed = await catalog.LoadRecentAsync();
        Assert.Single(listed);

        catalog.SetActiveProfile(new ProfileKey("gw", "other"));
        Assert.Empty(catalog.Cached);

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SearchAsync("q"));
    }
}

public class OfflineChatTests
{
    private static readonly ProfileKey A = new("g1", "default");
    private static readonly ProfileKey B = new("g1", "other");

    [Fact]
    public async Task Drafts_ArePerProfile()
    {
        var drafts = new DraftStore(new MemoryStore());
        await drafts.SetAsync(A, "hello A");
        await drafts.SetAsync(B, "hello B");
        Assert.Equal("hello A", await drafts.GetAsync(A));
        Assert.Equal("hello B", await drafts.GetAsync(B));
        await drafts.SetAsync(A, "");
        Assert.Equal("", await drafts.GetAsync(A));
    }

    [Fact]
    public async Task OfflineTranscript_RoundTrips_AndDropsDeselected()
    {
        var sealer = AesGcmSealer.CreateRandom();
        var allowed = new HashSet<ProfileKey> { A };
        var cache = new OfflineChatCache(new MemoryStore(), sealer, k => allowed.Contains(k));
        var t = new OfflineTranscript("sess1", A,
            new[] { new OfflineTranscriptMessage("user", "hi"), new OfflineTranscriptMessage("assistant", "yo") },
            1_700_000_000);
        await cache.SaveTranscriptAsync(t);
        var loaded = await cache.LoadTranscriptAsync(A, "sess1");
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Messages.Count);
        Assert.True(loaded.SavedCopyAtUnix > 0);

        allowed.Clear();
        Assert.Null(await cache.LoadTranscriptAsync(A, "sess1"));
    }
}

public class GatewayFileTests
{
    [Theory]
    [InlineData("/opt/data/report.md", true)]
    [InlineData("~/notes.txt", true)]
    [InlineData("file:///home/me/a.txt", true)]
    [InlineData("https://evil.example/x", false)]
    [InlineData("mailto:a@b.c", false)]
    [InlineData("file://evil.example/etc/passwd", false)]
    public void FromLink_AcceptsGatewayPaths_RejectsRemote(string link, bool ok)
    {
        var parsed = GatewayFile.TryParse(link, out var path, out _);
        Assert.Equal(ok, parsed);
        if (ok) Assert.False(string.IsNullOrEmpty(path));
    }

    [Fact]
    public void Normalize_BlocksPathEscapePastRoot()
    {
        Assert.Equal("/a/b", GatewayFile.Normalize("/a/../a/b"));
        Assert.Equal("/b", GatewayFile.Normalize("/../../b"));
    }

    [Fact]
    public void SafeDownloadFileName_StripsDirectories()
    {
        Assert.Equal("report.md", GatewayFile.SafeDownloadFileName("/opt/../etc/report.md"));
        Assert.Equal("file", GatewayFile.SafeDownloadFileName("/opt/../"));
    }
}

public class AttachmentQueueTests
{
    private static readonly ProfileKey A = new("g1", "default");
    private static readonly ProfileKey B = new("g1", "other");

    [Fact]
    public async Task Queue_RejectsOtherProfile_AndOversize()
    {
        var prefs = new MemoryStore();
        var selection = new ProfileSelectionService(prefs);
        await selection.SetSelectedAsync(new[] { A });
        await selection.LoadAsync();
        var gate = new NotificationGate(selection);
        var q = new AttachmentQueue();
        q.Bind(A, "sess");
        Assert.Null(q.TryAdd(A, gate, new OutgoingAttachment("1", "a.png", "image/png", new byte[16])));
        Assert.NotNull(q.TryAdd(B, gate, new OutgoingAttachment("2", "b.png", "image/png", new byte[16])));
        Assert.Throws<ArgumentException>(() =>
            new OutgoingAttachment("3", "big.bin", "application/octet-stream", new byte[OutgoingAttachment.MaxBytes + 1]));
    }

    [Fact]
    public async Task Queue_ClearsWhenProfileDeselected()
    {
        var prefs = new MemoryStore();
        var selection = new ProfileSelectionService(prefs);
        await selection.SetSelectedAsync(new[] { A });
        await selection.LoadAsync();
        var gate = new NotificationGate(selection);
        var q = new AttachmentQueue();
        Assert.Null(q.TryAdd(A, gate, new OutgoingAttachment("1", "a.txt", "text/plain", "hi"u8.ToArray())));
        Assert.Single(q.Items);
        await selection.SetSelectedAsync(Array.Empty<ProfileKey>());
        q.EnsureOwner(A, gate);
        Assert.Empty(q.Items);
    }
}

public class AttachmentUploadFixtureTests
{
    [Fact]
    public async Task UploadImage_UsesAttachBytes_AndDetachOnFailureCleanup()
    {
        var transport = new FakeRpcTransport();
        transport.Push(FakeRpcTransport.ReadyFrame);
        await using var client = new JsonRpcClient(transport);
        await client.Ready.WaitAsync(CancellationToken.None);
        var uploader = new AttachmentUploader(() => client);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var answer = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                foreach (var frame in transport.Sent.ToArray())
                {
                    using var doc = JsonDocument.Parse(frame);
                    if (doc.RootElement.TryGetProperty("method", out var m) &&
                        m.GetString() == "image.attach_bytes" &&
                        doc.RootElement.TryGetProperty("id", out var id))
                    {
                        var reply = new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = JsonNode.Parse(id.GetRawText()),
                            ["result"] = new JsonObject { ["attached"] = true, ["path"] = "/tmp/img.png" }
                        };
                        transport.Push(reply.ToJsonString());
                        return;
                    }
                }
                await Task.Delay(5, cts.Token);
            }
        }, cts.Token);

        var file = new OutgoingAttachment("1", "x.png", "image/png", new byte[] { 1, 2, 3 });
        var result = await uploader.UploadAsync("runtime-1", file, cts.Token);
        await answer;
        Assert.True(result.Ok);
        Assert.Equal("/tmp/img.png", result.Path);
        Assert.Contains(transport.Sent, s => s.Contains("image.attach_bytes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MediaDownload_RefusesUntrustedRemotePath()
    {
        var handler = new FixedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[] { 1 }) });
        using var auth = new DashboardAuth(new Uri("https://gateway.example.invalid/"), handler);
        var media = new MediaApi(auth);
        await Assert.ThrowsAsync<InvalidOperationException>(() => media.DownloadAsync("https://evil/x", "default"));
    }

    private sealed class FixedHandler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(f(request));
    }
}

public class SessionResumeFixtureTests
{
    [Fact]
    public async Task Resume_BindsRuntimeIdToProfile_RejectsCrossProfile()
    {
        var transport = new FakeRpcTransport();
        transport.Push(FakeRpcTransport.ReadyFrame);
        var openCount = 0;
        using var auth = new DashboardAuth(new Uri("https://gateway.example.invalid/"), new TicketHandler());
        await auth.SignInAsync("basic", "u", "p", default);
        await using var hermes = new HermesDashboardTransport(auth, "gw", openSocket: (_, _) =>
        {
            openCount++;
            return Task.FromResult<IRpcTransport>(transport);
        }, ownsAuth: false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        async Task AnswerResumesAsync()
        {
            var seen = 0;
            while (!cts.IsCancellationRequested)
            {
                var sent = transport.Sent;
                for (; seen < sent.Count; seen++)
                {
                    using var doc = JsonDocument.Parse(sent[seen]);
                    if (!doc.RootElement.TryGetProperty("method", out var m)) continue;
                    if (m.GetString() != "session.resume") continue;
                    var id = doc.RootElement.GetProperty("id").GetRawText();
                    transport.Push("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"session_id\":\"runtime-9\",\"running\":false}}");
                }
                await Task.Delay(5, cts.Token);
            }
        }
        var answer = Task.Run(AnswerResumesAsync, cts.Token);

        var profile = new ProfileKey("gw", "default");
        var result = await hermes.ResumeSessionAsync(profile, "stored-1", cts.Token);
        Assert.Equal("runtime-9", result.RuntimeSessionId);
        Assert.True(openCount >= 1);

        // Same runtime id claimed for another profile must fail (ownership kept from first resume).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            hermes.ResumeSessionAsync(new ProfileKey("gw", "other"), "stored-2", cts.Token));
        cts.Cancel();
        try { await answer; } catch (OperationCanceledException) { }
    }

    private sealed class TicketHandler : HttpMessageHandler
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
}

public class InboxFinishingTests
{
    private static readonly ProfileKey A = new("g1", "default");

    [Fact]
    public async Task OpenRun_MarksRead_AndCallsApiWhenConnected()
    {
        var prefs = new MemoryStore();
        var selection = new ProfileSelectionService(prefs);
        await selection.SetSelectedAsync(new[] { A });
        await selection.LoadAsync();
        var gate = new NotificationGate(selection);
        var store = new InboxStore(new MemoryStore(), (_, bot) =>
            gate.Allow(Surface.Inbox, new ProfileKey("g1", bot)));
        await store.BindAsync("https://gw/", 100);
        var run = new InboxRun("default", "job-a", "2026-10-12_08-00-00", At: 200, Text: "short");
        await store.AddAsync("https://gw/", run);
        Assert.True(store.Snapshot.IsUnread(run));

        var transport = new FakeRpcTransport();
        transport.Push(FakeRpcTransport.ReadyFrame);
        await using var client = new JsonRpcClient(transport);
        await client.Ready.WaitAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var answer = Task.Run(async () =>
        {
            var seen = 0;
            while (!cts.IsCancellationRequested)
            {
                var sent = transport.Sent;
                for (; seen < sent.Count; seen++)
                {
                    using var doc = JsonDocument.Parse(sent[seen]);
                    if (!doc.RootElement.TryGetProperty("method", out var m)) continue;
                    if (m.GetString() != "command.dispatch") continue;
                    var id = doc.RootElement.GetProperty("id").GetRawText();
                    var arg = doc.RootElement.GetProperty("params").GetProperty("arg").GetString() ?? "";
                    Assert.Contains("cron read", arg, StringComparison.Ordinal);
                    var output = JsonSerializer.Serialize(new
                    {
                        ok = true,
                        run = new { bot = "default", job = "job-a", run = "2026-10-12_08-00-00", text = "full body", status = "ok", at = 200L }
                    });
                    var outputEsc = JsonSerializer.Serialize(output);
                    transport.Push("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"type\":\"plugin\",\"output\":" + outputEsc + "}}");
                    return;
                }
                await Task.Delay(5, cts.Token);
            }
        }, cts.Token);

        var svc = new InboxService(store, gate, "https://gw/", "g1", () => client, "desktop-1");
        var opened = await svc.OpenRunAsync(run, cts.Token);
        await answer;
        Assert.NotNull(opened);
        Assert.Equal("full body", opened!.Text);
        Assert.False(store.Snapshot.IsUnread(run));
    }

    [Fact]
    public async Task Filter_ByJobAndGroup()
    {
        var prefs = new MemoryStore();
        var selection = new ProfileSelectionService(prefs);
        await selection.SetSelectedAsync(new[] { A });
        await selection.LoadAsync();
        var gate = new NotificationGate(selection);
        var store = new InboxStore(new MemoryStore(), (_, bot) =>
            gate.Allow(Surface.Inbox, new ProfileKey("g1", bot)));
        await store.BindAsync("https://gw/", 1);
        await store.ReplaceAsync("https://gw/", new[]
        {
            new InboxRun("default", "alpha", "r1", At: 3, Text: "a"),
            new InboxRun("default", "beta", "r2", At: 2, Status: "failed", Text: "b"),
        }, complete: true);
        var groups = InboxGroups.Empty.WithJob("alpha", "Mine");
        var svc = new InboxService(store, gate, "https://gw/", "g1", () => null, "desktop-1");
        Assert.Single(svc.Filter(false, jobId: "alpha"));
        Assert.Single(svc.Filter(true));
        Assert.Single(svc.Filter(false, groupName: "Mine", groups: groups));
        Assert.Empty(svc.Filter(false, groupName: "Other", groups: groups));
    }

    [Fact]
    public async Task SetFollowing_CallsHeraldPushFollow()
    {
        var prefs = new MemoryStore();
        var selection = new ProfileSelectionService(prefs);
        await selection.SetSelectedAsync(new[] { A });
        await selection.LoadAsync();
        var gate = new NotificationGate(selection);
        var store = new InboxStore(new MemoryStore(), (_, _) => true);
        await store.BindAsync("https://gw/", 1);

        var transport = new FakeRpcTransport();
        transport.Push(FakeRpcTransport.ReadyFrame);
        await using var client = new JsonRpcClient(transport);
        await client.Ready.WaitAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var answer = Task.Run(async () =>
        {
            var seen = 0;
            while (!cts.IsCancellationRequested)
            {
                var sent = transport.Sent;
                for (; seen < sent.Count; seen++)
                {
                    using var doc = JsonDocument.Parse(sent[seen]);
                    if (!doc.RootElement.TryGetProperty("method", out var m)) continue;
                    if (m.GetString() != "command.dispatch") continue;
                    var arg = doc.RootElement.GetProperty("params").GetProperty("arg").GetString() ?? "";
                    Assert.StartsWith("cron follow ", arg, StringComparison.Ordinal);
                    var id = doc.RootElement.GetProperty("id").GetRawText();
                    var output = "{\"ok\":true,\"jobs\":{\"job-a\":\"quiet\"},\"failures\":true,\"default\":\"off\"}";
                    var outputEsc = JsonSerializer.Serialize(output);
                    transport.Push("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"type\":\"plugin\",\"output\":" + outputEsc + "}}");
                    return;
                }
                await Task.Delay(5, cts.Token);
            }
        }, cts.Token);

        var svc = new InboxService(store, gate, "https://gw/", "g1", () => client, "desktop-1");
        var following = new Following(new Dictionary<string, FollowMode> { ["job-a"] = FollowMode.Quiet }, Failures: true);
        var saved = await svc.SetFollowingAsync(following, cts.Token);
        await answer;
        Assert.Equal(FollowMode.Quiet, saved.Of("job-a"));
        Assert.True(saved.Failures);
    }
}
