using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

public sealed record GitPushPreview(
    bool Ok,
    string? Error = null,
    string? Branch = null,
    string? LocalSha = null,
    string? RemoteName = null,
    string? RemoteUrlDisplay = null,
    /// <summary>Validated effective push URL (no credentials). Used for push/ls-remote/reconcile/PR binding.</summary>
    string? EffectivePushUrl = null,
    string? UpstreamRef = null,
    string? Fingerprint = null,
    string? DurableKey = null,
    string WarningCopy = "",
    bool NetworkGrantRequired = true);

public sealed record GitPushResult(
    bool Ok,
    string? Error = null,
    bool Published = false,
    string? LocalSha = null,
    string? RemoteShaVerified = null,
    bool OutcomeUncertain = false,
    string? Detail = null,
    string? VerifiedPushUrlDisplay = null);

/// <summary>
/// Guided push: preview with exact PUSH destination, explicit confirm, revalidate, trusted ArgumentList,
/// no force/delete/mirror/all/tags, ls-remote verification against push URL, uncertain outcomes block retry.
/// </summary>
public sealed class GitPushOps(string gitExecutable = "git")
{
    public const string PublicationWarning =
        "This publishes the named branch to the remote. Others with access to that remote can see it. " +
        "Save checkpoint never pushes. Force, delete, mirror, --all, and --tags are not offered.";

    public const string UncertainRetryCopy =
        "A previous push outcome was uncertain. Reconcile with the remote (exact ref readback on the push destination) before retrying. " +
        "Hikyaku will not silently repeat a push.";

    private static readonly HashSet<string> UncertainFingerprints = new(StringComparer.Ordinal);
    private static readonly object FingerprintGate = new();
    private static readonly Regex ConflictCode = new(@"^(UU|AA|DD|AU|UA|DU|UD)$", RegexOptions.Compiled);

