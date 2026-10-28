using System.Diagnostics;
using System.Text;
using Hikyaku.Core;
using Xunit;
using static Hikyaku.Tests.Checkpoint12bGitHelpers;

namespace Hikyaku.Tests;

/// <summary>
/// Checkpoint 12b: guided-push execution-boundary corrections.
/// Disposable bare remotes / markers only — never this checkout or real remotes.
/// </summary>
public class Checkpoint12bPushEndpointTests
{
    [Fact]
    public async Task Distinct_pushurl_verify_reads_push_destination_not_fetch()
    {
        var root = Path.Combine(Path.GetTempPath(), "hikyaku-cp12b-ep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fetchBare = Path.Combine(root, "fetch.git");
        var pushBare = Path.Combine(root, "push.git");
        var seed = Path.Combine(root, "seed");
        var repo = Path.Combine(root, "work");
        try
        {
            Directory.CreateDirectory(seed);
            Directory.CreateDirectory(repo);
            RunGit(root, "init", "--bare", fetchBare);
            RunGit(root, "init", "--bare", pushBare);

            // Seed fetch bare with a different tip than the work repo will push.
            InitCommit(seed, "fetch-only.txt", "FETCH_TIP\n", "fetch tip");
            RunGit(seed, "remote", "add", "origin", fetchBare);
            RunGit(seed, "push", "-u", "origin", "HEAD:main");
            var fetchSha = RunGitCapture(fetchBare, "rev-parse", "refs/heads/main").Trim();

            InitCommit(repo, "work.txt", "PUSH_TIP\n", "work tip");
            var localSha = RunGitCapture(repo, "rev-parse", "HEAD").Trim();
            Assert.NotEqual(fetchSha, localSha);

            RunGit(repo, "remote", "add", "origin", fetchBare);
            RunGit(repo, "remote", "set-url", "--push", "origin", pushBare);

            var boundary = new PathBoundary(Path.GetFullPath(repo));
            var ops = new GitPushOps();
            var preview = await ops.PreviewAsync(repo, boundary);
            Assert.True(preview.Ok, preview.Error);
            Assert.Equal(localSha, preview.LocalSha);
            // Display must name the effective PUSH endpoint, not the fetch bare.
            Assert.Contains(Path.GetFileName(pushBare), preview.RemoteUrlDisplay!.Replace('/', Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

            var pushed = await ops.PushAsync(repo, boundary, preview.Fingerprint!, confirmed: true, networkGranted: true);
            Assert.True(pushed.Ok, pushed.Error);
            Assert.True(pushed.Published);
            Assert.Equal(localSha, pushed.RemoteShaVerified);

            var pushBareSha = RunGitCapture(pushBare, "rev-parse", "refs/heads/main").Trim();
            var fetchBareSha = RunGitCapture(fetchBare, "rev-parse", "refs/heads/main").Trim();
            Assert.Equal(localSha, pushBareSha);
            Assert.Equal(fetchSha, fetchBareSha); // fetch bare must stay untouched
            Assert.NotEqual(fetchBareSha, pushed.RemoteShaVerified);
        }
        finally { TryDelete(root); }
    }
}

public class Checkpoint12bUntrustedConfigTests
{
    [Fact]
    public async Task Bang_credential_helper_and_transport_overrides_fail_closed_before_push()
    {
        var root = Path.Combine(Path.GetTempPath(), "hikyaku-cp12b-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var bare = Path.Combine(root, "bare.git");
        var repo = Path.Combine(root, "work");
        var helperMarker = Path.Combine(root, "HELPER_RAN.txt");
        var recvMarker = Path.Combine(root, "RECV_RAN.txt");
        try
        {
            Directory.CreateDirectory(repo);
            RunGit(root, "init", "--bare", bare);
            InitCommit(repo, "a.txt", "a\n", "init");
            RunGit(repo, "remote", "add", "origin", bare);

            var helperCmd = Path.Combine(root, "cred-helper.cmd");
            await File.WriteAllTextAsync(helperCmd,
                $"@echo off\r\necho HELPER> \"{helperMarker}\"\r\nexit /b 0\r\n");
            var recvCmd = Path.Combine(root, "fake-recv.cmd");
            await File.WriteAllTextAsync(recvCmd,
                $"@echo off\r\necho RAN> \"{recvMarker}\"\r\ngit receive-pack %*\r\n");

            RunGit(repo, "config", "credential.helper", "!" + helperCmd);
            RunGit(repo, "config", "core.sshCommand", "echo SSH_OVERRIDE");
            RunGit(repo, "config", "remote.origin.receivepack", recvCmd);

            var boundary = new PathBoundary(Path.GetFullPath(repo));
            var ops = new GitPushOps();
            var preview = await ops.PreviewAsync(repo, boundary);
            // Preview itself must refuse before any network/push process that could run overrides.
            Assert.False(preview.Ok, "untrusted command-executing config must fail closed");
            Assert.False(string.IsNullOrWhiteSpace(preview.Error));
            Assert.False(File.Exists(helperMarker));
            Assert.False(File.Exists(recvMarker));
        }
        finally { TryDelete(root); }
    }
}

public class Checkpoint12bHooksDirTests
{
    [Fact]
    public async Task Prepopulated_shared_hooks_dir_does_not_run_planted_hook()
    {
        var shared = Path.Combine(Path.GetTempPath(), "hikyaku-git-hooks-empty");
        Directory.CreateDirectory(shared);
        var markerRoot = Path.Combine(Path.GetTempPath(), "hikyaku-cp12b-hookm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(markerRoot);
        var marker = Path.Combine(markerRoot, "HOOK_RAN.txt");
        var planted = Path.Combine(shared, "pre-push");
        // Harmless marker — Windows Git may use sh; also plant without extension for POSIX-style.
        await File.WriteAllTextAsync(planted, "#!/bin/sh\necho HOOK > \"" + marker.Replace('\\', '/') + "\"\nexit 0\n");

        var root = Path.Combine(Path.GetTempPath(), "hikyaku-cp12b-hk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var bare = Path.Combine(root, "bare.git");
        var repo = Path.Combine(root, "work");
        try
        {
            Directory.CreateDirectory(repo);
            RunGit(root, "init", "--bare", bare);
            InitCommit(repo, "a.txt", "a\n", "init");
            RunGit(repo, "remote", "add", "origin", bare);

            var boundary = new PathBoundary(Path.GetFullPath(repo));
            var ops = new GitPushOps();
            var preview = await ops.PreviewAsync(repo, boundary);
            Assert.True(preview.Ok, preview.Error);
            var pushed = await ops.PushAsync(repo, boundary, preview.Fingerprint!, confirmed: true, networkGranted: true);
            Assert.True(pushed.Ok, pushed.Error);
            Assert.False(File.Exists(marker), "planted shared-dir pre-push must not execute");
        }
        finally
        {
            TryDelete(root);
            TryDelete(markerRoot);
            try { if (File.Exists(planted)) File.Delete(planted); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Local_ops_also_ignore_contaminated_shared_hooks_dir()
    {
        var shared = Path.Combine(Path.GetTempPath(), "hikyaku-git-hooks-empty");
        Directory.CreateDirectory(shared);
        var planted = Path.Combine(shared, "pre-commit");
        await File.WriteAllTextAsync(planted, "#!/bin/sh\nexit 1\n");

        var repo = Path.Combine(Path.GetTempPath(), "hikyaku-cp12b-lok-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(repo);
            InitCommit(repo, "a.txt", "a\n", "init");
            File.WriteAllText(Path.Combine(repo, "b.txt"), "b\n");
            var ops = new GitLocalOps();
            var boundary = new PathBoundary(Path.GetFullPath(repo));
            var stage = await ops.StageExactAsync(repo, boundary, ["b.txt"]);
            Assert.True(stage.Ok, stage.Error);
            // Save checkpoint must not fail due to planted pre-commit in shared empty dir.
            var commit = await ops.SaveCheckpointAsync(repo, boundary, ["b.txt"], "add b", confirmed: true);
            Assert.True(commit.Ok, commit.Error);
        }
        finally
        {
            TryDelete(repo);
            try { if (File.Exists(planted)) File.Delete(planted); } catch { /* best-effort */ }
        }
    }
}

public class Checkpoint12bUrlSafetyTests
{
    public static IEnumerable<object[]> MustReject()
    {
        yield return ["https://user@" + "github.com/acme/app.git", "username-only"];
        yield return ["https://github.com/acme/app.git?token=abc", "query"];
        yield return ["https://github.com/acme/app.git#frag", "fragment"];
        yield return ["evil:helper:path.git", "colon-helper-local"];
        yield return ["git::something.git", "remote-helper"];
        yield return ["https://github.com/acme/app.git/../../../etc", "path traversal-ish"];
        yield return ["file://localhost/tmp/x.git", "file-with-host"];
        yield return ["ssh://-oProxyCommand=evil/acme/app.git", "ssh option host"];
    }

    [Theory]
    [MemberData(nameof(MustReject))]
    public void Rejects_strict_url_matrix(string url, string reason)
    {
        Assert.False(GitRemoteSafety.TryValidateRemoteUrl(url, out var err, out _),
            $"should reject ({reason}): {url}; err={err}");
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void Username_only_http_is_rejected_and_not_displayed_raw()
    {
        var url = "https://user@" + "github.com/acme/app.git";
        Assert.False(GitRemoteSafety.TryValidateRemoteUrl(url, out _, out _));
        // Redact path must not leave credential-shaped userinfo if somehow called.
        var red = GitRemoteSafety.RedactForDisplay(url);
        Assert.DoesNotContain("user@", red, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_ssh_git_username_still_accepted()
    {
        Assert.True(GitRemoteSafety.TryValidateRemoteUrl("ssh://git@" + "github.com/acme/app.git", out var err, out var d), err);
        Assert.Contains("git@", d, StringComparison.Ordinal);
        Assert.True(GitRemoteSafety.TryValidateRemoteUrl("git@" + "github.com:acme/app.git", out err, out _), err);
    }
}

public class Checkpoint12bUncertainLifecycleTests
{
    [Fact]
    public async Task Uncertain_gate_survives_index_change_new_fingerprint_until_reconcile_on_push_endpoint()
    {
        var (repo, bare, boundary) = MakeRepoWithBare();
        try
        {
            var ops = new GitPushOps();
            var preview = await ops.PreviewAsync(repo, boundary);
            Assert.True(preview.Ok, preview.Error);
            GitPushOps.MarkUncertainFromPreview(preview);
            Assert.True(GitPushOps.HasDurableUncertain(preview.DurableKey!));

            // Mutate index → new fingerprint would bypass fingerprint-only HashSet
            File.WriteAllText(Path.Combine(repo, "extra.txt"), "x\n");
            RunGit(repo, "add", "extra.txt");
            RunGit(repo, "commit", "-m", "extra");

            var preview2 = await ops.PreviewAsync(repo, boundary);
            Assert.True(preview2.Ok, preview2.Error);
            Assert.NotEqual(preview.Fingerprint, preview2.Fingerprint);
            Assert.Equal(preview.DurableKey, preview2.DurableKey);

            // Must still block: durable operation identity for this worktree/remote/branch/push URL.
            var blocked = await ops.PushAsync(repo, boundary, preview2.Fingerprint!, confirmed: true, networkGranted: true);
            Assert.False(blocked.Ok);
            Assert.True(blocked.OutcomeUncertain, blocked.Error);
            Assert.Contains("uncertain", blocked.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally { Cleanup(repo, bare); }
    }

    [Fact]
    public async Task Reconcile_uses_effective_push_url_not_fetch_remote_name_alone()
    {
        var root = Path.Combine(Path.GetTempPath(), "hikyaku-cp12b-rc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fetchBare = Path.Combine(root, "fetch.git");
        var pushBare = Path.Combine(root, "push.git");
        var seed = Path.Combine(root, "seed");
        var repo = Path.Combine(root, "work");
        try
        {
            Directory.CreateDirectory(seed);
            Directory.CreateDirectory(repo);
            RunGit(root, "init", "--bare", fetchBare);
            RunGit(root, "init", "--bare", pushBare);
            InitCommit(seed, "f.txt", "F\n", "fetch tip");
            RunGit(seed, "remote", "add", "origin", fetchBare);
            RunGit(seed, "push", "-u", "origin", "HEAD:main");

            InitCommit(repo, "w.txt", "W\n", "work tip");
            var localSha = RunGitCapture(repo, "rev-parse", "HEAD").Trim();
            RunGit(repo, "remote", "add", "origin", fetchBare);
            RunGit(repo, "remote", "set-url", "--push", "origin", pushBare);
            // Publish only to push bare (raw git)
            RunGit(repo, "push", pushBare, "HEAD:refs/heads/main");

            var boundary = new PathBoundary(Path.GetFullPath(repo));
            var ops = new GitPushOps();
            var preview = await ops.PreviewAsync(repo, boundary);
            Assert.True(preview.Ok, preview.Error);
            GitPushOps.MarkUncertainOutcome(preview.Fingerprint!);

            var recon = await ops.ReconcileRemoteRefAsync(repo, boundary, preview.Fingerprint!,
                preview.RemoteName!, preview.Branch!, localSha);
            Assert.True(recon.Ok, recon.Error);
            Assert.Equal(localSha, recon.RemoteShaVerified);
            Assert.False(GitPushOps.HasUncertainOutcome(preview.Fingerprint!));
        }
        finally { TryDelete(root); }
    }
}

internal static class Checkpoint12bGitHelpers
{
    public static void InitCommit(string repo, string file, string content, string msg)
    {
        if (!Directory.Exists(Path.Combine(repo, ".git")) && !File.Exists(Path.Combine(repo, ".git")))
            RunGit(repo, "init", "-b", "main");
        RunGit(repo, "config", "user.email", "test@users.noreply.github.com");
        RunGit(repo, "config", "user.name", "Test");
        RunGit(repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, file), content);
        RunGit(repo, "add", file);
        RunGit(repo, "commit", "-m", msg);
    }

    public static (string Repo, string Bare, PathBoundary Boundary) MakeRepoWithBare()
    {
        var bare = Path.Combine(Path.GetTempPath(), "hikyaku-cp12b-bare-" + Guid.NewGuid().ToString("N") + ".git");
        var repo = Path.Combine(Path.GetTempPath(), "hikyaku-cp12b-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        InitCommit(repo, "tracked.txt", "v1\n", "init");
        RunGit(".", "init", "--bare", bare);
        RunGit(repo, "remote", "add", "origin", bare);
        return (Path.GetFullPath(repo), Path.GetFullPath(bare), new PathBoundary(Path.GetFullPath(repo)));
    }

    public static void Cleanup(string repo, string bare)
    {
        TryDelete(repo);
        TryDelete(bare);
    }

    public static void RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(60_000);
        if (p.ExitCode != 0) throw new InvalidOperationException(err + stdout);
    }

    public static string RunGitCapture(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        if (p.ExitCode != 0) throw new InvalidOperationException(err);
        return o;
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch { /* best-effort */ }
    }
}
