using System.Net;
using System.Text;
using System.Text.Json;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 16a — edit/regenerate via <c>prompt.submit</c> rewind (BookahHermes ChatSession.rewind @ 5f00986b):
/// <c>truncate_before_row_id</c> + <c>confirm_truncate</c> + <c>confirm_empty_truncate</c>, <c>user_row_id</c> in the
/// reply, busy 4009, stale 4018/4030/4004. Synthetic gateway only; no live chat.
/// </summary>
public class Checkpoint16RewindTests
{
    private static readonly Uri Base = new("https://gateway.example.invalid/");
    private static readonly ProfileKey Profile = new("gw1", "default");

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

    /// <summary>Answers each request; a body starting with "ERR:code:message" becomes a JSON-RPC error.</summary>
    internal static async Task<(HermesDashboardTransport T, FakeRpcTransport S, CancellationTokenSource Stop)> Start(
        Func<string, JsonDocument, string?> answer, bool completeTurn = true)
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
        await transport.EnsureConnectedAsync(default);
        var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            var seen = 0;
            while (!stop.IsCancellationRequested)
            {
                var sent = sock!.Sent;
                for (; seen < sent.Count; seen++)
                {
                    using var doc = JsonDocument.Parse(sent[seen]);
                    if (!doc.RootElement.TryGetProperty("method", out var m)) continue;
                    var method = m.GetString()!;
                    var id = doc.RootElement.GetProperty("id").GetString()!;
                    var body = method == "client.capabilities" ? "{}" : answer(method, doc);
                    if (body is null) continue;
                    if (body.StartsWith("ERR:"))
                    {
                        var parts = body.Split(':', 3);
                        sock.Push("{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"error\":{\"code\":" + parts[1] + ",\"message\":\"" + parts[2] + "\"}}");
                        continue;
                    }
                    sock.Push("{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":" + body + "}");
                    if (method == "prompt.submit" && completeTurn)
                        sock.Push("""{"jsonrpc":"2.0","method":"event","params":{"type":"message.complete","session_id":"s1","payload":{}}}""");
                }
                await Task.Delay(5, stop.Token).ContinueWith(_ => { });
            }
        });
        return (transport, sock!, stop);
    }

    private static ChatRequest Req(string text, long? rewind = null, string? session = "s1") =>
        new(Profile, new[] { new ChatMessage("user", text) }, session, RewindBeforeRowId: rewind);

    private static async Task<List<ChatEvent>> Run(HermesDashboardTransport t, ChatRequest r)
    {
        var events = new List<ChatEvent>();
        await foreach (var e in t.StreamChatAsync(r, default).WithCancellation(new CancellationTokenSource(5000).Token))
            events.Add(e);
        return events;
    }

    private static JsonElement SubmitParams(FakeRpcTransport s)
    {
        using var doc = JsonDocument.Parse(s.FirstCall("prompt.submit")!);
        return doc.RootElement.GetProperty("params").Clone();
    }

    [Fact]
    public async Task Rewind_sends_numeric_row_id_with_both_confirmations_and_reports_the_new_row()
    {
        var (t, s, stop) = await Start((m, _) => m == "prompt.submit" ? """{"status":"streaming","user_row_id":77}""" : null);
        await using var _t = t;
        var events = await Run(t, Req("edited text", rewind: 42));
        stop.Cancel();

        var p = SubmitParams(s);
        Assert.Equal("s1", p.GetProperty("session_id").GetString());
        Assert.Equal("edited text", p.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Number, p.GetProperty("truncate_before_row_id").ValueKind);
        Assert.Equal(42, p.GetProperty("truncate_before_row_id").GetInt64());
        Assert.True(p.GetProperty("confirm_truncate").GetBoolean());
        Assert.True(p.GetProperty("confirm_empty_truncate").GetBoolean());
        Assert.Equal(77, events.OfType<UserRowBound>().Single().RowId);
        Assert.Contains(events, e => e is StreamCompleted);
        Assert.Equal(1, s.CountCalls("prompt.submit"));
    }

    [Fact]
    public async Task Plain_send_never_carries_truncation_fields_but_still_reports_the_row_id()
    {
        var (t, s, stop) = await Start((m, _) => m == "prompt.submit" ? """{"status":"streaming","user_row_id":"9"}""" : null);
        await using var _t = t;
        var events = await Run(t, Req("hello"));
        stop.Cancel();
        var p = SubmitParams(s);
        Assert.False(p.TryGetProperty("truncate_before_row_id", out _));
        Assert.False(p.TryGetProperty("confirm_truncate", out _));
        Assert.False(p.TryGetProperty("confirm_empty_truncate", out _));
        Assert.Equal(9, events.OfType<UserRowBound>().Single().RowId); // numeric string accepted
    }

    [Fact]
    public async Task Missing_or_non_numeric_row_id_yields_no_binding()
    {
        var (t, _, stop) = await Start((m, _) => m == "prompt.submit" ? """{"status":"streaming","user_row_id":"abc"}""" : null);
        await using var _t = t;
        var events = await Run(t, Req("hello"));
        stop.Cancel();
        Assert.Empty(events.OfType<UserRowBound>());
    }

    [Fact]
    public async Task Rewind_without_an_attached_session_is_refused_before_any_prompt_is_sent()
    {
        var (t, s, stop) = await Start((m, _) => m == "prompt.submit" ? """{"status":"streaming"}""" : null);
        await using var _t = t;
        var events = await Run(t, Req("x", rewind: 5, session: null));
        stop.Cancel();
        var refused = events.OfType<RewindRefused>().Single();
        Assert.Equal(RewindRefusal.NoSession, refused.Reason);
        Assert.Equal(0, s.CountCalls("prompt.submit"));
        Assert.Equal(0, s.CountCalls("session.create"));
    }

    [Theory]
    [InlineData(4009, RewindRefusal.Busy)]
    [InlineData(4018, RewindRefusal.Stale)]
    [InlineData(4030, RewindRefusal.Stale)]
    [InlineData(4004, RewindRefusal.Stale)]
    public async Task Gateway_refusals_are_classified_and_not_retried(int code, RewindRefusal expected)
    {
        var (t, s, stop) = await Start((m, _) => m == "prompt.submit" ? $"ERR:{code}:nope" : null, completeTurn: false);
        await using var _t = t;
        var events = await Run(t, Req("x", rewind: 5));
        stop.Cancel();
        Assert.Equal(expected, events.OfType<RewindRefused>().Single().Reason);
        Assert.DoesNotContain(events, e => e is ProtocolError);
        Assert.Equal(1, s.CountCalls("prompt.submit")); // never auto-resubmitted
    }

    [Fact]
    public async Task Other_rewind_errors_stay_ordinary_protocol_errors()
    {
        var (t, _, stop) = await Start((m, _) => m == "prompt.submit" ? "ERR:5000:boom" : null, completeTurn: false);
        await using var _t = t;
        var events = await Run(t, Req("x", rewind: 5));
        stop.Cancel();
        Assert.Empty(events.OfType<RewindRefused>());
        Assert.Contains("boom", events.OfType<ProtocolError>().Single().Message);
    }

    [Fact]
    public async Task Busy_code_without_a_rewind_is_still_an_ordinary_protocol_error()
    {
        var (t, _, stop) = await Start((m, _) => m == "prompt.submit" ? "ERR:4009:busy" : null, completeTurn: false);
        await using var _t = t;
        var events = await Run(t, Req("x"));
        stop.Cancel();
        Assert.Empty(events.OfType<RewindRefused>());
        Assert.Single(events.OfType<ProtocolError>());
    }

    [Fact]
    public async Task Cross_profile_rewind_is_refused_by_the_existing_owner_check()
    {
        var (t, s, stop) = await Start((m, _) => m == "prompt.submit" ? """{"status":"streaming"}""" : null);
        await using var _t = t;
        await Run(t, Req("first")); // binds s1 to the default profile
        var other = new ChatRequest(new ProfileKey("gw1", "other"), new[] { new ChatMessage("user", "x") }, "s1", RewindBeforeRowId: 3);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(t, other));
        stop.Cancel();
        Assert.Equal(1, s.CountCalls("prompt.submit"));
    }
}
