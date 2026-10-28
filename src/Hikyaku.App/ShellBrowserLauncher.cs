using System.Diagnostics;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>
/// Opens the system default browser, but only at the configured gateway's sign-in page
/// (<see cref="BrowserLaunchPolicy"/>). Called only from an explicit button press.
/// </summary>
internal static class ShellBrowserLauncher
{
    public static void Open(Uri url, Uri gateway)
    {
        if (!BrowserLaunchPolicy.IsAllowed(url, gateway))
            throw new InvalidOperationException("Refusing to open a page outside the configured gateway.");
        Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }
}