    public async Task<GitPushPreview> PreviewAsync(
        string workingTree,
        PathBoundary grantBoundary,
        string? preferredRemote = null,
        CancellationToken ct = default)
    {
        var gate = await PreflightAsync(workingTree, grantBoundary, ct).ConfigureAwait(false);
        if (!gate.Ok)
            return new GitPushPreview(false, gate.Error, WarningCopy: PublicationWarning);

        var trust = await GitSafeInvocation.AssertNoUntrustedCommandOverridesAsync(gitExecutable, workingTree, ct)
            .ConfigureAwait(false);
        if (!trust.Ok)
            return new GitPushPreview(false, trust.Error, WarningCopy: PublicationWarning);

        var branchRun = await RunAsync(workingTree, ["rev-parse", "--abbrev-ref", "HEAD"], ct).ConfigureAwait(false);
        if (!branchRun.Ok)
            return new GitPushPreview(false, branchRun.Error ?? "Could not read branch.", WarningCopy: PublicationWarning);
        var branch = branchRun.Text.Trim();
        if (branch is "HEAD" || string.IsNullOrEmpty(branch))
            return new GitPushPreview(false, "Detached HEAD is not supported for guided push.", WarningCopy: PublicationWarning);
        if (!GitRemoteSafety.IsSafeBranchName(branch))
            return new GitPushPreview(false, "Branch name rejected for guided push.", WarningCopy: PublicationWarning);

        var shaRun = await RunAsync(workingTree, ["rev-parse", "HEAD"], ct).ConfigureAwait(false);
        if (!shaRun.Ok || string.IsNullOrWhiteSpace(shaRun.Text))
            return new GitPushPreview(false, shaRun.Error ?? "Could not read HEAD.", WarningCopy: PublicationWarning);
        var localSha = shaRun.Text.Trim();

        var snap = await new GitChangesReader(gitExecutable).ReadAsync(workingTree, ct).ConfigureAwait(false);
        if (!snap.Ok)
            return new GitPushPreview(false, snap.Error ?? "Could not read index.", WarningCopy: PublicationWarning);
        foreach (var e in snap.Entries)
        {
            if (ConflictCode.IsMatch(e.StatusCode))
                return new GitPushPreview(false, $"Conflicted path present ({e.Path}). Resolve before push.", WarningCopy: PublicationWarning);
        }

        var remotes = await ListRemotesAsync(workingTree, ct).ConfigureAwait(false);
        if (!remotes.Ok)
            return new GitPushPreview(false, remotes.Error, WarningCopy: PublicationWarning);
        if (remotes.Entries.Count == 0)
            return new GitPushPreview(false, "No remotes configured. Add a remote before guided push.", WarningCopy: PublicationWarning);

        var rewrite = await DetectUnsafeRewritesAsync(workingTree, ct).ConfigureAwait(false);
        if (!rewrite.Ok)
            return new GitPushPreview(false, rewrite.Error, WarningCopy: PublicationWarning);

        string remoteName;
        if (!string.IsNullOrWhiteSpace(preferredRemote))
        {
            if (!GitRemoteSafety.IsSafeRemoteName(preferredRemote))
                return new GitPushPreview(false, "Remote name rejected.", WarningCopy: PublicationWarning);
            remoteName = preferredRemote.Trim();
            if (!remotes.Entries.Any(r => string.Equals(r.Name, remoteName, StringComparison.Ordinal)))
                return new GitPushPreview(false, "Preferred remote is not configured.", WarningCopy: PublicationWarning);
        }
        else
        {
            remoteName = remotes.Entries.FirstOrDefault(r => r.Name == "origin")?.Name
                         ?? remotes.Entries[0].Name;
        }

        var dest = remotes.Entries.First(r => r.Name == remoteName);
        if (dest.PushUrls.Count > 1)
            return new GitPushPreview(false,
                "Multiple pushurl destinations are not supported by guided push (ambiguous publication target).",
                WarningCopy: PublicationWarning);

        var pushUrl = dest.PushUrls.Count == 1 ? dest.PushUrls[0] : dest.FetchUrl;
        if (!GitRemoteSafety.TryValidateRemoteUrl(pushUrl, out var urlErr, out var display))
            return new GitPushPreview(false, urlErr ?? "Remote URL rejected.", WarningCopy: PublicationWarning);
        if (!GitRemoteSafety.TryValidateRemoteUrl(dest.FetchUrl, out var fetchErr, out _))
            return new GitPushPreview(false, fetchErr ?? "Fetch remote URL rejected.", WarningCopy: PublicationWarning);

        var upstream = $"{remoteName}/{branch}";
        var indexFp = IndexFingerprint(snap);
        var fingerprint = MakeFingerprint(workingTree, grantBoundary.Root, branch, localSha, remoteName, pushUrl, indexFp);
        var durable = GuidedPushUncertaintyStore.MakeDurableKey(workingTree, grantBoundary.Root, remoteName, pushUrl, branch);

        return new GitPushPreview(
            true,
            Branch: branch,
            LocalSha: localSha,
            RemoteName: remoteName,
            RemoteUrlDisplay: display,
            EffectivePushUrl: pushUrl,
            UpstreamRef: upstream,
            Fingerprint: fingerprint,
            DurableKey: durable,
            WarningCopy: PublicationWarning);
    }

