using System.Security.Cryptography;
using System.Text;

namespace Hikyaku.Core;

/// <summary>
/// What a live gateway connection was built from: address, gateway id, username and a digest of the sign-in material
/// (saved password, which browser sign-in, Cloudflare Access token). Closing Settings keeps the connection when this is
/// unchanged and rebuilds it at once when it changed, so a visit to Settings never leaves the app disconnected.
/// Secrets only enter as a SHA-256 digest held in memory; nothing here is stored or logged.
/// </summary>
public sealed record ConnectionIdentity(string Url, string GatewayId, string Username, string SignInDigest)
{
    public static ConnectionIdentity Of(Uri? url, string? gatewayId, string? username, string? password,
        NativeSession? browserSignIn, AccessToken? accessToken)
    {
        // A browser sign-in is identified by who signed in, not by its tokens: those rotate on every refresh.
        var parts = new[]
        {
            password ?? "\0",
            browserSignIn is null ? "\0" : (browserSignIn.Provider ?? "") + "\u0001" + (browserSignIn.UserId ?? ""),
            accessToken is null ? "\0" : accessToken.ClientId + "\u0001" + accessToken.ClientSecret
        };
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u0002", parts))));
        return new ConnectionIdentity(url?.AbsoluteUri ?? "", (gatewayId ?? "").Trim(), (username ?? "").Trim(), digest);
    }
}
