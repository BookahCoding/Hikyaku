using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 15b — native browser sign-in (RFC 8252 + PKCE S256) against the contract in BookahHermes
/// BrowserSignIn.kt / AuthApi.kt / NativeBearer.kt @ 5f00986b. Synthetic gateway only; no live sign-in.
/// </summary>
public class Checkpoint15PkceTests
{
    private static readonly Uri Gateway = new("https://hermes.example/hermes/");

    // ---- helpers -------------------------------------------------------------------------------

    private sealed class MemorySecrets : ISecretStore
    {
        public readonly Dictionary<string, string> Items = new();
        public string? Get(string name) => Items.TryGetValue(name, out var v) ? v : null;
        public void Set(string name, string value) => Items[name] = value;
        public void Remove(string name) => Items.Remove(name);
    }

    private sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> impl) : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string? Bearer, string Body)> Calls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Calls) Calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString(), body));
            return await impl(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string TokenJson =
        """{"access_token":"AT1","refresh_token":"RT1","token_type":"Bearer","expires_at":1900000000,"provider":"self-hosted","user_id":"u1"}""";

    private sealed class FakeLoopback(Func<Uri, CancellationToken, Task<Dictionary<string, string>>[]> script) : ILoopbackReceiver
    {
        public readonly Func<Uri, CancellationToken, Task<Dictionary<string, string>>[]> Script = script;
        public bool Disposed;
        public Uri? Opened;
        public int Index;
        public Task<ILoopbackListener> StartAsync(CancellationToken ct) => Task.FromResult<ILoopbackListener>(new L(this));
        private sealed class L(FakeLoopback o) : ILoopbackListener
        {
            public string RedirectUri => "http://127.0.0.1:43210/callback";
            public async Task<IReadOnlyDictionary<string, string>> AwaitCallbackAsync(CancellationToken ct)
            {
                var steps = o.Script(o.Opened!, ct);
                return await steps[o.Index++];
            }
            public void Dispose() => o.Disposed = true;
        }
    }

    private static Task<Dictionary<string, string>> Cb(params (string, string)[] kv) =>
        Task.FromResult(kv.ToDictionary(p => p.Item1, p => p.Item2));

    private static string Q(Uri u, string key)
    {
        foreach (var part in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            if (Uri.UnescapeDataString(part[..i]) == key) return Uri.UnescapeDataString(part[(i + 1)..]);
        }
        return "";
    }

    private static (DashboardAuth Auth, NativeTokenStore Tokens, Stub Http, MemorySecrets Secrets) Make(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> impl, Func<DateTimeOffset>? clock = null)
    {
        var secrets = new MemorySecrets();
        var tokens = new NativeTokenStore(secrets);
        var http = new Stub(impl);
        return (new DashboardAuth(Gateway, http, tokens, clock), tokens, http, secrets);
    }

    // ---- PKCE primitives -----------------------------------------------------------------------

    [Fact]
    public void Pkce_challenge_matches_rfc7636_appendix_b_vector()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void Pkce_verifier_is_43_url_safe_chars_and_unique()
    {
        var a = Pkce.NewVerifier();
        var b = Pkce.NewVerifier();
        Assert.Equal(43, a.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", a);
        Assert.NotEqual(a, b);
        Assert.Matches("^[A-Za-z0-9_-]{22}$", Pkce.NewState());
    }

    [Fact]
    public void NativeSession_to_string_never_prints_tokens()
    {
        var s = new NativeSession("SECRET-AT", "SECRET-RT", 1, "p", "u", Gateway.ToString());
        Assert.DoesNotContain("SECRET", s.ToString());
    }

    // ---- sign-in flow --------------------------------------------------------------------------

    [Fact]
    public async Task SignIn_opens_authorize_url_and_redeems_code_with_matching_verifier()
    {
        var (auth, tokens, http, secrets) = Make(_ => Task.FromResult(Json(HttpStatusCode.OK, TokenJson)));
        FakeLoopback? lb = null;
        lb = new FakeLoopback((opened, _) => new[] { Cb(("code", "GW-CODE"), ("state", Q(opened, "state"))) });
        Uri? opened = null;
        await new NativeSignIn(auth, lb).SignInAsync(null, u => { opened = u; lb.Opened = u; }, default);

        Assert.Equal("/hermes/auth/native/authorize", opened!.AbsolutePath);
        Assert.Equal("hermes.example", opened.Host);
        Assert.Equal("S256", Q(opened, "code_challenge_method"));
        Assert.Equal("http://127.0.0.1:43210/callback", Q(opened, "redirect_uri"));
        Assert.Equal("", Q(opened, "provider")); // blank provider: gateway picks / shows chooser

        var call = Assert.Single(http.Calls);
        Assert.Equal("/hermes/auth/native/token", call.Path);
        Assert.Null(call.Bearer);
        using var body = JsonDocument.Parse(call.Body);
        Assert.Equal("GW-CODE", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(Q(opened, "code_challenge"), Pkce.Challenge(body.RootElement.GetProperty("code_verifier").GetString()!));

        var session = tokens.Get(Gateway)!;
        Assert.Equal("AT1", session.AccessToken);
        Assert.Equal("RT1", session.RefreshToken);
        Assert.True(lb.Disposed);
        Assert.Single(secrets.Items); // one protected entry, nothing else persisted
    }

    [Fact]
    public async Task SignIn_ignores_callbacks_with_wrong_or_missing_state_or_code()
    {
        var (auth, tokens, http, _) = Make(_ => Task.FromResult(Json(HttpStatusCode.OK, TokenJson)));
        FakeLoopback? lb = null;
        lb = new FakeLoopback((o, _) => new[]
        {
            Cb(("code", "EVIL"), ("state", "attacker")),
            Cb(("state", Q(o, "state"))),                    // no code
            Cb(("code", ""), ("state", Q(o, "state"))),      // empty code
            Cb(("code", "GOOD"), ("state", Q(o, "state"))),
        });
        await new NativeSignIn(auth, lb).SignInAsync("self-hosted", u => lb.Opened = u, default);
        Assert.Contains("\"code\":\"GOOD\"", Assert.Single(http.Calls).Body);
        Assert.Equal("self-hosted", Q(lb.Opened!, "provider"));
    }

    [Fact]
    public async Task SignIn_error_callback_with_matching_state_fails_without_token_call()
    {
        var (auth, tokens, http, _) = Make(_ => Task.FromResult(Json(HttpStatusCode.OK, TokenJson)));
        FakeLoopback? lb = null;
        lb = new FakeLoopback((o, _) => new[]
        {
            Cb(("error", "access_denied"), ("state", "attacker")), // wrong state: ignored
            Cb(("error", "access_denied"), ("state", Q(o, "state"))),
        });
        var ex = await Assert.ThrowsAsync<AuthFailedException>(() =>
            new NativeSignIn(auth, lb).SignInAsync(null, u => lb.Opened = u, default));
        Assert.Equal("denied", ex.Kind);
        Assert.Empty(http.Calls);
        Assert.Null(tokens.Get(Gateway));
        Assert.True(lb.Disposed);
    }

    [Fact]
    public async Task SignIn_times_out_and_releases_listener()
    {
        var (auth, tokens, http, _) = Make(_ => Task.FromResult(Json(HttpStatusCode.OK, TokenJson)));
        var lb = new FakeLoopback((_, ct) => new[] { Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => new Dictionary<string, string>(), ct) });
        var ex = await Assert.ThrowsAsync<AuthFailedException>(() =>
            new NativeSignIn(auth, lb, timeout: TimeSpan.FromMilliseconds(50)).SignInAsync(null, _ => { }, default));
        Assert.Equal("timeout", ex.Kind);
        Assert.True(lb.Disposed);
        Assert.Empty(http.Calls);
    }

    [Fact]
    public async Task SignIn_cancellation_stops_without_token_call_and_releases_listener()
    {
        var (auth, tokens, http, _) = Make(_ => Task.FromResult(Json(HttpStatusCode.OK, TokenJson)));
        using var cts = new CancellationTokenSource();
        var lb = new FakeLoopback((_, ct) => new[] { Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => new Dictionary<string, string>(), ct) });
        var task = new NativeSignIn(auth, lb).SignInAsync(null, _ => cts.Cancel(), cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(lb.Disposed);
        Assert.Empty(http.Calls);
        Assert.Null(tokens.Get(Gateway));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{}")]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    [InlineData(HttpStatusCode.OK, """{"access_token":"A"}""")]                      // no refresh token
    [InlineData(HttpStatusCode.OK, """{"access_token":"","refresh_token":"R"}""")]   // empty access token
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task Failed_or_malformed_token_exchange_stores_nothing(HttpStatusCode code, string body)
    {
        var (auth, tokens, _, secrets) = Make(_ => Task.FromResult(Json(code, body)));
        FakeLoopback? lb = null;
        lb = new FakeLoopback((o, _) => new[] { Cb(("code", "c"), ("state", Q(o, "state"))) });
        await Assert.ThrowsAsync<AuthFailedException>(() =>
            new NativeSignIn(auth, lb).SignInAsync(null, u => lb.Opened = u, default));
        Assert.Null(tokens.Get(Gateway));
        Assert.Empty(secrets.Items);
        Assert.True(lb.Disposed);
    }

    [Fact]
    public async Task Oversized_token_response_is_rejected()
    {
        var (auth, tokens, _, _) = Make(_ => Task.FromResult(Json(HttpStatusCode.OK,
            "{\"access_token\":\"" + new string('a', 300_000) + "\",\"refresh_token\":\"r\"}")));
        FakeLoopback? lb = null;
        lb = new FakeLoopback((o, _) => new[] { Cb(("code", "c"), ("state", Q(o, "state"))) });
        await Assert.ThrowsAsync<AuthFailedException>(() =>
            new NativeSignIn(auth, lb).SignInAsync(null, u => lb.Opened = u, default));
        Assert.Null(tokens.Get(Gateway));
    }

    // ---- bearer attach -------------------------------------------------------------------------

    [Fact]
    public async Task Bearer_is_sent_on_api_routes_only_and_never_to_other_origins()
    {
        var (auth, tokens, http, _) = Make(_ => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        tokens.Set(Gateway, new NativeSession("AT", "RT", 0, "p", "u", ""));

        using (await auth.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(Gateway, "api/auth/me")))) { }
        using (await auth.SendAsync(new HttpRequestMessage(HttpMethod.Post, new Uri(Gateway, "auth/logout")))) { }
        using (await auth.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri("https://evil.example/hermes/api/x")))) { }
        using (await auth.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri("https://hermes.example/other/api/x")))) { }

        Assert.Equal("Bearer AT", http.Calls[0].Bearer);
        Assert.Null(http.Calls[1].Bearer);
        Assert.Null(http.Calls[2].Bearer);
        Assert.Null(http.Calls[3].Bearer);
    }

    [Fact]
    public async Task No_session_means_no_authorization_header()
    {
        var (auth, _, http, _) = Make(_ => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        using (await auth.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(Gateway, "api/auth/me")))) { }
        Assert.Null(http.Calls[0].Bearer);
    }

    [Fact]
    public async Task Concurrent_401s_trigger_exactly_one_refresh_and_all_retry_with_new_token()
    {
        var refreshes = 0;
        var (auth, tokens, http, _) = Make(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/native/refresh"))
            {
                Interlocked.Increment(ref refreshes);
                return Task.FromResult(Json(HttpStatusCode.OK,
                    """{"access_token":"AT2","refresh_token":"RT2","expires_at":1900000001,"provider":"p","user_id":"u"}"""));
            }
            return Task.FromResult(req.Headers.Authorization?.Parameter == "AT2"
                ? Json(HttpStatusCode.OK, "{}")
                : Json(HttpStatusCode.Unauthorized, """{"detail":"expired"}"""));
        });
        tokens.Set(Gateway, new NativeSession("AT1", "RT1", 0, "p", "u", ""));

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var r = await auth.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(Gateway, "api/profiles")));
            return r.StatusCode;
        }));
        Assert.All(results, s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(1, refreshes);
        Assert.Equal("AT2", tokens.Get(Gateway)!.AccessToken);
        var refreshCall = http.Calls.Single(c => c.Path.EndsWith("/auth/native/refresh"));
        Assert.Null(refreshCall.Bearer);
        using var body = JsonDocument.Parse(refreshCall.Body);
        Assert.Equal("RT1", body.RootElement.GetProperty("refresh_token").GetString());
        Assert.Equal("p", body.RootElement.GetProperty("provider").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Rejected_refresh_forgets_tokens_and_surfaces_original_401(HttpStatusCode refreshStatus)
    {
        var (auth, tokens, _, secrets) = Make(req =>
            Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("/auth/native/refresh")
                ? Json(refreshStatus, """{"detail":"session_expired"}""")
                : Json(HttpStatusCode.Unauthorized, "{}")));
        tokens.Set(Gateway, new NativeSession("AT1", "RT1", 0, "p", "u", ""));
        using var r = await auth.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(Gateway, "api/profiles")));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Null(tokens.Get(Gateway));
        Assert.Empty(secrets.Items);
    }

    [Fact]
    public async Task Unreachable_refresh_keeps_tokens_and_backs_off_before_retrying()
    {
        var now = DateTimeOffset.Parse("2026-10-26T12:00:00Z");
        var refreshes = 0;
        var (auth, tokens, _, _) = Make(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/auth/native/refresh"))
            {
                refreshes++;
                return Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "{}"));
            }
            return Task.FromResult(Json(HttpStatusCode.Unauthorized, "{}"));
        }, () => now);
        tokens.Set(Gateway, new NativeSession("AT1", "RT1", 0, "p", "u", ""));

        var ex = await Assert.ThrowsAsync<AuthFailedException>(() =>
            auth.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(Gateway, "api/profiles"))));
        Assert.Equal("unavailable", ex.Kind);
        Assert.NotNull(tokens.Get(Gateway)); // not signed out

        now = now.AddSeconds(5);
        await Assert.ThrowsAsync<AuthFailedException>(() =>
            auth.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(Gateway, "api/profiles"))));
        Assert.Equal(1, refreshes); // inside the 30 s window: no second refresh storm

        now = now.AddSeconds(31);
        await Assert.ThrowsAsync<AuthFailedException>(() =>
            auth.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(Gateway, "api/profiles"))));
        Assert.Equal(2, refreshes);
    }

    // ---- session lifecycle ---------------------------------------------------------------------

    [Fact]
    public async Task Password_sign_in_forgets_native_tokens()
    {
        var (auth, tokens, _, secrets) = Make(_ => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        tokens.Set(Gateway, new NativeSession("AT", "RT", 0, "p", "u", ""));
        await auth.SignInAsync("basic", "u", "pw", default);
        Assert.Null(tokens.Get(Gateway));
        Assert.Empty(secrets.Items);
    }

    [Fact]
    public async Task Sign_out_forgets_tokens_locally_even_when_offline()
    {
        var (auth, tokens, _, secrets) = Make(_ => throw new HttpRequestException("offline"));
        tokens.Set(Gateway, new NativeSession("AT", "RT", 0, "p", "u", ""));
        await auth.SignOutAsync(default);
        Assert.Null(tokens.Get(Gateway));
        Assert.Empty(secrets.Items);
    }

    [Fact]
    public async Task Sign_out_forgets_tokens_locally_even_when_the_gateway_times_out()
    {
        // An HttpClient timeout surfaces as TaskCanceledException; the Settings button awaits this from an async void handler.
        var (auth, tokens, _, secrets) = Make(_ => throw new TaskCanceledException("timed out"));
        tokens.Set(Gateway, new NativeSession("AT", "RT", 0, "p", "u", ""));
        await auth.SignOutAsync(default);
        Assert.Null(tokens.Get(Gateway));
        Assert.Empty(secrets.Items);
    }

    [Fact]
    public void Token_store_is_keyed_per_gateway_and_survives_reload()
    {
        var secrets = new MemorySecrets();
        var a = new Uri("https://a.example/");
        var b = new Uri("https://b.example/x/");
        var store = new NativeTokenStore(secrets);
        store.Set(a, new NativeSession("A-AT", "A-RT", 0, null, null, ""));
        store.Set(b, new NativeSession("B-AT", "B-RT", 0, null, null, ""));
        var reloaded = new NativeTokenStore(secrets);
        Assert.Equal("A-AT", reloaded.Get(a)!.AccessToken);
        Assert.Equal("B-AT", reloaded.Get(b)!.AccessToken);
        Assert.Null(reloaded.Get(new Uri("https://c.example/")));
        Assert.DoesNotContain(secrets.Items.Keys, k => k.Contains("AT"));
    }

    [Fact]
    public void Token_store_ignores_corrupt_entry()
    {
        var secrets = new MemorySecrets();
        var store = new NativeTokenStore(secrets);
        store.Set(Gateway, new NativeSession("AT", "RT", 0, null, null, ""));
        foreach (var k in secrets.Items.Keys.ToList()) secrets.Items[k] = "{corrupt";
        Assert.Null(new NativeTokenStore(secrets).Get(Gateway));
    }

    // ---- capability probe ----------------------------------------------------------------------

    [Theory]
    [InlineData("""{"auth_required":true,"auth_flows":["password","native_pkce"]}""", true)]
    [InlineData("""{"auth_required":true,"auth_flows":["password"]}""", false)]
    [InlineData("""{"auth_required":true}""", false)]
    [InlineData("not json", false)]
    public async Task Status_probe_reports_native_pkce_only_when_advertised(string body, bool expected)
    {
        var (auth, _, http, _) = Make(_ => Task.FromResult(Json(HttpStatusCode.OK, body)));
        Assert.Equal(expected, await auth.SupportsNativeSignInAsync(default));
        Assert.Equal("/hermes/api/status", http.Calls[0].Path);
        Assert.Null(http.Calls[0].Bearer);
    }

    [Fact]
    public async Task Status_probe_failure_is_not_supported_rather_than_throwing()
    {
        var (auth, _, _, _) = Make(_ => Task.FromResult(Json(HttpStatusCode.InternalServerError, "{}")));
        Assert.False(await auth.SupportsNativeSignInAsync(default));
    }

    // ---- browser launch policy -----------------------------------------------------------------

    [Theory]
    [InlineData("https://hermes.example/hermes/auth/native/authorize?x=1", true)]
    [InlineData("https://evil.example/hermes/auth/native/authorize", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://hermes.example:8443/hermes/auth/native/authorize", false)]
    [InlineData("https://hermes.example/hermes/api/other", false)]
    public void Browser_launch_policy_allows_only_the_gateway_authorize_url(string url, bool expected)
    {
        Assert.Equal(expected, BrowserLaunchPolicy.IsAllowed(new Uri(url), Gateway));
    }

    // ---- real loopback listener ----------------------------------------------------------------

    [Fact]
    public async Task Loopback_listener_binds_127_0_0_1_delivers_matching_callback_and_releases_port()
    {
        using var http = new HttpClient();
        int port;
        string redirect;
        using (var listener = await new HttpListenerLoopbackReceiver().StartAsync(default))
        {
            redirect = listener.RedirectUri;
            var u = new Uri(redirect);
            port = u.Port;
            Assert.Equal("127.0.0.1", u.Host);
            Assert.Equal("/callback", u.AbsolutePath);

            var wait = listener.AwaitCallbackAsync(default);
            // Wrong path / method are answered but never delivered.
            using (var r404 = await http.GetAsync($"http://127.0.0.1:{port}/other?code=x&state=y"))
                Assert.Equal(HttpStatusCode.NotFound, r404.StatusCode);
            using (var rPost = await http.PostAsync($"http://127.0.0.1:{port}/callback?code=x&state=y", new StringContent("")))
                Assert.Equal(HttpStatusCode.MethodNotAllowed, rPost.StatusCode);
            Assert.False(wait.IsCompleted);

            using (var ok = await http.GetAsync($"{redirect}?code=C0DE&state=S%2F1"))
            {
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
                Assert.Contains("close", await ok.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("C0DE", await ok.Content.ReadAsStringAsync());
            }
            var q = await wait.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("C0DE", q["code"]);
            Assert.Equal("S/1", q["state"]);
        }
        // Port is free again after dispose.
        using var rebind = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
        rebind.Start();
    }

    [Fact]
    public async Task Loopback_listener_rejects_foreign_host_header_and_oversized_query()
    {
        using var listener = await new HttpListenerLoopbackReceiver().StartAsync(default);
        var port = new Uri(listener.RedirectUri).Port;
        var wait = listener.AwaitCallbackAsync(default);

        using var raw = new HttpClient();
        using var rebinding = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/callback?code=x&state=y");
        rebinding.Headers.Host = "evil.example";
        using (var resp = await raw.SendAsync(rebinding))
            Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
        using (var big = await raw.GetAsync($"http://127.0.0.1:{port}/callback?code={new string('a', 9000)}&state=y"))
            Assert.NotEqual(HttpStatusCode.OK, big.StatusCode);
        Assert.False(wait.IsCompleted);
    }

    [Fact]
    public async Task Loopback_await_is_cancellable_and_dispose_is_idempotent()
    {
        var listener = await new HttpListenerLoopbackReceiver().StartAsync(default);
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.AwaitCallbackAsync(cts.Token));
        listener.Dispose();
        listener.Dispose();
    }
}
