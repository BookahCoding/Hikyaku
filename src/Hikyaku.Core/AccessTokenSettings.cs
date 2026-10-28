namespace Hikyaku.Core;

public sealed record AccessSettingsResult(bool Ok, string Message);

/// <summary>
/// The rules behind Settings → Cloudflare Access, apart from the window: a token is only accepted for an https gateway address (it
/// is only ever sent over https), is validated, is stored per host, and no message ever contains the Client ID or Secret.
/// </summary>
public sealed class AccessTokenSettings(AccessTokenStore store)
{
    public AccessSettingsResult Save(Uri? gateway, string? clientId, string? clientSecret)
    {
        if (gateway is null) return new(false, "Enter a valid dashboard URL first.");
        if (gateway.Scheme != Uri.UriSchemeHttps)
            return new(false, "Cloudflare Access is always https. The token is only ever sent over https, so use an https address.");
        if (!AccessToken.TryValidate(clientId, clientSecret, out var token, out var error))
            return new(false, error ?? "That token is not valid.");
        store.Set(gateway.Host, token);
        return new(true, $"Saved for {gateway.Host}. It is sent with every request to that address and nowhere else. The secret is stored encrypted for your Windows user and is not shown again.");
    }

    public AccessSettingsResult Remove(Uri? gateway)
    {
        if (gateway is null) return new(false, "Enter a valid dashboard URL first.");
        store.Set(gateway.Host, null);
        return new(true, $"Removed the service token for {gateway.Host}.");
    }

    public string Status(Uri? gateway) =>
        gateway is null ? "Enter the gateway address under Account first."
        : store.Has(gateway.Host) ? $"A service token is saved for {gateway.Host}. The secret is stored encrypted for your Windows user and is not shown again."
        : $"No service token is saved for {gateway.Host}.";

    /// <summary>What the Test button says for a probe result.</summary>
    public static string Describe(GatewayProbeResult r) => r.Reach switch
    {
        GatewayReach.Reachable => (r.TokenSent ? "Reached the gateway with the saved service token. " : "Reached the gateway. ") + r.Message,
        _ => r.Message
    };
}
