namespace Hikyaku.Core;

/// <summary>Stable identity of an agent profile: the (gateway, profile) pair. Never the display name.</summary>
public readonly record struct ProfileKey
{
    public string GatewayId { get; }
    public string ProfileId { get; }

    public ProfileKey(string gatewayId, string profileId)
    {
        if (string.IsNullOrWhiteSpace(gatewayId)) throw new ArgumentException("Gateway id is required.", nameof(gatewayId));
        if (string.IsNullOrWhiteSpace(profileId)) throw new ArgumentException("Profile id is required.", nameof(profileId));
        GatewayId = gatewayId.Trim();
        ProfileId = profileId.Trim();
    }

    public override string ToString() => $"{GatewayId}/{ProfileId}";
}
