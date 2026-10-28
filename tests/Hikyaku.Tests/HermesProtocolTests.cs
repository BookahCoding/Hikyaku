using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

public class DashboardAuthTests
{
    private static readonly Uri Base = new("https://gateway.example.invalid/");

    private sealed class SeqHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, Task<HttpResponseMessage>> _f;
        private int _n;
        public SeqHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> f) => _f = f;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => _f(r, Interlocked.Increment(ref _n) - 1);
    }

    [Fact]
    public async Task PasswordLoginStoresCookiesAndSendsThemOnTicketMint()
    {
        string? ticketCookie = null;
        var handler = new SeqHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/password-login", StringComparison.Ordinal))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                Assert.Contains("\"provider\":\"basic\"", body);
                Assert.Contains("\"username\":\"me\"", body);
                Assert.DoesNotContain("pw-secret-value", req.RequestUri.ToString());
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
                };
                resp.Headers.TryAddWithoutValidation("Set-Cookie",
                    "hermes_session_at=AT1; Path=/; HttpOnly");
                resp.Headers.TryAddWithoutValidation("Set-Cookie",
                    "hermes_session_rt=RT1; Path=/; HttpOnly");
                return Task.FromResult(resp);
            }
            if (path.EndsWith("/api/auth/ws-ticket", StringComparison.Ordinal))
            {
                ticketCookie = req.Headers.TryGetValues("Cookie", out var c) ? string.Join("; ", c) : null;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"ticket":"T-123","ttl_seconds":30}""", Encoding.UTF8, "application/json")
                });
            }
            throw new InvalidOperationException("unexpected " + path);
        });

        using var auth = new DashboardAuth(Base, handler);
        await auth.SignInAsync("basic", "me", "pw-secret-value", default);
        Assert.True(auth.HasSession);
        var ticket = await auth.MintWsTicketAsync(default);
        Assert.Equal("T-123", ticket);
        Assert.Contains("hermes_session_at=AT1", ticketCookie);
        Assert.Contains("hermes_session_rt=RT1", ticketCookie);
        Assert.Equal("hermes-gateway-v1, hermes-gateway-ticket.T-123", DashboardAuth.TicketProtocols(ticket));
        Assert.DoesNotContain("T-123", DashboardAuth.WebSocketUri(Base).ToString());
    }

    [Fact]
    public async Task BadPasswordIsInvalidCredentials()
    {
        var handler = new SeqHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"detail":"Invalid credentials"}""", Encoding.UTF8, "application/json")
        }));
        using var auth = new DashboardAuth(Base, handler);
        var ex = await Assert.ThrowsAsync<AuthFailedException>(() => auth.SignInAsync("basic", "me", "wrong", default));
        Assert.Equal("invalid_credentials", ex.Kind);
    }

    [Fact]
    public async Task RateLimitedLogin()
    {
        var handler = new SeqHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"detail":"Too many login attempts."}""", Encoding.UTF8, "application/json")
        }));
        using var auth = new DashboardAuth(Base, handler);
        var ex = await Assert.ThrowsAsync<AuthFailedException>(() => auth.SignInAsync("basic", "me", "pw", default));
        Assert.Equal("rate_limited", ex.Kind);
    }

    [Fact]
    public async Task ExpiredSessionOnTicketMint()
    {
        var handler = new SeqHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"reason":"session_expired"}""", Encoding.UTF8, "application/json")
        }));
        using var auth = new DashboardAuth(Base, handler);
        var ex = await Assert.ThrowsAsync<AuthFailedException>(() => auth.MintWsTicketAsync(default));
        Assert.Equal("session_expired", ex.Kind);
    }

    [Fact]
    public async Task ListProfilesParsesRoster()
    {
        var handler = new SeqHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"profiles":[{"name":"default","is_default":true,"display_name":"Default"},{"name":"work","display_name":"Work"}]}""",
                Encoding.UTF8, "application/json")
        }));
        using var auth = new DashboardAuth(Base, handler);
        var profiles = await auth.ListProfilesAsync("gw1", default);
        Assert.Equal(2, profiles.Count);
        Assert.Equal(new ProfileKey("gw1", "default"), profiles[0].Key);
        Assert.Equal("Work", profiles[1].DisplayName);
    }
}

public class JsonRpcClientTests
{
    [Fact]
    public async Task ReadyAdvertisesClientCapabilitiesAndAnswersRequests()
    {
        var transport = new FakeRpcTransport();
        await using var client = new JsonRpcClient(transport);
        transport.Push(FakeRpcTransport.ReadyFrame);

        // Answer client.capabilities
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                var raw = transport.FirstCall("client.capabilities");
                if (raw is not null)
                {
                    using var doc = JsonDocument.Parse(raw);
                    var id = doc.RootElement.GetProperty("id").GetString();
                    transport.Push("{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":{\"declines_not_shown\":true}}");
                    return;
                }
                await Task.Delay(10);
            }
        });

        var ready = await client.Ready.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("e1", ready["replay_epoch"]?.GetValue<string>());

        var answered = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                foreach (var s in transport.Sent)
                {
                    using var doc = JsonDocument.Parse(s);
                    if (doc.RootElement.TryGetProperty("method", out var m) && m.GetString() == "session.create")
                    {
                        var id = doc.RootElement.GetProperty("id").GetString();
                        transport.Push("{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":{\"session_id\":\"sess-1\"}}");
                        answered.TrySetResult();
                        return;
                    }
                }
                await Task.Delay(10);
            }
        });

        var result = await client.RequestAsync("session.create", new JsonObject { ["profile"] = "default" });
        Assert.Equal("sess-1", result?["session_id"]?.GetValue<string>());
        await answered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RpcErrorSurfacesCodeAndReason()
    {
        var transport = new FakeRpcTransport();
        await using var client = new JsonRpcClient(transport);
        transport.Push(FakeRpcTransport.ReadyFrame);
        await client.Ready.WaitAsync(TimeSpan.FromSeconds(2));

        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                var raw = transport.Sent.LastOrDefault(s => s.Contains("prompt.submit"));
                if (raw is not null)
                {
                    using var doc = JsonDocument.Parse(raw);
                    var id = doc.RootElement.GetProperty("id").GetString();
                    transport.Push("{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"error\":{\"code\":4090,\"message\":\"busy\",\"data\":{\"reason\":\"SESSION_NOT_OWNED\"}}}");
                    return;
                }
                await Task.Delay(10);
            }
        });

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            client.RequestAsync("prompt.submit", new JsonObject { ["session_id"] = "s", ["text"] = "hi" }));
        Assert.Equal(4090, ex.Code);
        Assert.Equal("SESSION_NOT_OWNED", ex.Reason);
    }
}

public class HermesWireFixtureTests
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
                { Content = new StringContent("""{"ticket":"TICKET1","ttl_seconds":30}""", Encoding.UTF8, "application/json") });
            if (path.EndsWith("/api/profiles", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"profiles":[{"name":"default","display_name":"Default"}]}""", Encoding.UTF8, "application/json")
                });
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
                if (body.StartsWith('{') && body.Contains("\"id\""))
                    transport.Push(body);
                else
                    transport.Push("{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":" + body + "}");
            }
            await Task.Delay(5, ct);
        }
    }

    [Fact]
    public async Task FullLoop_LoginTicketStreamCancelAndUnknownEvent()
    {
        var sockets = new List<FakeRpcTransport>();
        using var auth = new DashboardAuth(Base, new AuthHandler());
        await auth.SignInAsync("basic", "user", "pass", default);

        await using var transport = new HermesDashboardTransport(auth, "gw1", (ticket, ct) =>
        {
            Assert.Equal("TICKET1", ticket);
            var sock = new FakeRpcTransport();
            sockets.Add(sock);
            sock.Push(FakeRpcTransport.ReadyFrame);
            return Task.FromResult<IRpcTransport>(sock);
        }, ownsAuth: false);

        using var answerCts = new CancellationTokenSource();
        var answerTask = Task.Run(async () =>
        {
            while (sockets.Count == 0) await Task.Delay(5);
            await AnswerLoop(sockets[0], (method, raw) => method switch
            {
                "client.capabilities" => """{"declines_not_shown":true}""",
                "session.create" => """{"session_id":"runtime-1","stored_session_id":"stored-1"}""",
                "prompt.submit" => """{"status":"streaming","user_row_id":1}""",
                "session.interrupt" => """{"ok":true}""",
                _ => null
            }, answerCts.Token);
        });

        var profiles = await transport.ListProfilesAsync(default);
        Assert.Equal(new ProfileKey("gw1", "default"), profiles[0].Key);

        var events = new List<ChatEvent>();
        var req = new ChatRequest(new ProfileKey("gw1", "default"), new[] { new ChatMessage("user", "hello") });

        using var streamCts = new CancellationTokenSource();
        var stream = Task.Run(async () =>
        {
            await foreach (var ev in transport.StreamChatAsync(req, streamCts.Token))
            {
                events.Add(ev);
                if (ev is SessionBound)
                {
                    sockets[0].Push(
                        """{"jsonrpc":"2.0","method":"event","params":{"type":"message.start","session_id":"runtime-1","payload":{}}}""");
                    sockets[0].Push(
                        """{"jsonrpc":"2.0","method":"event","params":{"type":"message.delta","session_id":"runtime-1","payload":{"text":"Hel"}}}""");
                    sockets[0].Push(
                        """{"jsonrpc":"2.0","method":"event","params":{"type":"weird.unknown","session_id":"runtime-1","payload":{}}}""");
                    sockets[0].Push(
                        """{"jsonrpc":"2.0","method":"event","params":{"type":"message.delta","session_id":"runtime-1","payload":{"text":"lo"}}}""");
                    sockets[0].Push(
                        """{"jsonrpc":"2.0","method":"event","params":{"type":"message.complete","session_id":"runtime-1","payload":{"text":"Hello"}}}""");
                }
            }
        });

        await stream.WaitAsync(TimeSpan.FromSeconds(5));
        answerCts.Cancel();
        try { await answerTask; } catch (OperationCanceledException) { }

        Assert.Contains(events, e => e is SessionBound sb && sb.SessionId == "runtime-1");
        Assert.Equal("Hello", string.Concat(events.OfType<TokenDelta>().Select(t => t.Text)));
        Assert.Contains(events, e => e is UnknownGatewayEvent u && u.Type == "weird.unknown");
        Assert.Contains(events, e => e is StreamCompleted);
        Assert.Equal(1, sockets[0].CountCalls("prompt.submit"));
        Assert.Equal(1, sockets[0].CountCalls("session.create"));
    }

    [Fact]
    public async Task ReconnectDoesNotDuplicatePromptSubmit()
    {
        var sockets = new List<FakeRpcTransport>();
        using var auth = new DashboardAuth(Base, new AuthHandler());
        await auth.SignInAsync("basic", "user", "pass", default);

        await using var transport = new HermesDashboardTransport(auth, "gw1", (ticket, ct) =>
        {
            var sock = new FakeRpcTransport();
            sockets.Add(sock);
            sock.Push(FakeRpcTransport.ReadyFrame);
            return Task.FromResult<IRpcTransport>(sock);
        }, ownsAuth: false);

        using var answerCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (sockets.Count == 0) await Task.Delay(5);
            await AnswerLoop(sockets[0], (method, _) => method switch
            {
                "client.capabilities" => "{}",
                "session.create" => """{"session_id":"s1"}""",
                "prompt.submit" => """{"status":"streaming"}""",
                _ => null
            }, answerCts.Token);
        });

        var policy = new ReconnectPolicy(3, TimeSpan.Zero, TimeSpan.Zero);
        var gotToken = false;
        var req = new ChatRequest(new ProfileKey("gw1", "default"), new[] { new ChatMessage("user", "x") });

        await policy.RunAsync(async ct =>
        {
            await foreach (var ev in transport.StreamChatAsync(req, ct))
            {
                if (ev is SessionBound)
                {
                    // Wait until prompt.submit has gone out once, then deliver a token (no second submit).
                    for (var i = 0; i < 100 && sockets.Sum(s => s.CountCalls("prompt.submit")) < 1; i++)
                        await Task.Delay(10, ct);
                    sockets[0].Push(
                        """{"jsonrpc":"2.0","method":"event","params":{"type":"message.delta","session_id":"s1","payload":{"text":"ok"}}}""");
                }
                if (ev is TokenDelta) { gotToken = true; return; }
            }
        }, (_, _) => Task.CompletedTask, default);

        answerCts.Cancel();
        Assert.True(gotToken);
        Assert.Equal(1, sockets.Sum(s => s.CountCalls("prompt.submit")));
    }

    [Fact]
    public async Task CancellationIssuesInterrupt()
    {
        var sockets = new List<FakeRpcTransport>();
        using var auth = new DashboardAuth(Base, new AuthHandler());
        await auth.SignInAsync("basic", "u", "p", default);
        await using var transport = new HermesDashboardTransport(auth, "gw1", (_, _) =>
        {
            var sock = new FakeRpcTransport();
            sockets.Add(sock);
            sock.Push(FakeRpcTransport.ReadyFrame);
            return Task.FromResult<IRpcTransport>(sock);
        }, ownsAuth: false);

        using var answerCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (sockets.Count == 0) await Task.Delay(5);
            await AnswerLoop(sockets[0], (method, _) => method switch
            {
                "client.capabilities" => "{}",
                "session.create" => """{"session_id":"s1"}""",
                "prompt.submit" => null, // hang: never reply; cancellation should fire interrupt
                "session.interrupt" => """{"ok":true}""",
                _ => null
            }, answerCts.Token);
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var req = new ChatRequest(new ProfileKey("gw1", "default"), new[] { new ChatMessage("user", "hi") });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in transport.StreamChatAsync(req, cts.Token)) { }
        });
        answerCts.Cancel();
        // Allow interrupt reply path to run
        await Task.Delay(100);
        Assert.True(sockets[0].CountCalls("session.interrupt") >= 1);
    }
}

