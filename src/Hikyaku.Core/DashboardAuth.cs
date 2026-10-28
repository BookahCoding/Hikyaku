using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Hikyaku.Core;

public class AuthFailedException(string kind, string message) : Exception(message)
{
    public string Kind { get; } = kind; // invalid_credentials | rate_limited | session_expired | unavailable | failed
}

/// <summary>
/// Hermes dashboard auth (BookahHermes AuthApi.kt / hermes_cli/dashboard_auth).
/// Password login sets session cookies; WS uses a single-use ticket from POST /api/auth/ws-ticket.
/// </summary>
public sealed class DashboardAuth : IHermesDashboardAuth, IDisposable
{
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies;
    private readonly Uri _baseUri;
    private readonly bool _ownsHttp;
    private readonly NativeTokenStore? _tokens;
    private readonly AccessTokenStore? _access;

    /// <param name="tokens">Browser sign-in token store; when set, its bearer is attached to this gateway's /api/ calls.</param>
    /// <param name="access">Cloudflare Access service tokens; the host's token (if any) is attached over https only.</param>
    public DashboardAuth(Uri baseUri, HttpMessageHandler? handler = null, NativeTokenStore? tokens = null,
        Func<DateTimeOffset>? clock = null, AccessTokenStore? access = null)
    {
        _baseUri = NormalizeBase(baseUri);
        _cookies = new CookieContainer();
        _tokens = tokens;
        _access = access;
        // Redirects are followed by CloudflareAccessHandler (same-site GET only), never by the platform handler: a platform
        // redirect would carry custom headers, such as the Access secret, to whatever host the redirect names.
        HttpMessageHandler pipeline = handler is null
            ? new HttpClientHandler { CookieContainer = _cookies, UseCookies = true, AllowAutoRedirect = false }
            // Tests inject a handler; wrap so cookies still attach when the fake echoes Set-Cookie.
            : new CookieForwardingHandler(handler, _cookies, _baseUri);
        pipeline = new CloudflareAccessHandler(pipeline, access);
        if (tokens is not null)
            pipeline = new NativeBearerHandler(pipeline, tokens, _baseUri, clock ?? (() => DateTimeOffset.UtcNow));
        _http = new HttpClient(pipeline) { BaseAddress = _baseUri, Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttp = true;
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Test helper: build over an existing cookie jar and client factory.</summary>
    internal DashboardAuth(Uri baseUri, CookieContainer cookies, HttpClient http)
    {
        _baseUri = NormalizeBase(baseUri);
        _cookies = cookies;
        _http = http;
        _ownsHttp = false;
    }

    public Uri BaseUri => _baseUri;
    public CookieContainer Cookies => _cookies;
    public bool HasSession => HasNativeSession || _cookies.GetCookies(_baseUri).Cast<Cookie>().Any(c =>
        c.Name.Contains("hermes_session", StringComparison.OrdinalIgnoreCase));

    /// <summary>A Cloudflare Access service token is stored for this gateway's host.</summary>
    public bool HasAccessToken => _access?.Has(_baseUri.Host) == true;

    /// <summary>The Access headers for <paramref name="url"/> (https/wss only, that host's token only); empty otherwise. For the WebSocket upgrade.</summary>
    public IReadOnlyDictionary<string, string> AccessHeadersFor(Uri url)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (url.Scheme is not ("https" or "wss") || _access?.Get(url.Host) is not { } token) return headers;
        headers[AccessErrors.ClientIdHeader] = token.ClientId;
        headers[AccessErrors.ClientSecretHeader] = token.ClientSecret;
        return headers;
    }

    /// <summary>
    /// Is something answering as a Hermes dashboard at this address, and if not, why (Access in the way with or without a token,
    /// unreachable, or not Hermes)? Public <c>GET /api/status</c>; sends no credentials beyond the Access token.
    /// </summary>
    public async Task<GatewayProbeResult> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(new Uri(_baseUri, "api/status"), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new GatewayProbeResult(GatewayReach.Unexpected, $"The address answered HTTP {(int)resp.StatusCode}, which is not what a Hermes dashboard sends.");
            var text = await BoundedText.ReadAsync(resp.Content, NativeSession.MaxResponseBytes, ct).ConfigureAwait(false);
            if (text is null) return new GatewayProbeResult(GatewayReach.Unexpected, "The answer was unexpectedly large.");
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new GatewayProbeResult(GatewayReach.Unexpected, "The address answered, but not like a Hermes dashboard.");
            var version = doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new GatewayProbeResult(GatewayReach.Reachable, version is null ? "Reachable." : "Reachable (Hermes " + version + ").", HasAccessToken, version);
        }
        catch (AccessBlockedException ex)
        {
            return new GatewayProbeResult(GatewayReach.AccessBlocked, ex.Message, ex.TokenSent);
        }
        catch (JsonException)
        {
            return new GatewayProbeResult(GatewayReach.Unexpected, "The address answered, but not like a Hermes dashboard.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new GatewayProbeResult(GatewayReach.Unreachable, "Could not reach the address: " + (ex is HttpRequestException ? ex.Message : "timed out") + ".");
        }
    }

    /// <summary>A stored browser sign-in (bearer) session for this gateway.</summary>
    public bool HasNativeSession => _tokens?.Get(_baseUri) is not null;

    /// <summary>Authenticated dashboard HTTP (cookies attached). Used by SessionsApi / MediaApi.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

    public static Uri NormalizeBase(Uri uri)
    {
        var builder = new UriBuilder(uri) { Query = null, Fragment = null };
        var path = builder.Path.TrimEnd('/');
        builder.Path = path.Length == 0 ? "/" : path;
        var s = builder.Uri.ToString().TrimEnd('/');
        return new Uri(s + "/");
    }

    public static string TicketProtocols(string ticket) =>
        $"hermes-gateway-v1, hermes-gateway-ticket.{ticket}";

    public static Uri WebSocketUri(Uri baseUri)
    {
        var b = new UriBuilder(NormalizeBase(baseUri))
        {
            Scheme = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = new Uri(NormalizeBase(baseUri), "api/ws").AbsolutePath
        };
        return b.Uri;
    }

    public async Task SignInAsync(string provider, string username, string password, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { provider, username, password });
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "auth/password-login"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureAuthOkAsync(resp, isLogin: true, ct).ConfigureAwait(false);
        // A password session's cookies would be shadowed by older bearer tokens, so those go (AuthApi.signIn).
        _tokens?.Set(_baseUri, null);
    }

