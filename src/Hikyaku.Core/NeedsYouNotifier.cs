namespace Hikyaku.Core;

/// <summary>
/// Announces new Needs-you requests as OS toasts. The text is only the kind of attention needed and the profile name: never the
/// command, question, secret prompt or variable name. It stays silent while the app is in front (the user is looking), coalesces
/// bursts through the throttle (every request is still in the Needs-you list), never announces a request twice, and a failing
/// OS call can never disturb request handling. Profile gating is the notify callback's job (the toast service applies it).
/// </summary>
public sealed class NeedsYouNotifier : IDisposable
{
    public const string Title = "Hermes needs you";

    private readonly NeedsYouService _needs;
    private readonly Func<ProfileKey, string, string, ToastDeliveryResult> _notify;
    private readonly Func<bool> _suppressWhile;
    private readonly ToastThrottle _throttle;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    public NeedsYouNotifier(NeedsYouService needs, Func<ProfileKey, string, string, ToastDeliveryResult> notify,
        Func<bool> suppressWhile, ToastThrottle? throttle = null)
    {
        _needs = needs;
        _notify = notify;
        _suppressWhile = suppressWhile;
        _throttle = throttle ?? new ToastThrottle(TimeSpan.FromSeconds(15));
        lock (_gate) foreach (var r in needs.Pending) _seen.Add(r.Id);   // already pending when we started: not news
        needs.Changed += OnChanged;
    }

    public ToastDeliveryResult? LastResult { get; private set; }

    public void Dispose()
    {
        _disposed = true;
        _needs.Changed -= OnChanged;
    }

    private void OnChanged()
    {
        if (_disposed) return;
        List<NeedsYouRequest> fresh;
        lock (_gate)
        {
            var pending = _needs.Pending;
            var ids = pending.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            _seen.IntersectWith(ids);                                      // answered or withdrawn ids are forgotten
            fresh = pending.Where(p => _seen.Add(p.Id)).ToList();
        }
        if (fresh.Count == 0 || _suppressWhile()) return;                   // in front: seen, so not announced late either
        Announce(fresh);
    }

    /// <summary>Announces <paramref name="requests"/> (grouped per profile) subject to the throttle.</summary>
    public void Announce(IReadOnlyList<NeedsYouRequest> requests)
    {
        foreach (var group in requests.Where(r => r.Profile is not null).GroupBy(r => r.Profile!.Value))
        {
            if (!_throttle.TryAcquire("needs-you")) return;
            var body = group.Count() == 1
                ? $"{KindText(group.First())} · {group.Key.ProfileId}"
                : $"{group.Count()} requests waiting · {group.Key.ProfileId}";
            try { LastResult = _notify(group.Key, Title, body); }
            catch (Exception ex) { LastResult = new ToastDeliveryResult(ToastObservation.NotObserved, "notify failed: " + ex.Message); }
        }
    }

    private static string KindText(NeedsYouRequest r) => r switch
    {
        ApprovalRequest => "Approval requested",
        ClarifyRequest => "A question is waiting",
        _ => "Input needed"
    };
}
