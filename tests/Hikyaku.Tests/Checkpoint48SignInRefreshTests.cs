using System.Net;
using System.Text;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// 0.5.2 live finding: after the 0.5.1 update the maintainer had to sign in with the browser again. The app talks to the
/// gateway through several connections (chat, Inbox, Needs you, settings) that share one saved browser sign-in; each did its
/// own one-at-a-time refresh, so after a restart with an expired access token they refreshed at the same moment with the same
/// one-use refresh token. The gateway accepted the first and refused the rest, and a refusal forgot the saved sign-in —
/// including the one the first refresh had just saved.
/// </summary>
public class Checkpoint48SignInRefreshTests
{
    private static readonly Uri Gateway = new("https://hermes.example/hermes/");

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _items = new();
        public string? Get(string name) { lock (_items) return _items.TryGetValue(name, out var v) ? v : null; }
        public void Set(string name, string value) { lock (_items) _items[name] = value; }
        public void Remove(string name) { lock (_items) _items.Remove(name); }
    }

    /// <summary>A gateway whose refresh tokens work once (rotation): RT1 → AT2/RT2, RT1 again → 400.</summary>
    private sealed class RotatingGateway : HttpMessageHandler
    {
        private readonly HashSet<string> _used = new();
        public int Refreshes;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/auth/native/refresh"))
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                Interlocked.Increment(ref Refreshes);
                await Task.Delay(50, ct);                                   // both callers are in flight together
                bool first;
                lock (_used) first = _used.Add(body.Contains("RT1") ? "RT1" : body);
                return first && body.Contains("RT1")
                    ? Json(HttpStatusCode.OK, """{"access_token":"AT2","refresh_token":"RT2","expires_at":1900000001,"provider":"p","user_id":"u"}""")
                    : Json(HttpStatusCode.BadRequest, """{"detail":"refresh token already used"}""");
            }
            return request.Headers.Authorization?.Parameter == "AT2"
                ? Json(HttpStatusCode.OK, "{}")
                : Json(HttpStatusCode.Unauthorized, """{"detail":"expired"}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
            new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task Connections_sharing_a_sign_in_refresh_it_once_and_nobody_is_signed_out()
    {
        var tokens = new NativeTokenStore(new MemorySecrets());
        tokens.Set(Gateway, new NativeSession("AT1", "RT1", 0, "p", "u", ""));
        var gateway = new RotatingGateway();
        using var chat = new DashboardAuth(Gateway, gateway, tokens);
        using var inbox = new DashboardAuth(Gateway, gateway, tokens);
        using var needsYou = new DashboardAuth(Gateway, gateway, tokens);

        var results = await Task.WhenAll(new[] { chat, inbox, needsYou }.Select(async auth =>
        {
            using var r = await auth.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(Gateway, "api/profiles")));
            return r.StatusCode;
        }));

        Assert.All(results, s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(1, gateway.Refreshes);
        Assert.Equal("AT2", tokens.Get(Gateway)?.AccessToken);              // still signed in
    }

    [Fact]
    public async Task A_refusal_of_an_older_refresh_token_does_not_forget_a_newer_sign_in()
    {
        var tokens = new NativeTokenStore(new MemorySecrets());
        tokens.Set(Gateway, new NativeSession("AT1", "RT1", 0, "p", "u", ""));
        var handler = new DelegateHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/auth/native/refresh"))
            {
                // While this refresh is in flight, another app instance on the PC signs in again.
                tokens.Set(Gateway, new NativeSession("AT9", "RT9", 0, "p", "u", ""));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
        });
        using var auth = new DashboardAuth(Gateway, handler, tokens);
        using var r = await auth.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(Gateway, "api/profiles")));
        Assert.Equal("AT9", tokens.Get(Gateway)?.AccessToken);              // only RT1's sign-in was refused
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => impl(request, ct);
    }
}
