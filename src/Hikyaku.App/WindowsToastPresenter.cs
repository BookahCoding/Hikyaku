using System.Runtime.InteropServices;
using Hikyaku.Core;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Hikyaku.App;

/// <summary>
/// Per-user toast identity: <c>HKCU\Software\Classes\AppUserModelId\&lt;appId&gt;</c> with a display name, nothing else, and
/// only the one key for a validated id. No machine-wide change, no shortcut, no admin rights; <see cref="Unregister"/> removes it.
/// </summary>
public sealed class HkcuToastRegistry : IToastRegistry
{
    private const string Root = @"Software\Classes\AppUserModelId";

    public bool IsRegistered(string appId)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Root + "\\" + ToastAppId.Require(appId));
        return key?.GetValue("DisplayName") is string s && s.Length > 0;
    }

    public void Register(string appId, string displayName)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Root + "\\" + ToastAppId.Require(appId));
        key.SetValue("DisplayName", ToastXml.Clean(displayName, 64).Length > 0 ? ToastXml.Clean(displayName, 64) : "Hikyaku");
    }

    public void Unregister(string appId) =>
        Registry.CurrentUser.DeleteSubKeyTree(Root + "\\" + ToastAppId.Require(appId), throwOnMissingSubKey: false);
}

/// <summary>
/// Shows a Windows toast through the native API and reports what actually happened. "Observed" means the toast is in this app's
/// Action Center history afterwards; a <c>Show</c> call that returned is not enough. A blocked, unregistered or refused
/// toast is reported as such and never as delivered.
/// </summary>
public sealed class WindowsToastPresenter(string appId, IToastRegistry registry) : IToastPresenter
{
    public ToastDeliveryResult Show(string title, string body, string? launchArgs)
    {
        var step = "registry check";
        try
        {
            if (!registry.IsRegistered(appId))
                return new ToastDeliveryResult(ToastObservation.Unavailable,
                    "Notifications are not set up for this Windows user. Turn them on in Settings.");

            step = "create notifier";
            var notifier = ToastNotificationManager.CreateToastNotifier(appId);
            step = "read notification setting";
            var setting = ReadSetting(notifier);
            if (setting is { } blocked && blocked != NotificationSetting.Enabled)
                return new ToastDeliveryResult(ToastObservation.NotObserved, "Windows is not allowing notifications from Hikyaku: " + blocked + ".");

            step = "build toast";
            var doc = new XmlDocument();
            doc.LoadXml(ToastXml.Build(title, body, launchArgs));
            var tag = Guid.NewGuid().ToString("N");
            var toast = new ToastNotification(doc) { Tag = tag, Group = "hikyaku", ExpirationTime = DateTimeOffset.Now.AddHours(12) };
            step = "show";
            notifier.Show(toast);

            step = "read history";
            var present = ToastNotificationManager.History.GetHistory(appId).Any(t => t.Tag == tag);
            return present
                ? new ToastDeliveryResult(ToastObservation.Observed, "Toast is in the Action Center history.")
                : new ToastDeliveryResult(ToastObservation.NotObserved, "Show returned but the toast was not found in the history (Focus assist or Windows dropped it).");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            return new ToastDeliveryResult(ToastObservation.NotObserved,
                $"Toast failed at '{step}': {ex.GetType().Name} 0x{ex.HResult:X8}" + (string.IsNullOrWhiteSpace(ex.Message) ? "" : " — " + ex.Message.Trim()));
        }
    }

    /// <summary>
    /// Windows answers "element not found" for the setting of an identity it has not seen yet. That is "unknown", not "blocked":
    /// the toast is still attempted and the history check decides what is reported.
    /// </summary>
    private static NotificationSetting? ReadSetting(ToastNotifier notifier)
    {
        try { return notifier.Setting; }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490)) { return null; }
    }

    /// <summary>Removes this app's toasts from the Action Center (acceptance cleanup).</summary>
    public void ClearAll()
    {
        try { ToastNotificationManager.History.Clear(appId); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { /* nothing to clear */ }
    }
}
