using System.IO;
using System.Windows;

namespace Hikyaku.App;

public partial class App : Application
{
    /// <summary>Per-user data folder; override with HIKYAKU_DATA_DIR (used by the smoke test for isolation).</summary>
    public static string DataDir { get; } =
        Environment.GetEnvironmentVariable("HIKYAKU_DATA_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hikyaku");

    /// <summary>Downloaded + verified update packages. Local (not roaming) data; override follows HIKYAKU_DATA_DIR.</summary>
    public static string UpdateWorkDir { get; } =
        Environment.GetEnvironmentVariable("HIKYAKU_DATA_DIR") is { Length: > 0 } d2
            ? Path.Combine(d2, "updates")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hikyaku", "updates");

    public const string UpdateRepo = "BookahCoding/Hikyaku";

    /// <summary>The per-user notification identity registered (with consent) so Windows will show this app's toasts.</summary>
    public const string ToastAppId = "Hikyaku.Desktop";

    public static bool DemoMode { get; private set; }
    public static bool SmokeMode { get; private set; }
    /// <summary>Synthetic UI screenshots + toast observation (forces demo transport; not live).</summary>
    public static bool UiAcceptanceMode { get; private set; }

    static App()
    {
        // Crisp text and edges in every window: text snapped to pixels like the rest of Windows (the default "Ideal" mode looked
        // soft at chat sizes, a live finding), and layout rounded to whole pixels so borders and icons do not straddle two.
        System.Windows.Media.TextOptions.TextFormattingModeProperty.OverrideMetadata(typeof(Window),
            new FrameworkPropertyMetadata(System.Windows.Media.TextFormattingMode.Display, FrameworkPropertyMetadataOptions.Inherits));
        FrameworkElement.UseLayoutRoundingProperty.OverrideMetadata(typeof(Window), new FrameworkPropertyMetadata(true));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // The verified new build re-runs itself as the file-swap helper: no window, no normal startup.
        if (e.Args.Contains("--apply-update"))
        {
            UpdateLauncher.RunApplyMode(e.Args);
            Shutdown();
            return;
        }
        DemoMode = e.Args.Contains("--demo");
        SmokeMode = e.Args.Contains("--smoke");
        UiAcceptanceMode = e.Args.Contains("--ui-acceptance");
        if (UiAcceptanceMode) DemoMode = true;
        base.OnStartup(e);
        if (!SmokeMode && !UiAcceptanceMode) UpdateLauncher.CleanStaleWork(UpdateWorkDir);
        new MainWindow().Show();
    }
}