    public async Task<GitPushResult> PushAsync(
        string workingTree,
        PathBoundary grantBoundary,
        string previewFingerprint,
        bool confirmed,
        bool networkGranted,
        string? preferredRemote = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (!confirmed)
            return new GitPushResult(false, "Push requires explicit confirmation naming the remote destination.");
        if (!networkGranted)
            return new GitPushResult(false, "Network publish grant is required (separate from folder grant).");
        if (string.IsNullOrWhiteSpace(previewFingerprint))
            return new GitPushResult(false, "Push requires a preview fingerprint.");

        var preview = await PreviewAsync(workingTree, grantBoundary, preferredRemote, ct).ConfigureAwait(false);
        if (!preview.Ok)
            return new GitPushResult(false, preview.Error ?? "Push preview failed.");
        if (!string.Equals(preview.Fingerprint, previewFingerprint, StringComparison.Ordinal))
            return new GitPushResult(false,
                "Repository, branch, remote, or index changed after preview — push refused. Preview again.");

        // Durable gate (survives index/fingerprint change and process restart) + legacy fingerprint gate.
        if ((!string.IsNullOrEmpty(preview.DurableKey) && GuidedPushUncertaintyStore.Contains(preview.DurableKey)) ||
            FingerprintUncertain(previewFingerprint))
        {
            return new GitPushResult(false, UncertainRetryCopy, OutcomeUncertain: true);
        }

        // Revalidate push URL immediately before network action.
        if (!GitRemoteSafety.TryValidateRemoteUrl(preview.EffectivePushUrl, out var revalErr, out var display))
            return new GitPushResult(false, revalErr ?? "Push URL rejected on revalidation.");

        var refspec = $"refs/heads/{preview.Branch}:refs/heads/{preview.Branch}";
        // Push to the effective PUSH URL (not remote name / fetch URL).
        var args = new[] { "push", preview.EffectivePushUrl!, refspec };

        var run = await RunPushAsync(workingTree, args, timeout ?? TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (run.Uncertain)
        {
            MarkUncertain(preview);
            return new GitPushResult(false,
                run.Error ?? "Push outcome uncertain (timeout or interrupted).",
                LocalSha: preview.LocalSha,
                OutcomeUncertain: true);
        }
        if (!run.Ok)
        {
            return new GitPushResult(false, run.Error ?? "git push failed.", LocalSha: preview.LocalSha);
        }

        var verify = await VerifyRemoteRefAsync(workingTree, preview.EffectivePushUrl!, preview.Branch!, preview.LocalSha!, ct)
            .ConfigureAwait(false);
        if (!verify.Ok)
        {
            MarkUncertain(preview);
            return new GitPushResult(false,
                verify.Error ?? "Could not verify remote ref after push.",
                LocalSha: preview.LocalSha,
                OutcomeUncertain: true,
                Detail: "Push may have succeeded but remote readback failed — reconcile before retry.");
        }

        ClearUncertain(preview);
        return new GitPushResult(
            true,
            Published: true,
            LocalSha: preview.LocalSha,
            RemoteShaVerified: verify.RemoteSha,
            VerifiedPushUrlDisplay: display,
            Detail: $"Published {preview.Branch} → {preview.RemoteName} ({display}). Remote SHA verified on push destination.");
    }

    /// <summary>Clear uncertain gate after operator-driven reconciliation (tests / explicit reconcile).</summary>
    public static void ClearUncertainOutcome(string fingerprint)
    {
        lock (FingerprintGate) UncertainFingerprints.Remove(fingerprint);
    }

    public static bool HasUncertainOutcome(string fingerprint)
    {
        lock (FingerprintGate) return UncertainFingerprints.Contains(fingerprint);
    }

    /// <summary>Test helper: simulate an uncertain prior push outcome without publishing.</summary>
    public static void MarkUncertainOutcome(string fingerprint)
    {
        lock (FingerprintGate) UncertainFingerprints.Add(fingerprint);
    }

    /// <summary>Mark durable + fingerprint uncertainty from a preview (tests / explicit).</summary>
    public static void MarkUncertainFromPreview(GitPushPreview preview)
    {
        MarkUncertain(preview);
    }

    public static bool HasDurableUncertain(string durableKey) =>
        GuidedPushUncertaintyStore.Contains(durableKey);

    public async Task<GitPushResult> ReconcileRemoteRefAsync(
        string workingTree,
        PathBoundary grantBoundary,
        string fingerprint,
        string remoteName,
        string branch,
        string expectedSha,
        CancellationToken ct = default)
    {
        var gate = await PreflightAsync(workingTree, grantBoundary, ct).ConfigureAwait(false);
        if (!gate.Ok) return new GitPushResult(false, gate.Error);
        if (!GitRemoteSafety.IsSafeRemoteName(remoteName) || !GitRemoteSafety.IsSafeBranchName(branch))
            return new GitPushResult(false, "Remote or branch rejected.");

        var trust = await GitSafeInvocation.AssertNoUntrustedCommandOverridesAsync(gitExecutable, workingTree, ct)
            .ConfigureAwait(false);
        if (!trust.Ok) return new GitPushResult(false, trust.Error, OutcomeUncertain: true);

        var remotes = await ListRemotesAsync(workingTree, ct).ConfigureAwait(false);
        if (!remotes.Ok) return new GitPushResult(false, remotes.Error, OutcomeUncertain: true);
        var dest = remotes.Entries.FirstOrDefault(r => string.Equals(r.Name, remoteName, StringComparison.Ordinal));
        if (dest is null) return new GitPushResult(false, "Remote not configured.", OutcomeUncertain: true);
        if (dest.PushUrls.Count > 1)
            return new GitPushResult(false, "Multiple pushurl destinations — reconcile refused.", OutcomeUncertain: true);

        var pushUrl = dest.PushUrls.Count == 1 ? dest.PushUrls[0] : dest.FetchUrl;
        if (!GitRemoteSafety.TryValidateRemoteUrl(pushUrl, out var urlErr, out var display))
            return new GitPushResult(false, urlErr ?? "Push URL rejected.", OutcomeUncertain: true);

        var verify = await VerifyRemoteRefAsync(workingTree, pushUrl, branch, expectedSha, ct).ConfigureAwait(false);
        if (!verify.Ok)
            return new GitPushResult(false, verify.Error, OutcomeUncertain: true, LocalSha: expectedSha);

        var durable = GuidedPushUncertaintyStore.MakeDurableKey(workingTree, grantBoundary.Root, remoteName, pushUrl, branch);
        GuidedPushUncertaintyStore.Clear(durable);
        ClearUncertainOutcome(fingerprint);
        return new GitPushResult(true, Published: true, LocalSha: expectedSha, RemoteShaVerified: verify.RemoteSha,
            VerifiedPushUrlDisplay: display,
            Detail: "Remote ref reconciled on push destination; retry gate cleared.");
    }

    private static void MarkUncertain(GitPushPreview preview)
    {
        if (!string.IsNullOrEmpty(preview.DurableKey))
            GuidedPushUncertaintyStore.Mark(preview.DurableKey);
        if (!string.IsNullOrEmpty(preview.Fingerprint))
            MarkUncertainOutcome(preview.Fingerprint);
    }

    private static void ClearUncertain(GitPushPreview preview)
    {
        if (!string.IsNullOrEmpty(preview.DurableKey))
            GuidedPushUncertaintyStore.Clear(preview.DurableKey);
        if (!string.IsNullOrEmpty(preview.Fingerprint))
            ClearUncertainOutcome(preview.Fingerprint);
    }

    private static bool FingerprintUncertain(string fingerprint)
    {
        lock (FingerprintGate) return UncertainFingerprints.Contains(fingerprint);
    }

    private async Task<(bool Ok, string? RemoteSha, string? Error)> VerifyRemoteRefAsync(
        string workingTree,
        string pushUrl,
        string branch,
        string expectedSha,
        CancellationToken ct)
    {
        if (!GitRemoteSafety.TryValidateRemoteUrl(pushUrl, out var err, out _))
            return (false, null, err ?? "Push URL rejected before ls-remote.");

        // Exact-ref readback against the PUSH destination URL (never fetch remote name alone).
        var run = await RunAsync(workingTree, ["ls-remote", pushUrl, $"refs/heads/{branch}"], ct,
            promptNever: true).ConfigureAwait(false);
        if (!run.Ok)
            return (false, null, run.Error ?? "ls-remote failed.");

        var line = run.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrEmpty(line))
            return (false, null, "Remote ref missing after push.");

        var parts = line.Split('\t', 2);
        var remoteSha = parts[0].Trim();
        if (!string.Equals(remoteSha, expectedSha, StringComparison.OrdinalIgnoreCase))
            return (false, remoteSha, $"Remote SHA mismatch (expected {expectedSha}, got {remoteSha}).");
        return (true, remoteSha, null);
    }

