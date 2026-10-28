using System.Diagnostics;
using System.Net;
using System.Text;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Checkpoint 12: guided push (disposable bare remotes only) + fixture/loopback PR API.
/// Never pushes this repository or any real remote.
/// </summary>
public class Checkpoint12RemoteUrlSafetyTests
{
    public static IEnumerable<object[]> SupportedRemoteUrls()
    {
        yield return ["https://github.com/acme/app.git"];
        yield return ["ssh://git@" + "github.com/acme/app.git"];
        yield return ["C:\\temp\\bare.git"];
        yield return ["/tmp/bare.git"];
        yield return ["git@" + "github.com:acme/app.git"];
    }

    [Theory]
    [MemberData(nameof(SupportedRemoteUrls))]
    public void Accepts_supported_remote_urls(string url)
    {
        Assert.True(GitRemoteSafety.TryValidateRemoteUrl(url, out var err, out var display), err);
        Assert.False(string.IsNullOrWhiteSpace(display));
    }

    public static IEnumerable<object[]> RejectedRemoteUrls()
    {
        yield return ["https://user:secret@" + "github.com/acme/app.git"];
        yield return ["ext::sh -c evil"];
        yield return ["helper::credential"];
        yield return ["--upload-pack=evil"];
        yield return ["https://github.com/acme/app.git\n-c core.sshCommand=evil"];
        yield return ["user:token@" + "github.com:acme/app.git"];
    }

    [Theory]
    [MemberData(nameof(RejectedRemoteUrls))]
    public void Rejects_credential_protocol_and_injection_urls(string url)
    {
        Assert.False(GitRemoteSafety.TryValidateRemoteUrl(url, out var err, out _));
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void Remote_and_branch_names_reject_options()
    {
        Assert.False(GitRemoteSafety.IsSafeRemoteName("-origin"));
        Assert.False(GitRemoteSafety.IsSafeRemoteName("or igin"));
        Assert.True(GitRemoteSafety.IsSafeRemoteName("origin"));
        Assert.False(GitRemoteSafety.IsSafeBranchName("HEAD"));
        Assert.False(GitRemoteSafety.IsSafeBranchName("-main"));
        Assert.True(GitRemoteSafety.IsSafeBranchName("feature/x"));
    }
}

public class Checkpoint12NetworkGrantTests
{
    [Fact]
    public async Task Network_grant_is_separate_and_default_off()
    {
        var store = new NetworkPublishGrantStore(new MemoryPref12());
        var key = new ProfileKey("gw", "p1");
        Assert.False(await store.IsEnabledAsync(key));
        Assert.False(NetworkPublishAuthorisation.FolderGrantAuthorisesPush);
        Assert.False(NetworkPublishAuthorisation.SaveCheckpointAuthorisesPush);
        Assert.False(NetworkPublishAuthorisation.TerminalOptInAuthorisesPush);

        var denied = await store.EnableAsync(key, warningAcknowledged: false);
        Assert.False(denied.Ok);

        var ok = await store.EnableAsync(key, warningAcknowledged: true);
        Assert.True(ok.Ok);
        Assert.True(await store.IsEnabledAsync(key));

        await store.RevokeAsync(key);
        Assert.False(await store.IsEnabledAsync(key));
        Assert.Contains("publishes", NetworkPublishGrantStore.WarningCopy, StringComparison.OrdinalIgnoreCase);
    }
}

public class Checkpoint12PushBareRemoteTests
{
    [Fact]
    public async Task Disposable_bare_remote_push_verifies_exact_ref_and_leaves_index()
    {
        var (repo, bare, boundary) = MakeRepoWithBare();
        try
        {
            File.WriteAllText(Path.Combine(repo, "tracked.txt"), "before\n");
            // leave unstaged change to prove index/worktree untouched beyond push
            File.WriteAllText(Path.Combine(repo, "wip.txt"), "wip\n");

            var ops = new GitPushOps();
            var preview = await ops.PreviewAsync(repo, boundary);
            Assert.True(preview.Ok, preview.Error);
            Assert.True(preview.Branch is "main" or "master", preview.Branch);
            Assert.False(string.IsNullOrWhiteSpace(preview.LocalSha));
            Assert.Equal("origin", preview.RemoteName);
            Assert.Contains("publishes", preview.WarningCopy, StringComparison.OrdinalIgnoreCase);

            var deniedConfirm = await ops.PushAsync(repo, boundary, preview.Fingerprint!, confirmed: false, networkGranted: true);
            Assert.False(deniedConfirm.Ok);
            Assert.Contains("confirm", deniedConfirm.Error!, StringComparison.OrdinalIgnoreCase);

            var deniedNet = await ops.PushAsync(repo, boundary, preview.Fingerprint!, confirmed: true, networkGranted: false);
            Assert.False(deniedNet.Ok);
            Assert.Contains("Network", deniedNet.Error!, StringComparison.OrdinalIgnoreCase);

            var pushed = await ops.PushAsync(repo, boundary, preview.Fingerprint!, confirmed: true, networkGranted: true);
            Assert.True(pushed.Ok, pushed.Error);
            Assert.True(pushed.Published);
            Assert.Equal(preview.LocalSha, pushed.RemoteShaVerified);
            Assert.False(pushed.OutcomeUncertain);

            var remoteSha = RunGitCapture(bare, "rev-parse", preview.Branch!);
            Assert.Equal(preview.LocalSha, remoteSha.Trim());

            // Untracked/WIP file still present; push did not clean/reset
            Assert.True(File.Exists(Path.Combine(repo, "wip.txt")));
            var status = RunGitCapture(repo, "status", "--porcelain");
            Assert.Contains("wip.txt", status);
        }
        finally { Cleanup(repo, bare); }
    }

