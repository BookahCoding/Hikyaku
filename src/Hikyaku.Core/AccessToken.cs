using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Hikyaku.Core;

/// <summary>
/// A Cloudflare Access service token: lets the app through Access without a browser login (BookahHermes AccessTokens.kt).
/// Never printed or logged.
/// </summary>
public sealed record AccessToken(string ClientId, string ClientSecret)
{
    public const int MaxLength = 512;

    public override string ToString() => "AccessToken(redacted)";

    /// <summary>
    /// Both parts are required, trimmed, at most <see cref="MaxLength"/> characters of printable ASCII with no spaces: anything
    /// else could not be a Cloudflare credential and, in a header, could be used to inject extra headers.
    /// </summary>
    public static bool TryValidate(string? clientId, string? clientSecret, out AccessToken? token, out string? error)
    {
        token = null;
        var id = clientId?.Trim();
        var secret = clientSecret?.Trim();
        if (string.IsNullOrEmpty(id)) { error = "Enter the Client ID."; return false; }
        if (string.IsNullOrEmpty(secret)) { error = "Enter the Client Secret."; return false; }
        if (id.Length > MaxLength || secret.Length > MaxLength) { error = "That value is too long to be a Cloudflare credential."; return false; }
        if (!IsHeaderSafe(id) || !IsHeaderSafe(secret)) { error = "The Client ID and Secret may only contain visible ASCII characters, with no spaces or line breaks."; return false; }
        token = new AccessToken(id, secret);
        error = null;
        return true;
    }

    private static bool IsHeaderSafe(string s) => s.All(c => c is > ' ' and < (char)0x7F);
}

/// <summary>The host as Access tokens are keyed: lower case, without IPv6 brackets or a trailing dot.</summary>
public static class AccessHost
{
    public static string Key(string host) => (host ?? "").Trim().ToLowerInvariant().Trim('[', ']').TrimEnd('.');
}

/// <summary>
/// Service tokens for gateways behind Cloudflare Access, by host, in the OS-protected <see cref="ISecretStore"/> (DPAPI in the app).
/// Access protects a hostname, so a token goes with requests to that host and to no other. One instance per app keeps the cache coherent.
/// </summary>
public sealed class AccessTokenStore(ISecretStore secrets)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AccessToken?> _cache = new();

    private static string Name(string host) => "cf-access:" + AccessHost.Key(host);

    public bool Has(string host) => Get(host) is not null;

    public AccessToken? Get(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var name = Name(host);
        lock (_gate)
        {
            if (_cache.TryGetValue(name, out var cached)) return cached;
            AccessToken? token = null;
            if (secrets.Get(name) is { } raw)
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    var r = doc.RootElement;
                    if (AccessToken.TryValidate(r.GetProperty("client_id").GetString(), r.GetProperty("client_secret").GetString(), out var t, out _)) token = t;
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { token = null; }
            }
            _cache[name] = token;
            return token;
        }
    }

    /// <summary>A null token forgets the host's. An invalid token throws and stores nothing.</summary>
    public void Set(string host, AccessToken? token)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("host required", nameof(host));
        var name = Name(host);
        lock (_gate)
        {
            if (token is null) { secrets.Remove(name); _cache[name] = null; return; }
            if (!AccessToken.TryValidate(token.ClientId, token.ClientSecret, out var valid, out var error))
                throw new ArgumentException(error, nameof(token));
            secrets.Set(name, JsonSerializer.Serialize(new { client_id = valid!.ClientId, client_secret = valid.ClientSecret }));
            _cache[name] = valid;
        }
    }
}

public static class AccessErrors
{
    public const string ClientIdHeader = "CF-Access-Client-Id";
    public const string ClientSecretHeader = "CF-Access-Client-Secret";

    /// <summary>A redirect to the Cloudflare Access login: Access stopped the request before it reached Hermes.</summary>
    public static bool IsLoginRedirect(int status, string? location)
    {
        if (status is < 300 or > 399 || string.IsNullOrEmpty(location)) return false;
        if (Uri.TryCreate(location, UriKind.Absolute, out var u)
            && u.Host.EndsWith(".cloudflareaccess.com", StringComparison.OrdinalIgnoreCase)) return true;
        return location.Contains("/cdn-cgi/access/", StringComparison.Ordinal);
    }

