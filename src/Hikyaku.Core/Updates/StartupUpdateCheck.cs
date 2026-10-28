namespace Hikyaku.Core;

/// <summary>
/// The one check at start-up (the maintainer chose it, 2026-10-28; Settings → Updates can turn it off). It is anonymous, says
/// nothing on any failure (offline, rate limited, no release yet), and only reports that a newer version exists:
/// downloading and installing still take the explicit clicks in Settings → Updates.
/// </summary>
public static class StartupUpdateCheck
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static async Task<UpdateRelease?> FindNewerAsync(Func<CancellationToken, Task<UpdateCheckResult>> check, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            return await check(cts.Token).ConfigureAwait(false) is UpdateAvailable a ? a.Release : null;
        }
        catch (Exception) { return null; }   // never an error dialog at start-up; Settings → Updates gives the reason
    }
}
