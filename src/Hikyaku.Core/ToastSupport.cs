using System.Text;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

/// <summary>
/// Per-user registration of the app's notification identity (an AppUserModelID), without which an unpackaged desktop app
/// cannot show toasts. Implementations touch only the current user's settings and only the single key for <c>appId</c>.
/// </summary>
public interface IToastRegistry
{
    bool IsRegistered(string appId);
    void Register(string appId, string displayName);
    void Unregister(string appId);
}

/// <summary>A valid app id is a short dotted token, so it can never be used to address a different registry path.</summary>
public static partial class ToastAppId
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? id) => id is not null && Pattern().IsMatch(id);

    public static string Require(string? id) =>
        IsValid(id) ? id! : throw new ArgumentException("Invalid notification app id.", nameof(id));
}

/// <summary>Builds the toast XML from untrusted text: control characters removed, length bounded, everything escaped.</summary>
public static class ToastXml
{
    public const int MaxTitle = 120;
    public const int MaxBody = 300;
    public const int MaxLaunch = 256;

    public static string Build(string? title, string? body, string? launchArgs)
    {
        var t = Clean(title, MaxTitle);
        if (t.Length == 0) t = "Hikyaku";
        var b = Clean(body, MaxBody);
        var sb = new StringBuilder("<toast");
        if (!string.IsNullOrEmpty(launchArgs))
            sb.Append(" launch=\"").Append(System.Security.SecurityElement.Escape(Clean(launchArgs, MaxLaunch))).Append('"');
        sb.Append("><visual><binding template=\"ToastGeneric\"><text>")
          .Append(System.Security.SecurityElement.Escape(t)).Append("</text>");
        if (b.Length > 0) sb.Append("<text>").Append(System.Security.SecurityElement.Escape(b)).Append("</text>");
        sb.Append("</binding></visual></toast>");
        return sb.ToString();
    }

    /// <summary>Single line, no control characters, collapsed whitespace, truncated with an ellipsis.</summary>
    public static string Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        var space = false;
        foreach (var c in text)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
            if (sb.Length > max) break;
        }
        var s = sb.ToString();
        return s.Length > max ? s[..(max - 1)] + "…" : s;
    }
}

/// <summary>At most <c>maxPerWindow</c> notifications per key per window, so a burst of events is one toast, not a barrage.</summary>
public sealed class ToastThrottle(TimeSpan window, int maxPerWindow = 1, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _sent = new();
    private readonly object _gate = new();

    public bool TryAcquire(string key)
    {
        lock (_gate)
        {
            var now = _now();
            if (!_sent.TryGetValue(key, out var q)) _sent[key] = q = new Queue<DateTimeOffset>();
            while (q.Count > 0 && now - q.Peek() >= window) q.Dequeue();
            if (q.Count >= maxPerWindow) return false;
            q.Enqueue(now);
            return true;
        }
    }
}
