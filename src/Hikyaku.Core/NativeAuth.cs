using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hikyaku.Core;

/// <summary>
/// Bearer tokens from a browser sign-in (<c>/auth/native/token</c>, <c>/auth/native/refresh</c>; BookahHermes
/// NativeTokens.kt @ 5f00986b). <see cref="ExpiresAt"/> is Unix seconds. Never printed or logged.
/// </summary>
public sealed record NativeSession(
    string AccessToken, string RefreshToken, long ExpiresAt, string? Provider, string? UserId, string BaseUrl)
{
    public const int MaxResponseBytes = 64 * 1024;

    public override string ToString() => "NativeSession(redacted)";

    internal string ToJson() => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["access_token"] = AccessToken,
        ["refresh_token"] = RefreshToken,
        ["expires_at"] = ExpiresAt,
        ["provider"] = Provider,
        ["user_id"] = UserId,
        ["base_url"] = BaseUrl
    });

    /// <summary>Parses a token/refresh response or a stored entry; both tokens must be non-empty strings.</summary>
    internal static NativeSession? TryParse(string json, string baseUrl)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var at = Str(r, "access_token");
            var rt = Str(r, "refresh_token");
            if (string.IsNullOrEmpty(at) || string.IsNullOrEmpty(rt)) return null;
            var exp = r.TryGetProperty("expires_at", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var l) ? l : 0;
            return new NativeSession(at, rt, exp, Str(r, "provider"), Str(r, "user_id"), baseUrl);
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string n) =>
        e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}

/// <summary>
/// Browser sign-in tokens by gateway, kept in the OS-protected <see cref="ISecretStore"/> (DPAPI in the app).
/// One instance per app so the cache stays coherent.
/// </summary>
public sealed class NativeTokenStore(ISecretStore secrets)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, NativeSession?> _cache = new();

    private static string Key(Uri gateway) => "native-session:" + DashboardAuth.NormalizeBase(gateway);

    public NativeSession? Get(Uri gateway)
    {
        var key = Key(gateway);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
            var raw = secrets.Get(key);
            var session = raw is null ? null : NativeSession.TryParse(raw, DashboardAuth.NormalizeBase(gateway).ToString());
            _cache[key] = session;
            return session;
        }
    }

    /// <summary>A null session forgets the gateway's tokens.</summary>
    public void Set(Uri gateway, NativeSession? session)
    {
        var key = Key(gateway);
        var baseUrl = DashboardAuth.NormalizeBase(gateway).ToString();
        lock (_gate)
        {
            if (session is null)
            {
                secrets.Remove(key);
                _cache[key] = null;
                return;
            }
            var bound = session with { BaseUrl = baseUrl };
            secrets.Set(key, bound.ToJson());
            _cache[key] = bound;
        }
    }
}

/// <summary>RFC 7636 S256 primitives (Android pkceVerifier/pkceChallenge).</summary>
public static class Pkce
{
    /// <summary>32 random bytes → 43 base64url characters.</summary>
    public static string NewVerifier() => Token(32);

