namespace Hikyaku.Core;

/// <summary>
/// Which of the ticked profiles the app is working with (the sidebar's profile switcher): new chats, the chat list and every
/// page use it. The saved choice wins while it is still ticked; otherwise the gateway's own "default" profile; otherwise the
/// first in a stable order. Never a profile that is not ticked.
/// </summary>
public static class ActiveProfile
{
    public const string GatewayDefault = "default";

    public static ProfileKey? Pick(IEnumerable<ProfileKey> selected, string? savedProfileId)
    {
        var list = selected.OrderBy(k => k.ToString(), StringComparer.Ordinal).ToList();
        if (list.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(savedProfileId) && list.FirstOrDefault(k => k.ProfileId == savedProfileId) is { ProfileId.Length: > 0 } saved)
            return saved;
        if (list.FirstOrDefault(k => k.ProfileId == GatewayDefault) is { ProfileId.Length: > 0 } def)
            return def;
        return list[0];
    }

    /// <summary>The name to show for a profile: the gateway's display name when known, else its id.</summary>
    public static string Label(ProfileKey key, IReadOnlyDictionary<string, string>? displayNames) =>
        displayNames is not null && displayNames.TryGetValue(key.ProfileId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name.Trim()
            : key.ProfileId;
}
