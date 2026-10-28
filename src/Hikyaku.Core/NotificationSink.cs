namespace Hikyaku.Core;

public sealed record GatedNotification(ProfileKey? Profile, string Title, string Body, Surface Surface = Surface.Toast);

/// <summary>
/// Boundary that accepts notifications only through <see cref="NotificationGate"/>.
/// Does not deliver Windows toasts; that remains unclaimed until a real toast path is tested.
/// </summary>
public sealed class NotificationSink(NotificationGate gate)
{
    private readonly List<GatedNotification> _accepted = new();
    private readonly List<GatedNotification> _denied = new();
    private readonly object _lock = new();

    public IReadOnlyList<GatedNotification> Accepted { get { lock (_lock) return _accepted.ToArray(); } }
    public IReadOnlyList<GatedNotification> Denied { get { lock (_lock) return _denied.ToArray(); } }

    public bool TryAccept(GatedNotification notification)
    {
        if (!gate.Allow(notification.Surface, notification.Profile))
        {
            lock (_lock) _denied.Add(notification);
            return false;
        }
        lock (_lock) _accepted.Add(notification);
        return true;
    }
}