    private static Task<(bool Ok, string? Error)> PreflightAsync(string workingTree, PathBoundary grantBoundary, CancellationToken ct)
    {
        _ = ct;
        if (string.IsNullOrWhiteSpace(workingTree) || !Directory.Exists(workingTree))
            return Task.FromResult<(bool, string?)>((false, "Working tree path missing."));
        if (!PathBoundary.AreSameRoot(grantBoundary.Root, workingTree) &&
            !IsUnderOrEqual(workingTree, grantBoundary.Root))
            return Task.FromResult<(bool, string?)>((false, "Git root is outside the folder grant."));
        if (!Directory.Exists(Path.Combine(workingTree, ".git")) &&
            !File.Exists(Path.Combine(workingTree, ".git")))
            return Task.FromResult<(bool, string?)>((false, "Not a Git repository."));
        return Task.FromResult<(bool, string?)>((true, null));
    }

    private async Task<(bool Ok, IReadOnlyList<RemoteEntry> Entries, string? Error)> ListRemotesAsync(
        string workingTree, CancellationToken ct)
    {
        var run = await RunAsync(workingTree, ["remote", "-v"], ct).ConfigureAwait(false);
        if (!run.Ok) return (false, [], run.Error);

        var map = new Dictionary<string, RemoteEntry>(StringComparer.Ordinal);
        using var reader = new StringReader(run.Text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var parts = Regex.Split(line.Trim(), @"\s+");
            if (parts.Length < 3) continue;
            var name = parts[0];
            var url = parts[1];
            var kind = parts[2].Trim('(', ')');
            if (!GitRemoteSafety.IsSafeRemoteName(name))
                return (false, [], "Remote name rejected: " + name);
            if (!map.TryGetValue(name, out var entry))
            {
                entry = new RemoteEntry(name);
                map[name] = entry;
            }
            if (kind.Equals("fetch", StringComparison.OrdinalIgnoreCase))
                entry.FetchUrl = url;
            else if (kind.Equals("push", StringComparison.OrdinalIgnoreCase))
                entry.PushUrls.Add(url);
        }

        var pushurls = await RunAsync(workingTree, ["config", "--get-regexp", @"^remote\..*\.pushurl$"], ct,
            safetyOverrides: false).ConfigureAwait(false);
        if (pushurls.Ok && !string.IsNullOrWhiteSpace(pushurls.Text))
        {
            using var pr = new StringReader(pushurls.Text);
            while ((line = pr.ReadLine()) is not null)
            {
                var sp = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (sp.Length < 2) continue;
                var key = sp[0];
                var m = Regex.Match(key, @"^remote\.(.+)\.pushurl$");
                if (!m.Success) continue;
                var name = m.Groups[1].Value;
                if (!map.TryGetValue(name, out var entry))
                {
                    entry = new RemoteEntry(name);
                    map[name] = entry;
                }
                if (!entry.PushUrls.Contains(sp[1], StringComparer.Ordinal))
                    entry.PushUrls.Add(sp[1]);
            }
        }

        return (true, map.Values.Where(e => !string.IsNullOrEmpty(e.FetchUrl)).ToList(), null);
    }

