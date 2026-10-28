using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>
/// App-side glue for the updater: the running version, starting the verified new build as the file-swap helper,
/// and the helper mode itself. All real decisions (trust, extraction, transactional swap) live in Hikyaku.Core.
/// </summary>
internal static class UpdateLauncher
{
    public static ReleaseVersion CurrentVersion { get; } = ReleaseVersion.TryParseCurrent(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        out var v) ? v : default;

    public static string InstallDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>The swap needs to write the install folder (not the case under Program Files without elevation).</summary>
    public static bool InstallDirWritable()
    {
        try
        {
            var probe = Path.Combine(InstallDir, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Starts the staged (signature-verified) build as the helper. The caller then shuts this app down.</summary>
    public static void StartHelper(StagedUpdate staged)
    {
        var psi = new ProcessStartInfo(Path.Combine(staged.StagingDir, UpdateStaging.ExeName))
        {
            UseShellExecute = false,
            WorkingDirectory = staged.StagingDir
        };
        psi.ArgumentList.Add("--apply-update");
        psi.ArgumentList.Add("--pid"); psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add("--install"); psi.ArgumentList.Add(InstallDir);
        psi.ArgumentList.Add("--staging"); psi.ArgumentList.Add(staged.StagingDir);
        Process.Start(psi)?.Dispose();
    }

    /// <summary>Runs inside the helper process, before any window exists.</summary>
    public static void RunApplyMode(string[] args)
    {
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var staging = Arg("--staging");
        var install = Arg("--install");
        var pidText = Arg("--pid");
        string? error = null;
        try
        {
            if (staging is null || install is null || !int.TryParse(pidText, out var pid))
                throw new UpdateException(UpdateFailure.Failed, "Missing update arguments.");
            var work = Path.GetFullPath(App.UpdateWorkDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(staging).StartsWith(work, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException(UpdateFailure.NotTrusted, "The staging folder is not the app's update folder.");
            Task.Run(() => UpdateApplier.ApplyAsync(staging, install, pid)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            error = ex is UpdateException ? ex.Message : "The update failed: " + ex.Message;
        }

        if (error is not null)
            MessageBox.Show(error + "\n\nYour previous version was left in place.", "Hikyaku update",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        // Success runs the new build; failure rolled back, so this starts the previous one.
        try
        {
            var exe = install is null ? null : Path.Combine(install, UpdateStaging.ExeName);
            if (exe is not null && File.Exists(exe))
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = install })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { /* user can start it manually */ }
    }

    /// <summary>Removes prepared packages from earlier sessions; a prepared update is never auto-installed.</summary>
    public static void CleanStaleWork(string workDir)
    {
        try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>Opens the GitHub release page in the default browser, only if it is this repo's page for that tag.</summary>
    public static void OpenReleasePage(UpdateRelease release)
    {
        if (!GitHubReleasePolicy.IsReleasePage(release.PageUrl, App.UpdateRepo, release.Tag))
            throw new InvalidOperationException("Refusing to open a page that is not this project's release page.");
        Process.Start(new ProcessStartInfo(release.PageUrl.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }
}
