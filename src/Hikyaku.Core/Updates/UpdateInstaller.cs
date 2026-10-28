using System.Diagnostics;
using System.Text;

namespace Hikyaku.Core;

/// <summary>
/// Download → verify → stage. Fail-closed: with no trusted publisher key nothing is even downloaded, and a package
/// is extracted only after its detached signature verifies for exactly this version. The download is deleted
/// afterwards; only the verified, extracted staging folder (with a marker) remains.
/// </summary>
public sealed class UpdateInstaller(HttpMessageHandler handler, ReleaseSignatureVerifier verifier, string workRoot)
{
    public const long MaxZipBytes = 150L << 20;
    public const long MaxSigBytes = 4096;

    public async Task<StagedUpdate> PrepareAsync(UpdateRelease release, IProgress<long>? progress, CancellationToken ct)
    {
        if (!verifier.IsConfigured)
            throw new UpdateException(UpdateFailure.NotTrusted,
                "Installing is disabled: this build has no trusted publisher key, so a download could not be verified.");

        var dir = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var zip = Path.Combine(dir, UpdateChecker.ZipName);
            var sigPath = Path.Combine(dir, UpdateChecker.SigName);
            var downloader = new UpdateDownloader(handler);
            await downloader.DownloadAsync(release.SigUrl, sigPath, MaxSigBytes, null, ct).ConfigureAwait(false);
            await downloader.DownloadAsync(release.ZipUrl, zip, MaxZipBytes, progress, ct).ConfigureAwait(false);

            var sig = Encoding.ASCII.GetString(await File.ReadAllBytesAsync(sigPath, ct).ConfigureAwait(false));
            if (verifier.Verify(release.Version, zip, sig) != SignatureResult.Ok)
                throw new UpdateException(UpdateFailure.NotTrusted, "The download's signature did not verify; it was discarded and not run.");

            var staging = Path.Combine(dir, "staged");
            UpdateStaging.Extract(zip, staging);
            StagedUpdate.WriteMarker(staging, release.Version);
            File.Delete(zip);
            File.Delete(sigPath);
            return new StagedUpdate(release.Version, staging);
        }
        catch
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
            throw;
        }
    }
}

/// <summary>
/// Swaps a verified staging folder into the install folder, transactionally. Runs from the NEW build (the staged
/// Hikyaku.exe), after the old process has exited. Replaced files are moved aside into a transient rollback folder,
/// restored if anything fails and deleted on success: there is no standing backup.
/// </summary>
public static class UpdateApplier
{
    public const string RollbackDir = ".update-old";
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(60);

    public static async Task ApplyAsync(string stagingDir, string installDir, int parentPid, TimeSpan? waitTimeout = null, CancellationToken ct = default)
    {
        if (StagedUpdate.ReadMarker(stagingDir) is null)
            throw new UpdateException(UpdateFailure.NotTrusted, "The staged update was not verified by this app.");
        if (!File.Exists(Path.Combine(installDir, UpdateStaging.ExeName)))
            throw new UpdateException(UpdateFailure.Failed, "The install folder does not contain " + UpdateStaging.ExeName + ".");

        await WaitForExitAsync(parentPid, waitTimeout ?? DefaultWait, ct).ConfigureAwait(false);

        var installRoot = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var rollbackRoot = Path.Combine(installRoot, RollbackDir);
        RecoverInterrupted(installRoot, rollbackRoot);

        var moved = new List<(string Original, string Aside)>();
        var created = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(stagingDir, file);
                if (rel == StagedUpdate.MarkerName) continue;
                var target = Path.GetFullPath(Path.Combine(installRoot, rel));
                if (!target.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase) || target.StartsWith(rollbackRoot, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException(UpdateFailure.Failed, "A staged file maps outside the install folder.");

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var aside = Path.Combine(rollbackRoot, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
                    File.Move(target, aside);
                    moved.Add((target, aside));
                }
                File.Copy(file, target, overwrite: false);
                created.Add(target);
            }
        }
        catch (Exception ex)
        {
            Rollback(moved, created);
            TryDelete(rollbackRoot);
            if (ex is UpdateException or OperationCanceledException) throw;
            throw new UpdateException(UpdateFailure.Failed, "The update could not be applied and was rolled back: " + ex.Message);
        }
        TryDelete(rollbackRoot);
    }

    private static async Task WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken ct)
    {
        if (pid <= 0) return;
        Process proc;
        try { proc = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; } // already gone
        using (proc)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new UpdateException(UpdateFailure.Busy, "Hikyaku is still running. Close it and try again.");
            }
        }
    }

    private static void Rollback(List<(string Original, string Aside)> moved, List<string> created)
    {
        foreach (var c in Enumerable.Reverse(created)) { try { File.Delete(c); } catch { /* best effort */ } }
        foreach (var (original, aside) in Enumerable.Reverse(moved))
        {
            try { if (File.Exists(aside)) File.Move(aside, original, overwrite: true); } catch { /* best effort */ }
        }
    }

    /// <summary>A crash mid-apply leaves the old files aside: put them back before starting again.</summary>
    private static void RecoverInterrupted(string installRoot, string rollbackRoot)
    {
        if (!Directory.Exists(rollbackRoot)) return;
        foreach (var f in Directory.EnumerateFiles(rollbackRoot, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(installRoot, Path.GetRelativePath(rollbackRoot, f));
            try { File.Move(f, target, overwrite: true); } catch { /* leave for the next attempt */ }
        }
        TryDelete(rollbackRoot);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }
}