    /// <summary>Cloudflare itself answering 403 with its own page (not JSON from Hermes): Access turned the request down.</summary>
    public static bool IsCloudflareRefusal(int status, string? server, bool isJson) =>
        status == 403 && !isJson && string.Equals(server, "cloudflare", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Cloudflare Access refused or redirected the request before Hermes saw it. Carries whether a token was sent; never the token.</summary>
public sealed class AccessBlockedException(bool tokenSent, int status) : AuthFailedException("access_blocked",
    tokenSent
        ? $"Cloudflare Access turned down this gateway's service token (HTTP {status}). Check the Client ID and Client Secret, and that the Access application has a Service Auth policy that includes this token."
        : $"Cloudflare Access stopped the request before it reached Hermes (HTTP {status}). Add a Cloudflare Access service token for this gateway in Settings, and give the Access application a Service Auth policy that includes it.")
{
    public bool TokenSent { get; } = tokenSent;
    public int Status { get; } = status;
}

public enum GatewayReach { Reachable, AccessBlocked, Unreachable, Unexpected }

public sealed record GatewayProbeResult(GatewayReach Reach, string Message, bool TokenSent = false, string? Version = null);

/// <summary>
/// Outermost network step: adds the host's Access headers (https only, never any other host), follows only same-site GET redirects
/// itself (so a redirect to another host can never carry the secret, and the Access login page is never fetched), and turns "Access
/// blocked this" into <see cref="AccessBlockedException"/> for every caller.
/// </summary>
internal sealed class CloudflareAccessHandler(HttpMessageHandler inner, AccessTokenStore? store) : DelegatingHandler(inner)
{
    public const int MaxRedirects = 5;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var current = request;
        for (var hop = 0; ; hop++)
        {
            var tokenSent = Apply(current);
            var response = await base.SendAsync(current, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                var target = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
                if (AccessErrors.IsLoginRedirect(status, target.AbsoluteUri))
                {
                    response.Dispose();
                    throw new AccessBlockedException(tokenSent, status);
                }
                if (hop < MaxRedirects && IsSameSite(current.RequestUri!, target)
                    && (current.Method == HttpMethod.Get || current.Method == HttpMethod.Head) && current.Content is null)
                {
                    response.Dispose();
                    current = Follow(current, target);
                    continue;
                }
                return response;                          // another host, a POST, or too many hops: the caller decides
            }

            var isJson = response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
            var server = response.Headers.TryGetValues("Server", out var s) ? s.FirstOrDefault() : null;
            if (AccessErrors.IsCloudflareRefusal(status, server, isJson))
            {
                response.Dispose();
                throw new AccessBlockedException(tokenSent, status);
            }
            return response;
        }
    }

    /// <summary>Replaces any access headers with this host's token (https only). Returns whether a token was attached.</summary>
    private bool Apply(HttpRequestMessage r)
    {
        r.Headers.Remove(AccessErrors.ClientIdHeader);
        r.Headers.Remove(AccessErrors.ClientSecretHeader);
        if (r.RequestUri is not { } uri || uri.Scheme != Uri.UriSchemeHttps) return false;
        if (store?.Get(uri.Host) is not { } token) return false;
        r.Headers.TryAddWithoutValidation(AccessErrors.ClientIdHeader, token.ClientId);
        r.Headers.TryAddWithoutValidation(AccessErrors.ClientSecretHeader, token.ClientSecret);
        return true;
    }

    private static bool IsSameSite(Uri from, Uri to) =>
        string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase)
        && ((from.Scheme == to.Scheme && from.Port == to.Port)
            || (from.Scheme == Uri.UriSchemeHttp && to.Scheme == Uri.UriSchemeHttps && from.IsDefaultPort && to.IsDefaultPort));

    private static HttpRequestMessage Follow(HttpRequestMessage from, Uri target)
    {
        var next = new HttpRequestMessage(from.Method, target) { Version = from.Version };
        foreach (var h in from.Headers)
            if (!h.Key.Equals(AccessErrors.ClientIdHeader, StringComparison.OrdinalIgnoreCase)
                && !h.Key.Equals(AccessErrors.ClientSecretHeader, StringComparison.OrdinalIgnoreCase))
                next.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return next;
    }
}
