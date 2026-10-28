using System.Net;
using System.Text;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 21 — Cloudflare Access service token (BookahHermes AccessTokens.kt @ 5f00986b): CF-Access-Client-Id / -Secret on every
/// request to the token's host over https only, never to another host (including via redirects), Access blocks detected as a typed
/// error, tokens validated and stored per host in the protected store. Synthetic hosts and handlers only.
/// </summary>
public class Checkpoint21AccessTests
{
    private static readonly Uri Gw = new("https://hermes.example/hermes/");
    private const string Id = "abc123.access";
    private static readonly string Secret = Fixture("value-0123456789abcdef");   // synthetic, built so the repository scan stays strict
    private static string Fixture(string tail) => "fx-" + tail;

    private sealed class MemorySecrets : ISecretStore
    {
        public readonly Dictionary<string, string> Items = new();
        public string? Get(string name) => Items.TryGetValue(name, out var v) ? v : null;
        public void Set(string name, string value) => Items[name] = value;
        public void Remove(string name) => Items.Remove(name);
    }

    private sealed class Net(Func<HttpRequestMessage, HttpResponseMessage> impl) : HttpMessageHandler
    {
        public readonly List<(string Method, Uri Url, string? Id, string? Secret, string? Auth)> Seen = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            lock (Seen) Seen.Add((r.Method.Method, r.RequestUri!,
                r.Headers.TryGetValues("CF-Access-Client-Id", out var i) ? i.Single() : null,
                r.Headers.TryGetValues("CF-Access-Client-Secret", out var s) ? s.Single() : null,
                r.Headers.Authorization?.ToString()));
            return Task.FromResult(impl(r));
        }
    }

    private static HttpResponseMessage Ok(string json = "{}") =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Redirect(string to, HttpStatusCode code = HttpStatusCode.Found) =>
        new(code) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

    private static HttpResponseMessage CloudflareHtml403()
    {
        var r = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<html>Access denied</html>", Encoding.UTF8, "text/html") };
        r.Headers.TryAddWithoutValidation("Server", "cloudflare");
        return r;
    }

    private static (DashboardAuth Auth, AccessTokenStore Store, MemorySecrets Secrets, Net Net) Make(
        Func<HttpRequestMessage, HttpResponseMessage>? impl = null, bool withToken = true, Uri? gateway = null)
    {
        var secrets = new MemorySecrets();
        var store = new AccessTokenStore(secrets);
        if (withToken) store.Set((gateway ?? Gw).Host, new AccessToken(Id, Secret));
        var net = new Net(impl ?? (_ => Ok()));
        return (new DashboardAuth(gateway ?? Gw, net, access: store), store, secrets, net);
    }

    private static async Task Get(DashboardAuth a, string url)
    {
        using var r = await a.SendAsync(new HttpRequestMessage(HttpMethod.Get, url));
    }

    // ---- headers: where they go ------------------------------------------------------------------

    [Fact]
    public async Task The_token_headers_go_on_every_request_to_the_gateway_host_over_https()
    {
        var (auth, _, _, net) = Make();
        await Get(auth, "https://hermes.example/hermes/api/status");
        await Get(auth, "https://hermes.example/hermes/auth/password-login");
        Assert.All(net.Seen, s => { Assert.Equal(Id, s.Id); Assert.Equal(Secret, s.Secret); });
    }

    [Fact]
    public async Task The_token_is_never_sent_over_plain_http_or_to_another_host_or_port_variant_of_it()
    {
        var (auth, _, _, net) = Make();
        await Get(auth, "http://hermes.example/hermes/api/status");              // same host, plain http: secret must not cross the wire
        await Get(auth, "https://evil.example/hermes/api/status");               // another host
        await Get(auth, "https://hermes.example.evil.example/api/status");       // look-alike host
        Assert.All(net.Seen, s => { Assert.Null(s.Id); Assert.Null(s.Secret); });
    }

    [Fact]
    public async Task Host_matching_ignores_case_and_a_trailing_dot_like_Android()
    {
        var (auth, store, _, net) = Make(withToken: false);
        store.Set("HERMES.Example.", new AccessToken(Id, Secret));
        await Get(auth, "https://hermes.example/hermes/api/status");
        Assert.Equal(Id, net.Seen.Single().Id);
        Assert.Equal("hermes.example", AccessHost.Key("HERMES.Example."));
        Assert.Equal("::1", AccessHost.Key("[::1]"));
    }

    [Fact]
    public async Task Without_a_token_nothing_is_added()
    {
        var (auth, _, _, net) = Make(withToken: false);
        await Get(auth, "https://hermes.example/hermes/api/status");
        Assert.Null(net.Seen.Single().Id);
    }

    [Fact]
    public async Task A_caller_supplied_access_header_is_replaced_not_trusted()
    {
        var (auth, _, _, net) = Make();
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/x");
        req.Headers.TryAddWithoutValidation("CF-Access-Client-Secret", "caller-chosen");
        using var _ = await auth.SendAsync(req);
        Assert.Null(net.Seen.Single().Secret);                                    // not forwarded to a host that has no token
    }

    [Fact]
    public async Task Both_the_access_token_and_a_browser_sign_in_bearer_can_ride_the_same_request()
    {
        var secrets = new MemorySecrets();
        var access = new AccessTokenStore(secrets);
        access.Set(Gw.Host, new AccessToken(Id, Secret));
        var tokens = new NativeTokenStore(secrets);
        tokens.Set(Gw, new NativeSession("AT", "RT", 0, "p", "u", ""));
        var net = new Net(_ => Ok());
        var auth = new DashboardAuth(Gw, net, tokens, access: access);
        await Get(auth, "https://hermes.example/hermes/api/auth/me");
        var s = net.Seen.Single();
        Assert.Equal("Bearer AT", s.Auth);
        Assert.Equal(Id, s.Id);
    }

    // ---- redirects -------------------------------------------------------------------------------

    [Fact]
    public async Task A_same_site_get_redirect_is_followed_and_keeps_the_headers()
    {
        var (auth, _, _, net) = Make(r => r.RequestUri!.AbsolutePath.EndsWith("/status") ? Redirect("/hermes/api/status/") : Ok());
        await Get(auth, "https://hermes.example/hermes/api/status");
        Assert.Equal(2, net.Seen.Count);
        Assert.All(net.Seen, s => Assert.Equal(Id, s.Id));
    }

    [Fact]
    public async Task An_http_to_https_upgrade_on_the_same_host_is_followed_and_only_the_https_hop_carries_the_token()
    {
        var (auth, _, _, net) = Make(r => r.RequestUri!.Scheme == "http" ? Redirect("https://hermes.example/hermes/api/status") : Ok());
        await Get(auth, "http://hermes.example/hermes/api/status");
        Assert.Equal(2, net.Seen.Count);
        Assert.Null(net.Seen[0].Id);
        Assert.Equal(Id, net.Seen[1].Id);
    }

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("https://hermes.example.evil.example/steal")]
    [InlineData("https://hermes.example:8443/other")]
    public async Task A_redirect_to_another_host_or_port_is_not_followed_so_the_secret_cannot_follow_it(string target)
    {
        var (auth, _, _, net) = Make(_ => Redirect(target));
        using var resp = await auth.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://hermes.example/hermes/api/status"));
        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);                      // handed back to the caller, not chased
        Assert.Single(net.Seen);
        Assert.DoesNotContain(net.Seen, s => s.Url.Host.Contains("evil"));
    }

    [Fact]
    public async Task Post_redirects_are_not_followed_and_redirect_loops_stop()
    {
        var (auth, _, _, net) = Make(_ => Redirect("/hermes/again"));
        using (var post = await auth.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://hermes.example/hermes/auth/password-login")
        { Content = new StringContent("x") }))
            Assert.Equal(HttpStatusCode.Found, post.StatusCode);
        Assert.Single(net.Seen);

        net.Seen.Clear();
        using var loop = await auth.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://hermes.example/hermes/loop"));
        Assert.Equal(HttpStatusCode.Found, loop.StatusCode);
        Assert.True(net.Seen.Count <= 6);                                         // bounded, not endless
    }

    // ---- Access blocks ---------------------------------------------------------------------------

    [Theory]
    [InlineData("https://team.cloudflareaccess.com/cdn-cgi/access/login/hermes.example")]
    [InlineData("https://hermes.example/cdn-cgi/access/authorized")]
    public async Task A_redirect_to_the_access_login_is_an_access_block_and_is_never_followed(string login)
    {
        var (auth, _, _, net) = Make(_ => Redirect(login), withToken: false);
        var ex = await Assert.ThrowsAsync<AccessBlockedException>(() => Get(auth, "https://hermes.example/hermes/api/status"));
        Assert.False(ex.TokenSent);
        Assert.Equal("access_blocked", ex.Kind);
        Assert.Contains("service token", ex.Message);
        Assert.Single(net.Seen);                                                  // the login page was not fetched
    }

    [Fact]
    public async Task A_cloudflare_403_page_after_a_token_was_sent_says_the_token_was_turned_down_without_echoing_it()
    {
        var (auth, _, _, _) = Make(_ => CloudflareHtml403());
        var ex = await Assert.ThrowsAsync<AccessBlockedException>(() => Get(auth, "https://hermes.example/hermes/api/status"));
        Assert.True(ex.TokenSent);
        Assert.Contains("turned down", ex.Message);
        Assert.DoesNotContain(Secret, ex.Message);
        Assert.DoesNotContain(Id, ex.Message);
    }

    [Fact]
    public async Task Hermes_own_json_403_and_other_errors_are_not_mistaken_for_an_access_block()
    {
        var json403 = new Func<HttpRequestMessage, HttpResponseMessage>(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"detail":"forbidden"}""", Encoding.UTF8, "application/json") };
            r.Headers.TryAddWithoutValidation("Server", "cloudflare");           // behind Cloudflare, but the body is Hermes's
            return r;
        });
        var (auth, _, _, _) = Make(json403);
        using var resp = await auth.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://hermes.example/hermes/api/auth/me"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        var (auth2, _, _, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("nope", Encoding.UTF8, "text/plain") });
        using var plain = await auth2.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://hermes.example/x"));
        Assert.Equal(HttpStatusCode.Forbidden, plain.StatusCode);                 // a non-Cloudflare 403 is just a 403
    }

    [Theory]
    [InlineData(302, "https://x.cloudflareaccess.com/login", true)]
    [InlineData(302, "https://hermes.example/cdn-cgi/access/login", true)]
    [InlineData(302, "https://hermes.example/other", false)]
    [InlineData(200, "https://x.cloudflareaccess.com/login", false)]
    [InlineData(302, null, false)]
    public void Login_redirect_detection_matches_the_android_rule(int status, string? location, bool expected) =>
        Assert.Equal(expected, AccessErrors.IsLoginRedirect(status, location));

    [Theory]
    [InlineData(403, "cloudflare", false, true)]
    [InlineData(403, "Cloudflare", false, true)]
    [InlineData(403, "cloudflare", true, false)]
    [InlineData(403, "nginx", false, false)]
    [InlineData(500, "cloudflare", false, false)]
    public void Cloudflare_refusal_detection_matches_the_android_rule(int status, string server, bool json, bool expected) =>
        Assert.Equal(expected, AccessErrors.IsCloudflareRefusal(status, server, json));

    // ---- sign-in and probe through the same pipeline ---------------------------------------------

    [Fact]
    public async Task Password_sign_in_carries_the_token_and_a_block_surfaces_as_an_auth_failure_with_the_access_kind()
    {
        var (auth, _, _, net) = Make();
        await auth.SignInAsync("basic", "u", "p", default);
        Assert.Equal(Id, net.Seen.Single().Id);

        var (blocked, _, _, _) = Make(_ => CloudflareHtml403());
        var ex = await Assert.ThrowsAsync<AccessBlockedException>(() => blocked.SignInAsync("basic", "u", "p", default));
        Assert.IsAssignableFrom<AuthFailedException>(ex);
    }

    [Fact]
    public async Task The_probe_reports_reachable_blocked_unreachable_and_unexpected()
    {
        var (ok, _, _, _) = Make(_ => Ok("""{"version":"1.2.3","auth_flows":["password"]}"""));
        var r = await ok.ProbeAsync(default);
        Assert.Equal(GatewayReach.Reachable, r.Reach);
        Assert.Equal("1.2.3", r.Version);

        var (blocked, _, _, _) = Make(_ => CloudflareHtml403());
        var b = await blocked.ProbeAsync(default);
        Assert.Equal(GatewayReach.AccessBlocked, b.Reach);
        Assert.True(b.TokenSent);

        var (noToken, _, _, _) = Make(_ => Redirect("https://t.cloudflareaccess.com/login"), withToken: false);
        var n = await noToken.ProbeAsync(default);
        Assert.Equal(GatewayReach.AccessBlocked, n.Reach);
        Assert.False(n.TokenSent);

        var (down, _, _, _) = Make(_ => throw new HttpRequestException("connection refused"));
        Assert.Equal(GatewayReach.Unreachable, (await down.ProbeAsync(default)).Reach);

        var (html, _, _, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>hi</html>", Encoding.UTF8, "text/html") });
        Assert.Equal(GatewayReach.Unexpected, (await html.ProbeAsync(default)).Reach);
        var (err, _, _, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        Assert.Equal(GatewayReach.Unexpected, (await err.ProbeAsync(default)).Reach);
    }

    // ---- websocket header provider ---------------------------------------------------------------

    [Fact]
    public void The_websocket_upgrade_gets_the_headers_only_over_wss_and_only_for_the_token_host()
    {
        var (auth, _, _, _) = Make();
        var wss = auth.AccessHeadersFor(new Uri("wss://hermes.example/hermes/api/ws"));
        Assert.Equal(Id, wss["CF-Access-Client-Id"]);
        Assert.Equal(Secret, wss["CF-Access-Client-Secret"]);
        Assert.Empty(auth.AccessHeadersFor(new Uri("ws://hermes.example/hermes/api/ws")));         // plain ws: never
        Assert.Empty(auth.AccessHeadersFor(new Uri("wss://evil.example/hermes/api/ws")));
        Assert.Equal(Id, auth.AccessHeadersFor(new Uri("https://hermes.example/x"))["CF-Access-Client-Id"]);
    }

    // ---- store -----------------------------------------------------------------------------------

    [Fact]
    public void Tokens_are_stored_per_host_in_the_protected_store_and_survive_reload_and_removal()
    {
        var secrets = new MemorySecrets();
        var store = new AccessTokenStore(secrets);
        store.Set("a.example", new AccessToken("id-a", "secret-a"));
        store.Set("B.example", new AccessToken("id-b", "secret-b"));
        var again = new AccessTokenStore(secrets);
        Assert.Equal("id-a", again.Get("a.example")!.ClientId);
        Assert.Equal("id-b", again.Get("b.example.")!.ClientId);
        Assert.Null(again.Get("c.example"));
        Assert.True(again.Has("a.example"));
        Assert.All(secrets.Items.Keys, k => Assert.DoesNotContain("secret", k));    // secrets only in values, never in names
        again.Set("a.example", null);
        Assert.Null(new AccessTokenStore(secrets).Get("a.example"));
        Assert.DoesNotContain(secrets.Items, kv => kv.Key.Contains("a.example"));
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("id", "")]
    [InlineData("  ", "secret")]
    [InlineData("id\r\nX-Evil: 1", "secret")]
    [InlineData("id", "se\ncret")]
    [InlineData("idé", "secret")]
    [InlineData("id", "secrét")]
    public void Invalid_or_header_injecting_values_are_rejected_and_nothing_is_stored(string id, string secret)
    {
        Assert.False(AccessToken.TryValidate(id, secret, out var token, out var error));
        Assert.Null(token);
        Assert.False(string.IsNullOrEmpty(error));
        var secrets = new MemorySecrets();
        Assert.Throws<ArgumentException>(() => new AccessTokenStore(secrets).Set("h.example", new AccessToken(id, secret)));
        Assert.Empty(secrets.Items);
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed_and_overlong_values_are_refused()
    {
        Assert.True(AccessToken.TryValidate("  id  ", "\tsecret ", out var t, out _));
        Assert.Equal(("id", "secret"), (t!.ClientId, t.ClientSecret));
        Assert.False(AccessToken.TryValidate(new string('a', 513), "s", out _, out _));
        Assert.False(AccessToken.TryValidate("i", new string('a', 513), out _, out _));
    }

    [Fact]
    public void The_token_never_prints_and_a_corrupt_entry_is_ignored()
    {
        Assert.DoesNotContain(Secret, new AccessToken(Id, Secret).ToString());
        Assert.DoesNotContain(Id, new AccessToken(Id, Secret).ToString());
        var secrets = new MemorySecrets();
        var store = new AccessTokenStore(secrets);
        store.Set("h.example", new AccessToken(Id, Secret));
        foreach (var k in secrets.Items.Keys.ToList()) secrets.Items[k] = "{corrupt";
        Assert.Null(new AccessTokenStore(secrets).Get("h.example"));
    }
}

/// <summary>Stage 21 — the rules behind Settings → Cloudflare Access (no window needed).</summary>
public class Checkpoint21AccessSettingsTests
{
    private sealed class Mem : ISecretStore
    {
        public readonly Dictionary<string, string> Items = new();
        public string? Get(string n) => Items.TryGetValue(n, out var v) ? v : null;
        public void Set(string n, string v) => Items[n] = v;
        public void Remove(string n) => Items.Remove(n);
    }

    private static (AccessTokenSettings Rules, AccessTokenStore Store, Mem Mem) Make()
    {
        var mem = new Mem();
        var store = new AccessTokenStore(mem);
        return (new AccessTokenSettings(store), store, mem);
    }

    private static readonly Uri Https = new("https://hermes.example/hermes/");

    [Fact]
    public void Saving_for_an_https_gateway_stores_it_for_that_host_and_never_repeats_the_secret()
    {
        var (rules, store, _) = Make();
        var r = rules.Save(Https, "  my-id.access ", " very-secret-value ");
        Assert.True(r.Ok);
        Assert.Contains("hermes.example", r.Message);
        Assert.DoesNotContain("very-secret-value", r.Message);
        Assert.DoesNotContain("my-id", r.Message);
        Assert.Equal("my-id.access", store.Get("hermes.example")!.ClientId);
        Assert.DoesNotContain("very-secret-value", rules.Status(Https));
        Assert.Contains("saved", rules.Status(Https));
    }

    [Theory]
    [InlineData("http://hermes.example/")]
    [InlineData("ftp://hermes.example/")]
    public void A_token_is_refused_for_a_non_https_address_because_it_would_never_be_sent(string url)
    {
        var (rules, store, mem) = Make();
        var r = rules.Save(new Uri(url), "id", "secret");
        Assert.False(r.Ok);
        Assert.Contains("https", r.Message);
        Assert.Empty(mem.Items);
        Assert.False(store.Has("hermes.example"));
    }

    [Fact]
    public void Missing_url_and_invalid_values_explain_themselves_and_store_nothing()
    {
        var (rules, _, mem) = Make();
        Assert.False(rules.Save(null, "id", "secret").Ok);
        var bad = rules.Save(Https, "id", "se cret");
        Assert.False(bad.Ok);
        Assert.DoesNotContain("se cret", bad.Message);
        Assert.False(rules.Save(Https, "", "secret").Ok);
        Assert.Empty(mem.Items);
        Assert.Contains("Enter", rules.Status(null));
    }

    [Fact]
    public void Removing_forgets_only_that_hosts_token()
    {
        var (rules, store, _) = Make();
        rules.Save(Https, "id-a", "secret-a");
        rules.Save(new Uri("https://other.example/"), "id-b", "secret-b");
        var r = rules.Remove(Https);
        Assert.True(r.Ok);
        Assert.False(store.Has("hermes.example"));
        Assert.True(store.Has("other.example"));
        Assert.Contains("No service token", rules.Status(Https));
        Assert.False(rules.Remove(null).Ok);
    }

    [Fact]
    public void The_test_buttons_messages_say_whether_the_token_was_used_and_never_contain_it()
    {
        Assert.Contains("saved service token", AccessTokenSettings.Describe(new GatewayProbeResult(GatewayReach.Reachable, "Reachable.", true)));
        Assert.DoesNotContain("saved service token", AccessTokenSettings.Describe(new GatewayProbeResult(GatewayReach.Reachable, "Reachable.", false)));
        Assert.Equal("blocked", AccessTokenSettings.Describe(new GatewayProbeResult(GatewayReach.AccessBlocked, "blocked", true)));
    }
}

/// <summary>
/// Stage 21 — over a real loopback socket (no mocks): with a token stored for the host, neither the plain-http ticket request nor the
/// plain-ws upgrade carries the Access headers. (The https/wss positive path needs TLS and is covered at the header-provider level.)
/// </summary>
public class Checkpoint21AccessLoopbackTests
{
    private sealed class Mem : ISecretStore
    {
        private readonly Dictionary<string, string> _d = new();
        public string? Get(string n) => _d.TryGetValue(n, out var v) ? v : null;
        public void Set(string n, string v) => _d[n] = v;
        public void Remove(string n) => _d.Remove(n);
    }

    [Fact]
    public async Task Plain_http_and_plain_ws_never_carry_the_access_token_even_when_one_is_stored_for_the_host()
    {
        var listener = TestLoopback.Start(19150, 19200, out var port);

        var seen = new List<(string Path, string? Id, string? Secret)>();
        using var run = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!run.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().WaitAsync(run.Token); } catch (Exception) { return; }
                lock (seen) seen.Add((ctx.Request.Url!.AbsolutePath, ctx.Request.Headers["CF-Access-Client-Id"], ctx.Request.Headers["CF-Access-Client-Secret"]));
                if (ctx.Request.Url.AbsolutePath == "/api/auth/ws-ticket")
                {
                    var b = Encoding.UTF8.GetBytes("""{"ticket":"T","ttl_seconds":30}""");
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(b);
                    ctx.Response.Close();
                }
                else if (ctx.Request.IsWebSocketRequest)
                {
                    var wsCtx = await ctx.AcceptWebSocketAsync("hermes-gateway-v1");
                    var ready = Encoding.UTF8.GetBytes(FakeRpcTransport.ReadyFrame);
                    await wsCtx.WebSocket.SendAsync(ready, System.Net.WebSockets.WebSocketMessageType.Text, true, run.Token);
                    await Task.Delay(300, run.Token).ContinueWith(_ => { });
                    try { await wsCtx.WebSocket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { /* client gone */ }
                }
                else { ctx.Response.StatusCode = 404; ctx.Response.Close(); }
            }
        });

        try
        {
            var store = new AccessTokenStore(new Mem());
            store.Set("127.0.0.1", new AccessToken("loop-id.access", "loop-secret-value"));
            var auth = new DashboardAuth(new Uri($"http://127.0.0.1:{port}/"), access: store);
            await using var transport = new HermesDashboardTransport(auth, "gw", ownsAuth: true,
                ensureSignedIn: _ => Task.CompletedTask);
            await transport.EnsureConnectedAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

            lock (seen)
            {
                Assert.Contains(seen, s => s.Path == "/api/auth/ws-ticket");
                Assert.Contains(seen, s => s.Path == "/api/ws");
                Assert.All(seen, s => { Assert.Null(s.Id); Assert.Null(s.Secret); });
            }
        }
        finally
        {
            run.Cancel();
            listener.Stop();
            try { await server.WaitAsync(TimeSpan.FromSeconds(3)); } catch { /* shutting down */ }
        }
    }
}