    public static string NewState() => Token(16);

    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Token(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The system browser may only be pointed at the configured gateway's authorize page.</summary>
public static class BrowserLaunchPolicy
{
    public static bool IsAllowed(Uri url, Uri gatewayBase)
    {
        if (!url.IsAbsoluteUri) return false;
        if (url.Scheme is not ("http" or "https")) return false;
        var g = DashboardAuth.NormalizeBase(gatewayBase);
        return string.Equals(url.Scheme, g.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(url.Host, g.Host, StringComparison.OrdinalIgnoreCase)
            && url.Port == g.Port
            && string.Equals(url.AbsolutePath, g.AbsolutePath + "auth/native/authorize", StringComparison.Ordinal);
    }
}

/// <summary>
/// Sends the browser sign-in's access token as <c>Authorization: Bearer</c> on this gateway's <c>/api/</c> calls
/// only (never <c>/auth/*</c>, never another origin or path prefix). A 401 triggers one single-flight refresh and a
/// retry (NativeBearer.kt). A refresh the gateway rejects (401/400) forgets the tokens; one it cannot do (503,
/// network) keeps them, fails the call as <c>unavailable</c> and is not retried for 30 s.
/// </summary>
internal sealed class NativeBearerHandler : DelegatingHandler
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private readonly NativeTokenStore _tokens;
    private readonly Uri _base;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _failedAt = new();

    public NativeBearerHandler(HttpMessageHandler inner, NativeTokenStore tokens, Uri baseUri, Func<DateTimeOffset> clock)
        : base(inner)
    {
        _tokens = tokens;
        _base = baseUri;
        _clock = clock;
    }

    private bool IsGatewayApi(Uri? u) =>
        u is { IsAbsoluteUri: true }
        && string.Equals(u.Scheme, _base.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(u.Host, _base.Host, StringComparison.OrdinalIgnoreCase)
        && u.Port == _base.Port
        && u.AbsolutePath.StartsWith(_base.AbsolutePath + "api/", StringComparison.Ordinal);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!IsGatewayApi(request.RequestUri) || _tokens.Get(_base) is not { } session)
            return await base.SendAsync(request, ct).ConfigureAwait(false);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        var resp = await base.SendAsync(request, ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.Unauthorized) return resp;

        var refreshed = await RefreshAsync(session, ct).ConfigureAwait(false);
        switch (refreshed.Outcome)
        {
            case RefreshOutcome.Rejected:
                return resp;
            case RefreshOutcome.Unreachable:
                resp.Dispose();
                throw new AuthFailedException("unavailable", "The gateway could not renew the sign-in; try again shortly.");
        }

        var retry = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, Content = request.Content };
        foreach (var h in request.Headers.Where(h => !h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))
            retry.Headers.TryAddWithoutValidation(h.Key, h.Value);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.Session!.AccessToken);
        try
        {
            var second = await base.SendAsync(retry, ct).ConfigureAwait(false);
            resp.Dispose();
            return second;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Body not replayable (streamed upload): surface the original 401 rather than guess.
            return resp;
        }
    }

    private enum RefreshOutcome { Done, Rejected, Unreachable }

    private readonly record struct RefreshResult(RefreshOutcome Outcome, NativeSession? Session);

    private async Task<RefreshResult> RefreshAsync(NativeSession used, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _tokens.Get(_base);
            if (current is null) return new(RefreshOutcome.Rejected, null);
            // Another call refreshed while this one waited.
            if (current.AccessToken != used.AccessToken) return new(RefreshOutcome.Done, current);
            if (_failedAt.TryGetValue(current.AccessToken, out var at) && _clock() - at < RetryAfter)
                return new(RefreshOutcome.Unreachable, null);

            try
            {
                var body = JsonSerializer.Serialize(new { refresh_token = current.RefreshToken, provider = current.Provider ?? "" });
                using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "auth/native/refresh"))
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                using var resp = await base.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var text = await BoundedText.ReadAsync(resp.Content, NativeSession.MaxResponseBytes, ct).ConfigureAwait(false);
                    var next = text is null ? null : NativeSession.TryParse(text, _base.ToString());
                    if (next is not null)
                    {
                        _tokens.Set(_base, next);
                        _failedAt.Remove(current.AccessToken);
                        return new(RefreshOutcome.Done, _tokens.Get(_base));
                    }
                }
                else if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
                {
                    _tokens.Set(_base, null);
                    _failedAt.Remove(current.AccessToken);
                    return new(RefreshOutcome.Rejected, null);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                // fall through to Unreachable
            }
            _failedAt[current.AccessToken] = _clock();
            return new(RefreshOutcome.Unreachable, null);
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}

internal static class BoundedText
{
    /// <summary>Reads at most <paramref name="max"/> bytes; null when the body is larger.</summary>
    public static async Task<string?> ReadAsync(HttpContent content, int max, CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } len && len > max) return null;
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[max + 1];
        var total = 0;
        while (total < buf.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total > max ? null : Encoding.UTF8.GetString(buf, 0, total);
    }
}
