namespace Hikyaku.Core;

public enum Surface { Navigation, Conversation, Search, Badge, Toast, Sound, DeepLink, Inbox, NeedsYou, Cron, Rooms }

/// <summary>Single choke point every user-visible surface must pass through for a profile.</summary>
public sealed class NotificationGate(ProfileSelectionService selection)
{
    /// <param name="surface">Surface being rendered; all surfaces currently share one rule.</param>
    /// <param name="key">Profile the event belongs to; null (unattributable) is denied.</param>
    public bool Allow(Surface surface, ProfileKey? key)
        => key is { } k && selection.IsAllowed(k);
}