    private async Task<(bool Ok, string? Error)> DetectUnsafeRewritesAsync(string workingTree, CancellationToken ct)
    {
        var instead = await RunAsync(workingTree, ["config", "--get-regexp", @"^url\..*\.insteadof$"], ct,
            safetyOverrides: false).ConfigureAwait(false);
        if (instead.Ok && !string.IsNullOrWhiteSpace(instead.Text))
            return (false, "url.*.insteadOf is configured — guided push refuses rewritten remotes. " + GitRemoteSafety.LimitsCopy);

        var pushInstead = await RunAsync(workingTree, ["config", "--get-regexp", @"^url\..*\.pushinsteadof$"], ct,
            safetyOverrides: false).ConfigureAwait(false);
        if (pushInstead.Ok && !string.IsNullOrWhiteSpace(pushInstead.Text))
            return (false, "url.*.pushInsteadOf is configured — guided push refuses rewritten remotes. " + GitRemoteSafety.LimitsCopy);

        return (true, null);
    }

    private static string IndexFingerprint(GitSnapshot snap)
    {
        var sb = new StringBuilder();
        foreach (var e in snap.Entries.OrderBy(x => x.Path, StringComparer.Ordinal))
            sb.Append(e.StatusCode).Append('|').Append(e.Path).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string MakeFingerprint(
        string workingTree, string grantRoot, string branch, string sha, string remote, string url, string indexFp)
    {
        var raw = string.Join('\u001f',
            Path.GetFullPath(workingTree),
            Path.GetFullPath(grantRoot),
            branch, sha, remote, url, indexFp);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private async Task<(bool Ok, bool Uncertain, string? Error)> RunPushAsync(
        string workingTree, string[] gitArgs, TimeSpan timeout, CancellationToken ct)
    {
        var psi = BuildPsi(workingTree, gitArgs, safetyOverrides: true, promptNever: true, forGuidedPush: true);
        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p is null) return (false, false, "Could not start git.");
            var stdoutTask = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await KillAndDrainAsync(p, stdoutTask, stderrTask).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    return (false, true, "Push cancelled — outcome uncertain; reconcile before retry.");
                return (false, true, "Push timed out — outcome uncertain; reconcile before retry.");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            _ = stdout;
            if (p.ExitCode != 0)
            {
                var err = string.IsNullOrWhiteSpace(stderr) ? $"git push exited {p.ExitCode}" : stderr.Trim();
                return (false, false, err);
            }
            return (true, false, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (p is not null)
                await KillAndDrainAsync(p, null, null).ConfigureAwait(false);
            return (false, true, "Push cancelled — outcome uncertain; reconcile before retry.");
        }
        catch (Exception ex)
        {
            if (p is not null)
                await KillAndDrainAsync(p, null, null).ConfigureAwait(false);
            return (false, false, ex.Message);
        }
        finally
        {
            p?.Dispose();
        }
    }

    private static async Task KillAndDrainAsync(Process p, Task<string>? stdoutTask, Task<string>? stderrTask)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        try { if (stdoutTask is not null) await stdoutTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* drain best-effort */ }
        try { if (stderrTask is not null) await stderrTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* drain best-effort */ }
        try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* best-effort */ }
    }

    private async Task<(bool Ok, string Text, string? Error)> RunAsync(
        string workingTree,
        string[] gitArgs,
        CancellationToken ct,
        bool safetyOverrides = true,
        bool promptNever = false)
    {
        var psi = BuildPsi(workingTree, gitArgs, safetyOverrides, promptNever, forGuidedPush: false);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (false, "", "Could not start git.");
            var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (p.ExitCode != 0)
                return (false, stdout, string.IsNullOrWhiteSpace(stderr) ? $"git exited {p.ExitCode}" : stderr.Trim());
            return (true, stdout, null);
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    private ProcessStartInfo BuildPsi(
        string workingTree, string[] gitArgs, bool safetyOverrides, bool promptNever, bool forGuidedPush)
    {
        var psi = new ProcessStartInfo(gitExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        GitSafeInvocation.ApplyPinnedEnvironment(psi, promptNever);

        if (safetyOverrides)
            GitSafeInvocation.ApplySafetyArguments(psi, forGuidedPush);

        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(workingTree);
        foreach (var a in gitArgs)
            psi.ArgumentList.Add(a);
        return psi;
    }

    private static bool IsUnderOrEqual(string path, string root)
    {
        var p = TrimSlash(Path.GetFullPath(path));
        var r = TrimSlash(Path.GetFullPath(root));
        if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase)) return true;
        return p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimSlash(string p) =>
        p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private sealed class RemoteEntry(string name)
    {
        public string Name { get; } = name;
        public string FetchUrl { get; set; } = "";
        public List<string> PushUrls { get; } = new();
    }
}