    /// <summary><c>GET /auth/native/authorize</c>: the page the system browser opens. Blank provider lets the gateway choose.</summary>
    public Uri NativeAuthorizeUri(string? provider, string codeChallenge, string redirectUri, string state)
    {
        var q = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(provider)) q.Append("provider=").Append(Uri.EscapeDataString(provider)).Append('&');
        q.Append("code_challenge=").Append(Uri.EscapeDataString(codeChallenge))
         .Append("&code_challenge_method=S256")
         .Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri))
         .Append("&state=").Append(Uri.EscapeDataString(state));
        return new Uri(new Uri(_baseUri, "auth/native/authorize") + "?" + q);
    }

    /// <summary><c>POST /auth/native/token</c>: one-time code + PKCE verifier → bearer tokens, stored for this gateway.</summary>
    public async Task RedeemNativeCodeAsync(string code, string codeVerifier, CancellationToken ct)
    {
        if (_tokens is null) throw new InvalidOperationException("Browser sign-in needs a token store.");
        var body = JsonSerializer.Serialize(new { code, code_verifier = codeVerifier });
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "auth/native/token"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureAuthOkAsync(resp, isLogin: true, ct).ConfigureAwait(false);
        var text = await BoundedText.ReadAsync(resp.Content, NativeSession.MaxResponseBytes, ct).ConfigureAwait(false);
        var session = text is null ? null : NativeSession.TryParse(text, _baseUri.ToString());
        if (session is null) throw new AuthFailedException("failed", "The gateway's sign-in response was not usable.");
        _tokens.Set(_baseUri, session);
        ClearCookies();
    }

    /// <summary>
    /// Best-effort server logout, then forget cookies and bearer tokens locally regardless. The gateway has no
    /// revoke for a browser sign-in's tokens; they are only forgotten here.
    /// </summary>
    public async Task SignOutAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "auth/logout"));
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or AuthFailedException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested)) { /* offline or timed out: sign-out still clears */ }
        ClearCookies();
        _tokens?.Set(_baseUri, null);
    }

    /// <summary>Public <c>GET /api/status</c>: does the gateway advertise <c>native_pkce</c> in <c>auth_flows</c>?</summary>
    public async Task<bool> SupportsNativeSignInAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(new Uri(_baseUri, "api/status"), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var text = await BoundedText.ReadAsync(resp.Content, NativeSession.MaxResponseBytes, ct).ConfigureAwait(false);
            if (text is null) return false;
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("auth_flows", out var flows)
                && flows.ValueKind == JsonValueKind.Array
                && flows.EnumerateArray().Any(f => f.ValueKind == JsonValueKind.String && f.GetString() == "native_pkce");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or AuthFailedException) { return false; }
    }

    private void ClearCookies()
    {
        foreach (Cookie c in _cookies.GetCookies(_baseUri)) c.Expired = true;
    }

    public async Task<HermesAuthUser> MeAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync(new Uri(_baseUri, "api/auth/me"), ct).ConfigureAwait(false);
        await EnsureAuthOkAsync(resp, isLogin: false, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var r = doc.RootElement;
        return new HermesAuthUser(
            TryString(r, "user_id"),
            TryString(r, "display_name"),
            TryString(r, "email"),
            TryString(r, "provider"));
    }

    public async Task<string> MintWsTicketAsync(CancellationToken ct)
    {
        using var resp = await _http.PostAsync(new Uri(_baseUri, "api/auth/ws-ticket"), content: null, ct)
            .ConfigureAwait(false);
        await EnsureAuthOkAsync(resp, isLogin: false, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("ticket", out var t) || t.ValueKind != JsonValueKind.String)
            throw new AuthFailedException("failed", "ws-ticket response missing ticket");
        return t.GetString()!;
    }

    public async Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(string gatewayId, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(new Uri(_baseUri, "api/profiles"), ct).ConfigureAwait(false);
        await EnsureAuthOkAsync(resp, isLogin: false, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var list = new List<ProfileInfo>();
        if (doc.RootElement.TryGetProperty("profiles", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in arr.EnumerateArray())
            {
                var name = TryString(p, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var display = TryString(p, "display_name") ?? name;
                list.Add(new ProfileInfo(new ProfileKey(gatewayId, name), display));
            }
        }
        return list;
    }

    private static async Task EnsureAuthOkAsync(HttpResponseMessage resp, bool isLogin, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var detail = await ReadDetailAsync(resp, ct).ConfigureAwait(false);
        throw resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized when isLogin => new AuthFailedException("invalid_credentials", detail ?? "Invalid credentials"),
            HttpStatusCode.Unauthorized => new AuthFailedException("session_expired", detail ?? "session_expired"),
            HttpStatusCode.TooManyRequests => new AuthFailedException("rate_limited", detail ?? "Rate limited"),
            HttpStatusCode.ServiceUnavailable => new AuthFailedException("unavailable", detail ?? "Unavailable"),
            _ => new AuthFailedException("failed", detail ?? $"HTTP {(int)resp.StatusCode}")
        };
    }

    private static async Task<string?> ReadDetailAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
                return d.GetString();
            if (doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                return r.GetString();
            return text.Length > 200 ? text[..200] : text;
        }
        catch { return null; }
    }

    private static string? TryString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>Applies Set-Cookie from responses and Cookie request headers when a test handler does not.</summary>
internal sealed class CookieForwardingHandler(HttpMessageHandler inner, CookieContainer cookies, Uri baseUri)
    : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var cookieHeader = cookies.GetCookieHeader(request.RequestUri ?? baseUri);
        if (!string.IsNullOrEmpty(cookieHeader))
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

        var resp = await base.SendAsync(request, ct).ConfigureAwait(false);
        if (resp.Headers.TryGetValues("Set-Cookie", out var sets))
        {
            foreach (var set in sets)
            {
                try { cookies.SetCookies(request.RequestUri ?? baseUri, set); }
                catch (CookieException) { /* ignore malformed fixture cookies */ }
            }
        }
        return resp;
    }
}

