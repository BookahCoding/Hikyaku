namespace Hikyaku.Core;

/// <summary>Observation of an attempted Windows toast — never conflate sink accept with OS delivery.</summary>
public enum ToastObservation
{
    /// <summary>OS toast API reported success (or test presenter recorded a show).</summary>
    Observed,
    /// <summary>Attempted but the OS path did not confirm delivery.</summary>
    NotObserved,
    /// <summary>User disabled notifications in settings.</summary>
    SkippedDisabled,
    /// <summary>NotificationGate denied the profile/surface.</summary>
    DeniedByGate,
    /// <summary>Presenter unavailable (e.g. headless CI without toast COM).</summary>
    Unavailable
}

public sealed record ToastDeliveryResult(ToastObservation Observation, string Detail);

/// <summary>OS toast presenter. Implementations must not ring remote devices or touch personal chats in tests.</summary>
public interface IToastPresenter
{
    ToastDeliveryResult Show(string title, string body, string? launchArgs);
}

/// <summary>Records shows for tests / synthetic acceptance without claiming a real Action Center toast.</summary>
public sealed class RecordingToastPresenter : IToastPresenter
{
    private readonly List<(string Title, string Body, string? Args)> _shown = new();
    public IReadOnlyList<(string Title, string Body, string? Args)> Shown => _shown;
    public ToastObservation Result { get; set; } = ToastObservation.Observed;

    public ToastDeliveryResult Show(string title, string body, string? launchArgs)
    {
        _shown.Add((title, body, launchArgs));
        return new ToastDeliveryResult(Result, Result == ToastObservation.Observed
            ? "recording-presenter:observed"
            : "recording-presenter:" + Result);
    }
}

/// <summary>
/// Profile-gated toast path. Gate → optional user disable → presenter.
/// Does not notify when profile is cold/deselected/unknown; deep-links fail closed without selection.
/// </summary>
public sealed class ToastNotificationService
{
    private readonly NotificationGate _gate;
    private readonly Func<bool> _notificationsEnabled;
    private readonly IToastPresenter _presenter;
    private readonly List<ToastDeliveryResult> _history = new();
    private readonly object _lock = new();

    public ToastNotificationService(NotificationGate gate, Func<bool> notificationsEnabled, IToastPresenter presenter)
    {
        _gate = gate;
        _notificationsEnabled = notificationsEnabled;
        _presenter = presenter;
    }

    public IReadOnlyList<ToastDeliveryResult> History
    {
        get { lock (_lock) return _history.ToArray(); }
    }

    public ToastDeliveryResult TryShow(GatedNotification notification, string? deepLink = null)
    {
        ToastDeliveryResult result;
        if (!_gate.Allow(notification.Surface, notification.Profile))
            result = new ToastDeliveryResult(ToastObservation.DeniedByGate, "gate denied");
        else if (!_notificationsEnabled())
            result = new ToastDeliveryResult(ToastObservation.SkippedDisabled, "user disabled notifications");
        else if (notification.Surface == Surface.DeepLink && notification.Profile is null)
            result = new ToastDeliveryResult(ToastObservation.DeniedByGate, "deep-link without profile");
        else
            result = _presenter.Show(notification.Title, notification.Body, deepLink);

        lock (_lock) _history.Add(result);
        return result;
    }
}
