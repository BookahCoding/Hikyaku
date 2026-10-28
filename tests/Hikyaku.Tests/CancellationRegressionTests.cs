using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Fixture regressions for cancel-while-waiting-for-events after prompt.submit has replied.
/// Distinguishes fixture coverage from live authenticated cancel (not available without user entry).
/// </summary>
public class CancellationRegressionTests
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

    private static async Task<(HermesDashboardTransport Transport, FakeRpcTransport Socket, CancellationTokenSource AnswerCts, DashboardAuth Auth)> ConnectAsync()
    {
        var auth = new DashboardAuth(Base, new AuthHandler());
        await auth.SignInAsync("basic", "u", "p", default);
        FakeRpcTransport? sock = null;
        var transport = new HermesDashboardTransport(auth, "gw1", (_, _) =>
        {
            sock = new FakeRpcTransport();
            sock.Push(FakeRpcTransport.ReadyFrame);
            return Task.FromResult<IRpcTransport>(sock);
        }, ownsAuth: true);
        // Force connect so the fake socket exists before AnswerLoop races.
        await transport.EnsureConnectedAsync(default);
        Assert.NotNull(sock);
        var answerCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            await AnswerLoop(sock!, (method, _) => method switch
            {
                "client.capabilities" => "{}",
                "session.create" => """{"session_id":"s-wait"}""",
                "prompt.submit" => """{"status":"streaming"}""",
                "session.interrupt" => """{"ok":true}""",
                _ => null
            }, answerCts.Token);
        });
        return (transport, sock!, answerCts, auth);
    }

    [Fact]
    public async Task CancelWhileWaitingForNextEvent_AfterSubmitReplied_SendsInterruptOnce()
    {
        var (transport, sock, answerCts, _) = await ConnectAsync();
        await using (transport)
        {
            var req = new ChatRequest(new ProfileKey("gw1", "default"),
                new[] { new ChatMessage("user", "hi") });
            using var cts = new CancellationTokenSource();
            var sawBound = false;
            var stream = Task.Run(async () =>
            {
                await foreach (var ev in transport.StreamChatAsync(req, cts.Token))
                {
                    if (ev is SessionBound)
                    {
                        sawBound = true;
                        // Wait until prompt.submit has been answered, then cancel while blocked on next event.
                        for (var i = 0; i < 200 && sock.CountCalls("prompt.submit") < 1; i++)
                            await Task.Delay(10);
                        await Task.Delay(80); // submit reply path finished; still waiting for message.* events
                        cts.Cancel();
                    }
                }
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await stream.WaitAsync(TimeSpan.FromSeconds(5)));
            await Task.Delay(150);
            Assert.True(sawBound);
            Assert.Equal(1, sock.CountCalls("prompt.submit"));
            Assert.Equal(1, sock.CountCalls("session.interrupt"));
        }
        answerCts.Cancel();
    }

    [Fact]
    public async Task LocalCancellationDuringSubmit_SendsInterrupt_NoDuplicatePrompt()
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

        using var answerCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (sock is null) await Task.Delay(5);
            await AnswerLoop(sock, (method, _) => method switch
            {
                "client.capabilities" => "{}",
                "session.create" => """{"session_id":"s-sub"}""",
                "prompt.submit" => null, // hang
                "session.interrupt" => """{"ok":true}""",
                _ => null
            }, answerCts.Token);
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var req = new ChatRequest(new ProfileKey("gw1", "default"), new[] { new ChatMessage("user", "x") });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in transport.StreamChatAsync(req, cts.Token)) { }
        });
        answerCts.Cancel();
        await Task.Delay(100);
        Assert.NotNull(sock);
        Assert.Equal(1, sock!.CountCalls("prompt.submit"));
        Assert.Equal(1, sock.CountCalls("session.interrupt"));
    }

    [Fact]
    public async Task TeardownDispose_DoesNotResendPrompt()
    {
        var (transport, sock, answerCts, _) = await ConnectAsync();
        var req = new ChatRequest(new ProfileKey("gw1", "default"), new[] { new ChatMessage("user", "bye") });
        using var cts = new CancellationTokenSource();
        // Keep MoveNext-ing so prompt.submit runs after SessionBound is yielded.
        var stream = Task.Run(async () =>
        {
            await foreach (var _ in transport.StreamChatAsync(req, cts.Token)) { }
        });
        for (var i = 0; i < 200 && sock.CountCalls("prompt.submit") < 1; i++)
            await Task.Delay(10);
        Assert.Equal(1, sock.CountCalls("prompt.submit"));
        await transport.DisposeAsync();
        try { await stream.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { /* dispose may fault the enumerator */ }
        answerCts.Cancel();
        Assert.Equal(1, sock.CountCalls("prompt.submit"));
    }

    [Fact]
    public async Task StaleEventsFromOtherSession_DoNotAppearInStream()
    {
        var (transport, sock, answerCts, _) = await ConnectAsync();
        await using (transport)
        {
            var req = new ChatRequest(new ProfileKey("gw1", "default"),
                new[] { new ChatMessage("user", "hi") });
            var deltas = new List<string>();
            await foreach (var ev in transport.StreamChatAsync(req, default))
            {
                if (ev is SessionBound)
                {
                    sock.Push("""{"jsonrpc":"2.0","method":"event","params":{"type":"message.delta","session_id":"other-session","payload":{"text":"STALE"}}}""");
                    sock.Push("""{"jsonrpc":"2.0","method":"event","params":{"type":"message.delta","session_id":"s-wait","payload":{"text":"ok"}}}""");
                    sock.Push("""{"jsonrpc":"2.0","method":"event","params":{"type":"message.complete","session_id":"s-wait","payload":{}}}""");
                }
                if (ev is TokenDelta d) deltas.Add(d.Text);
                if (ev is StreamCompleted) break;
            }
            Assert.Equal(new[] { "ok" }, deltas);
        }
        answerCts.Cancel();
    }

    [Fact]
    public async Task SessionIdFromOtherProfile_IsRejected()
    {
        var (transport, sock, answerCts, _) = await ConnectAsync();
        await using (transport)
        {
            // First bind a session under profile default
            var first = new ChatRequest(new ProfileKey("gw1", "default"),
                new[] { new ChatMessage("user", "a") });
            string? sessionId = null;
            await foreach (var ev in transport.StreamChatAsync(first, default))
            {
                if (ev is SessionBound s)
                {
                    sessionId = s.SessionId;
                    sock.Push("""{"jsonrpc":"2.0","method":"event","params":{"type":"message.complete","session_id":"s-wait","payload":{}}}""");
                }
                if (ev is StreamCompleted) break;
            }
            Assert.Equal("s-wait", sessionId);

            // Reuse that session id under a different profile — must not stream under the stolen id.
            var stolen = new ChatRequest(new ProfileKey("gw1", "other"),
                new[] { new ChatMessage("user", "b") }, SessionId: sessionId);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in transport.StreamChatAsync(stolen, default)) { }
            });
        }
        answerCts.Cancel();
    }

    [Fact]
    public async Task ReconnectPolicy_DoesNotSilentlyResendUncertainPrompt_AfterPartialOutput()
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
                "prompt.submit" => """{"status":"streaming"}""",
                _ => null
            }, answerCts.Token);
        });

        var policy = new ReconnectPolicy(3, TimeSpan.Zero, TimeSpan.Zero);
        var req = new ChatRequest(new ProfileKey("gw1", "default"), new[] { new ChatMessage("user", "x") });
        var received = false;
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await policy.RunAsync(async ct =>
            {
                if (received) throw new IOException("Stream interrupted after partial output.");
                await foreach (var ev in transport.StreamChatAsync(req, ct))
                {
                    if (ev is SessionBound)
                    {
                        for (var i = 0; i < 100 && sockets[0].CountCalls("prompt.submit") < 1; i++)
                            await Task.Delay(10, ct);
                        sockets[0].Push(
                            """{"jsonrpc":"2.0","method":"event","params":{"type":"message.delta","session_id":"s1","payload":{"text":"partial"}}}""");
                    }
                    if (ev is TokenDelta)
                    {
                        received = true;
                        throw new IOException("simulated drop after partial");
                    }
                }
            }, (_, _) => Task.CompletedTask, default);
        });
        answerCts.Cancel();
        Assert.True(received);
        Assert.Equal(1, sockets.Sum(s => s.CountCalls("prompt.submit")));
    }
}