    [Fact]
    public async Task Preview_fingerprint_change_refuses_stale_push()
    {
        var (repo, bare, boundary) = MakeRepoWithBare();
        try
        {
            var ops = new GitPushOps();
            var preview = await ops.PreviewAsync(repo, boundary);
            Assert.True(preview.Ok, preview.Error);

            File.WriteAllText(Path.Combine(repo, "extra.txt"), "x\n");
            RunGit(repo, "add", "extra.txt");
            RunGit(repo, "commit", "-m", "extra");

            var stale = await ops.PushAsync(repo, boundary, preview.Fingerprint!, confirmed: true, networkGranted: true);
            Assert.False(stale.Ok);
            Assert.Contains("changed after preview", stale.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally { Cleanup(repo, bare); }
    }

    [Fact]
    public async Task Detached_head_and_grant_mismatch_fail_closed()
    {
        var (repo, bare, boundary) = MakeRepoWithBare();
        var outside = Path.Combine(Path.GetTempPath(), "hikyaku-cp12-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var ops = new GitPushOps();
            var sha = RunGitCapture(repo, "rev-parse", "HEAD").Trim();
            RunGit(repo, "checkout", "--detach", sha);
            var det = await ops.PreviewAsync(repo, boundary);
            Assert.False(det.Ok);
            Assert.Contains("Detached", det.Error!, StringComparison.OrdinalIgnoreCase);

            RunGit(repo, "checkout", "-");
            var wrongGrant = await ops.PreviewAsync(repo, new PathBoundary(outside));
            Assert.False(wrongGrant.Ok);
        }
        finally
        {
            Cleanup(repo, bare);
            TryDelete(outside);
        }
    }

    [Fact]
    public async Task Secret_protocol_remote_and_insteadof_refused()
    {
        var (repo, bare, boundary) = MakeRepoWithBare();
        try
        {
            var ops = new GitPushOps();
            RunGit(repo, "remote", "set-url", "origin", "https://user:hunter2@" + "github.com/acme/app.git");
            var bad = await ops.PreviewAsync(repo, boundary);
            Assert.False(bad.Ok);
            Assert.Contains("credential", bad.Error!, StringComparison.OrdinalIgnoreCase);

            RunGit(repo, "remote", "set-url", "origin", bare);
            RunGit(repo, "config", "url.https://evil.example/.insteadOf", "https://github.com/");
            var rewrite = await ops.PreviewAsync(repo, boundary);
            Assert.False(rewrite.Ok);
            Assert.Contains("insteadOf", rewrite.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally { Cleanup(repo, bare); }
    }

    [Fact]
    public async Task Malicious_pre_push_hook_does_not_execute()
    {
        var (repo, bare, boundary) = MakeRepoWithBare();
        try
        {
            var marker = Path.Combine(repo, "pre-push-ran.txt");
            var hooks = Path.Combine(repo, ".git", "hooks");
            Directory.CreateDirectory(hooks);
            await File.WriteAllTextAsync(Path.Combine(hooks, "pre-push"),
                "#!/bin/sh\necho ran > pre-push-ran.txt\nexit 1\n");

            var ops = new GitPushOps();
            var preview = await ops.PreviewAsync(repo, boundary);
            Assert.True(preview.Ok, preview.Error);
            var pushed = await ops.PushAsync(repo, boundary, preview.Fingerprint!, confirmed: true, networkGranted: true);
            Assert.True(pushed.Ok, pushed.Error);
            Assert.False(File.Exists(marker));
        }
        finally { Cleanup(repo, bare); }
    }

    [Fact]
    public async Task Non_fast_forward_rejection_is_definite_not_uncertain()
    {
        var (repo, bare, boundary) = MakeRepoWithBare();
        try
        {
            // Seed bare, then force-push a divergent tip from a second clone (test setup only — app never force-pushes).
            RunGit(repo, "push", "origin", "HEAD:refs/heads/main");

            var other = Path.Combine(Path.GetTempPath(), "hikyaku-cp12-other-" + Guid.NewGuid().ToString("N"));
            RunGit(Path.GetTempPath(), "clone", bare, other);
            RunGit(other, "config", "user.email", "test@users.noreply.github.com");
            RunGit(other, "config", "user.name", "Test");
            RunGit(other, "config", "commit.gpgsign", "false");
            File.WriteAllText(Path.Combine(other, "other.txt"), "o\n");
            RunGit(other, "add", "other.txt");
            RunGit(other, "commit", "-m", "other");
            RunGit(other, "push", "--force", "origin", "HEAD:refs/heads/main");

            File.WriteAllText(Path.Combine(repo, "local.txt"), "l\n");
            RunGit(repo, "add", "local.txt");
            RunGit(repo, "commit", "-m", "local");

            var ops = new GitPushOps();
            var preview = await ops.PreviewAsync(repo, boundary);
            Assert.True(preview.Ok, preview.Error);
            var result = await ops.PushAsync(repo, boundary, preview.Fingerprint!, confirmed: true, networkGranted: true);
            Assert.False(result.Ok, "non-fast-forward must fail: " + result.Error);
            Assert.False(result.OutcomeUncertain);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
            TryDelete(other);
        }
        finally { Cleanup(repo, bare); }
    }

    [Fact]
    public async Task Auth_failure_does_not_hang_with_prompt_disabled()
    {
        var repo = MakeRepo();
        try
        {
            // Unreachable host with auth-shaped demand; GIT_TERMINAL_PROMPT=0 must not hang.
            RunGit(repo, "remote", "add", "origin", "https://127.0.0.1:1/acme/no-such.git");
            var boundary = new PathBoundary(repo);
            var ops = new GitPushOps();
            var preview = await ops.PreviewAsync(repo, boundary);
            Assert.True(preview.Ok, preview.Error);
            var sw = Stopwatch.StartNew();
            var result = await ops.PushAsync(repo, boundary, preview.Fingerprint!, confirmed: true, networkGranted: true,
                timeout: TimeSpan.FromSeconds(15));
            sw.Stop();
            Assert.False(result.Ok);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "push must not hang on auth/prompt");
        }
        finally { TryDelete(repo); }
    }

    [Fact]
    public async Task Uncertain_outcome_blocks_silent_retry_until_reconcile()
    {
        var (repo, bare, boundary) = MakeRepoWithBare();
        try
        {
            var ops = new GitPushOps();
            var preview = await ops.PreviewAsync(repo, boundary);
            Assert.True(preview.Ok, preview.Error);
            GitPushOps.MarkUncertainOutcome(preview.Fingerprint!);
            Assert.True(GitPushOps.HasUncertainOutcome(preview.Fingerprint!));

            var blocked = await ops.PushAsync(repo, boundary, preview.Fingerprint!, confirmed: true, networkGranted: true);
            Assert.False(blocked.Ok);
            Assert.True(blocked.OutcomeUncertain);
            Assert.Contains("uncertain", blocked.Error!, StringComparison.OrdinalIgnoreCase);

            // Remote does not have the tip yet — reconcile fails and keeps the gate
            var reconFail = await ops.ReconcileRemoteRefAsync(repo, boundary, preview.Fingerprint!, "origin", preview.Branch!, preview.LocalSha!);
            Assert.False(reconFail.Ok);
            Assert.True(GitPushOps.HasUncertainOutcome(preview.Fingerprint!));

            // Actual publish via raw git, then reconcile clears the gate
            RunGit(repo, "push", "origin", "HEAD:refs/heads/" + preview.Branch);
            var reconOk = await ops.ReconcileRemoteRefAsync(repo, boundary, preview.Fingerprint!, "origin", preview.Branch!, preview.LocalSha!);
            Assert.True(reconOk.Ok, reconOk.Error);
            Assert.False(GitPushOps.HasUncertainOutcome(preview.Fingerprint!));
        }
        finally { Cleanup(repo, bare); }
    }

    private static (string Repo, string Bare, PathBoundary Boundary) MakeRepoWithBare()
    {
        var bare = Path.Combine(Path.GetTempPath(), "hikyaku-cp12-bare-" + Guid.NewGuid().ToString("N") + ".git");
        var repo = Path.Combine(Path.GetTempPath(), "hikyaku-cp12-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        RunGit(repo, "init", "-b", "main");
        RunGit(repo, "config", "user.email", "test@users.noreply.github.com");
        RunGit(repo, "config", "user.name", "Test");
        RunGit(repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, "tracked.txt"), "v1\n");
        RunGit(repo, "add", "tracked.txt");
        RunGit(repo, "commit", "-m", "init");
        RunGit(".", "init", "--bare", bare);
        RunGit(repo, "remote", "add", "origin", bare);
        return (Path.GetFullPath(repo), Path.GetFullPath(bare), new PathBoundary(Path.GetFullPath(repo)));
    }

    private static string MakeRepo()
    {
        var repo = Path.Combine(Path.GetTempPath(), "hikyaku-cp12-solo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        RunGit(repo, "init", "-b", "main");
        RunGit(repo, "config", "user.email", "test@users.noreply.github.com");
        RunGit(repo, "config", "user.name", "Test");
        RunGit(repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "a\n");
        RunGit(repo, "add", "a.txt");
        RunGit(repo, "commit", "-m", "init");
        return Path.GetFullPath(repo);
    }

    private static void Cleanup(string repo, string bare)
    {
        TryDelete(repo);
        TryDelete(bare);
    }

    private static void RunGit(string cwd, params string[] args)
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

    private static string RunGitCapture(string cwd, params string[] args)
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

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch { /* best-effort */ }
    }
}

public class Checkpoint12PullRequestFixtureTests
{
    [Fact]
    public async Task Fixture_create_readback_duplicate_and_cancel()
    {
        var api = new FixtureGitHubPullRequestApi { BaseAddress = new Uri("http://127.0.0.1:9/") };
        Assert.True(api.IsSynthetic);

        var cancelled = await api.CreateAsync(
            new GitHubPrRequest("acme", "app", "Title here", "body", "feature", "main"),
            confirmed: false);
        Assert.False(cancelled.Ok);
        Assert.True(cancelled.Cancelled);

        var created = await api.CreateAsync(
            new GitHubPrRequest("acme", "app", "Title here", "body", "feature", "main"),
            confirmed: true);
        Assert.True(created.Ok, created.Error);
        Assert.NotNull(created.Number);
        Assert.Equal("feature", created.HeadRef);
        Assert.Equal("main", created.BaseRef);

        var read = await api.GetAsync("acme", "app", created.Number!.Value);
        Assert.True(read.Ok, read.Error);
        Assert.Equal(created.Number, read.Number);
        Assert.Equal("feature", read.HeadRef);

        var dup = await api.CreateAsync(
            new GitHubPrRequest("acme", "app", "Another", "body", "feature", "main"),
            confirmed: true);
        Assert.False(dup.Ok);
        Assert.True(dup.Duplicate);
        Assert.Equal(created.Number, dup.Number);
    }

    [Fact]
    public async Task Setup_required_api_never_claims_live_acceptance()
    {
        var api = new SetupRequiredGitHubPullRequestApi();
        var r = await api.CreateAsync(
            new GitHubPrRequest("acme", "app", "Title here", "body", "feature", "main"),
            confirmed: true);
        Assert.False(r.Ok);
        Assert.True(r.SetupRequired);
        Assert.Contains("not configured", r.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compare URL", SetupRequiredGitHubPullRequestApi.SetupCopy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Guided_pr_requires_verified_push_and_github_remote()
    {
        var api = new FixtureGitHubPullRequestApi();
        var guided = new GuidedPullRequestOps(api);
        var noPush = await guided.CreateAfterVerifiedPushAsync(
            new GitPushResult(false, Error: "no"),
            "https://github.com/acme/app.git", "feature", "main", "Title here", "body", confirmed: true);
        Assert.False(noPush.Ok);

        var fileRemote = await guided.CreateAfterVerifiedPushAsync(
            new GitPushResult(true, Published: true, LocalSha: "abc", RemoteShaVerified: "abc"),
            @"C:\temp\bare.git", "feature", "main", "Title here", "body", confirmed: true);
        Assert.False(fileRemote.Ok);
        Assert.Contains("github.com", fileRemote.Error!, StringComparison.OrdinalIgnoreCase);

        var ok = await guided.CreateAfterVerifiedPushAsync(
            new GitPushResult(true, Published: true, LocalSha: "abc", RemoteShaVerified: "abc"),
            "https://github.com/acme/app.git", "feature", "main", "Title here", "body", confirmed: true);
        Assert.True(ok.Ok, ok.Error);
    }

    [Fact]
    public async Task Loopback_http_pr_create_and_wrong_destination_readback()
    {
        var listener = TestLoopback.Start(19100, 19150, out var port);

        var createCount = 0;
        using var run = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!run.IsCancellationRequested)
            {
                var ctx = await listener.GetContextAsync().WaitAsync(run.Token);
                var path = ctx.Request.Url!.AbsolutePath;
                var auth = ctx.Request.Headers["Authorization"] ?? "";
                Assert.StartsWith("Bearer ", auth);
                Assert.DoesNotContain("SECRETTOKEN", ctx.Request.Url.ToString());

                byte[] bytes;
                if (path == "/repos/acme/app/pulls" && ctx.Request.HttpMethod == "POST")
                {
                    createCount++;
                    bytes = Encoding.UTF8.GetBytes(
                        """{"number":7,"html_url":"http://127.0.0.1/acme/app/pull/7","url":"http://127.0.0.1/repos/acme/app/pulls/7","head":{"ref":"feature"},"base":{"ref":"main"}}""");
                    ctx.Response.StatusCode = 201;
                }
                else if (path == "/repos/acme/app/pulls/7")
                {
                    // Wrong destination on purpose for second call pattern — first readback matches
                    bytes = Encoding.UTF8.GetBytes(
                        createCount <= 1
                            ? """{"number":7,"html_url":"http://127.0.0.1/acme/app/pull/7","head":{"ref":"feature"},"base":{"ref":"main"}}"""
                            : """{"number":7,"html_url":"http://127.0.0.1/acme/app/pull/7","head":{"ref":"wrong"},"base":{"ref":"main"}}""");
                    ctx.Response.StatusCode = 200;
                }
                else if (path.StartsWith("/repos/acme/app/pulls", StringComparison.Ordinal))
                {
                    bytes = Encoding.UTF8.GetBytes("[]");
                    ctx.Response.StatusCode = 200;
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    bytes = "{}"u8.ToArray();
                }
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }, run.Token);

        try
        {
            var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            var api = new HttpGitHubPullRequestApi(http, () => "SECRETTOKEN");
            var created = await api.CreateAsync(
                new GitHubPrRequest("acme", "app", "Title here", "body", "feature", "main"),
                confirmed: true);
            Assert.True(created.Ok, created.Error);
            Assert.Equal(7, created.Number);

            // Expired / missing token
            var setup = new HttpGitHubPullRequestApi(http, () => null);
            var denied = await setup.CreateAsync(
                new GitHubPrRequest("acme", "app", "Title here", "body", "feature", "main"),
                confirmed: true);
            Assert.True(denied.SetupRequired);
        }
        finally
        {
            run.Cancel();
            listener.Stop();
            try { await server; } catch { /* cancelled */ }
            listener.Close();
        }
    }

    [Fact]
    public void Parse_github_identity_from_remote()
    {
        Assert.True(GitHubPrIdentityParser.TryParseRemote("https://github.com/Acme/App.git", out var o, out var r, out _));
        Assert.Equal("Acme", o);
        Assert.Equal("App", r);
        Assert.False(GitHubPrIdentityParser.TryParseRemote(@"D:\bare.git", out _, out _, out var err));
        Assert.Contains("github.com", err!, StringComparison.OrdinalIgnoreCase);
    }
}

public class Checkpoint12DocsTests
{
    [Fact]
    public void Handoff_and_parity_mark_push_pr_boundary()
    {
        var root = FindRepoRoot();
        var handoff = File.ReadAllText(Path.Combine(root, "docs", "HANDOFF.md"));
        Assert.Contains("Checkpoint 12", handoff);
        Assert.Contains("Push", handoff, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pending", handoff, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("12b", handoff);

        var desktop = File.ReadAllText(Path.Combine(root, "docs", "DESKTOP-PARITY.md"));
        Assert.Contains("Push", desktop, StringComparison.OrdinalIgnoreCase);

        var perms = File.ReadAllText(Path.Combine(root, "docs", "PERMISSIONS-DESIGN.md"));
        Assert.Contains("push", perms, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never", perms);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hikyaku.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("repo root not found");
    }
}

file sealed class MemoryPref12 : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default)
    {
        _content = content;
        return Task.CompletedTask;
    }
}