public class NotificationSinkTests
{
    private static readonly ProfileKey A = new("g1", "a");
    private static readonly ProfileKey B = new("g1", "b");

    [Fact]
    public async Task SinkAcceptsOnlyGatedEvents_ColdStartDeselectUnknown()
    {
        var store = new MemoryStore();
        await new ProfileSelectionService(store).SetSelectedAsync(new[] { A });

        var s = new ProfileSelectionService(store);
        var gate = new NotificationGate(s);
        var sink = new NotificationSink(gate);

        Assert.False(sink.TryAccept(new GatedNotification(A, "t", "b"))); // cold start
        Assert.False(sink.TryAccept(new GatedNotification(null, "t", "b"))); // unattributed

        await s.LoadAsync();
        Assert.True(sink.TryAccept(new GatedNotification(A, "ok", "body", Surface.Toast)));
        Assert.False(sink.TryAccept(new GatedNotification(B, "other", "x")));

        await s.SetSelectedAsync(Array.Empty<ProfileKey>());
        Assert.False(sink.TryAccept(new GatedNotification(A, "after", "deselect")));

        Assert.Single(sink.Accepted);
        Assert.Equal(4, sink.Denied.Count); // cold A, unattributed, B, post-deselect A
    }

    [Fact]
    public async Task ProfileSwitchFiltersNotifications()
    {
        var s = new ProfileSelectionService(new MemoryStore());
        var sink = new NotificationSink(new NotificationGate(s));
        await s.SetSelectedAsync(new[] { A });
        Assert.True(sink.TryAccept(new GatedNotification(A, "a", "1")));
        await s.SetSelectedAsync(new[] { B });
        Assert.False(sink.TryAccept(new GatedNotification(A, "a", "2")));
        Assert.True(sink.TryAccept(new GatedNotification(B, "b", "3")));
    }
}

public class ProtocolEvidenceTests
{
    [Fact]
    public void PinsBookahHermesRevision()
    {
        Assert.Equal("5f00986b2a582e480592fd680131d501d57038ef", HermesDashboardTransport.ProtocolEvidenceRevision);
    }

    [Fact]
    public void InboxPluginContract_MatchesHeraldPushSource()
    {
        Assert.Equal("0.4.0", InboxApi.MinPluginVersion);
        Assert.Equal("0.4.1", InboxApi.AllJobsPluginVersion);
        Assert.Equal("herald-push", InboxApi.PluginCommand);
        Assert.Equal(4018, InboxApi.NotACommand);
        Assert.Contains("approval", NeedsYouService.Methods);
        Assert.Contains("secret", NeedsYouService.Methods);
    }
}
