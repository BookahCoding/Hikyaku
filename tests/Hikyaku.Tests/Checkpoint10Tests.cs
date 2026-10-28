using System.Diagnostics;
using System.Text;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Checkpoint 10: exact-file stage/commit, worktrees, hooks safeguards, keys-exclusion — synthetic temps only.</summary>
public class Checkpoint10ShareExclusionTests
{
    [Fact]
    public void Suspected_keys_are_not_shared_merely_by_acknowledging()
    {
        var payload = "-----BEGIN " + "RSA PRIVATE KEY-----" + "\nMII...\n" + "-----END " + "RSA PRIVATE KEY-----";
        var denied = ProjectShareGate.Decide(payload, "notes.txt", acknowledgeRisk: false);
        Assert.False(denied.Allowed);
        Assert.True(denied.HardBlocked);

        var still = ProjectShareGate.Decide(payload, "notes.txt", acknowledgeRisk: true);
        Assert.False(still.Allowed);
        Assert.True(still.HardBlocked);
        Assert.NotNull(still.ConsentTimestampUtc); // understanding only — not share
        Assert.Contains("blocked", still.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MII", still.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("id_rsa")]
    [InlineData("prod.pem")]
    public void Credential_leaf_names_stay_hard_blocked_even_with_ack(string name)
    {
        var d = ProjectShareGate.Decide("not-a-secret-body", name, acknowledgeRisk: true);
        Assert.False(d.Allowed);
        Assert.True(d.HardBlocked);
        Assert.Contains("cannot be shared", d.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public class Checkpoint10GitStageCommitTests
{
    [Fact]
    public async Task Exact_file_stage_and_local_checkpoint_preserve_unrelated_index()
    {
        var root = MakeRepo();
        try
        {
            File.WriteAllText(Path.Combine(root, "keep.txt"), "keep-v1\n");
            File.WriteAllText(Path.Combine(root, "touch.txt"), "touch-v1\n");
            RunGit(root, "add", "keep.txt", "touch.txt");
            RunGit(root, "commit", "-m", "base");

            File.WriteAllText(Path.Combine(root, "keep.txt"), "keep-v2\n");
            File.WriteAllText(Path.Combine(root, "touch.txt"), "touch-v2\n");
            RunGit(root, "add", "keep.txt"); // pre-staged unrelated

            var boundary = new PathBoundary(root);
            var ops = new GitLocalOps();

            var blocked = await ops.SaveCheckpointAsync(root, boundary, ["touch.txt"], "only touch", confirmed: true);
            Assert.False(blocked.Ok);
            Assert.Contains("other staged", blocked.Error!, StringComparison.OrdinalIgnoreCase);

            Assert.True((await ops.UnstageExactAsync(root, boundary, ["keep.txt"])).Ok);
            var staged = await ops.StageExactAsync(root, boundary, ["touch.txt"]);
            Assert.True(staged.Ok, staged.Error);

            var deniedConfirm = await ops.SaveCheckpointAsync(root, boundary, ["touch.txt"], "only touch", confirmed: false);
            Assert.False(deniedConfirm.Ok);

            var ok = await ops.SaveCheckpointAsync(root, boundary, ["touch.txt"], "only touch", confirmed: true);
            Assert.True(ok.Ok, ok.Error);
            Assert.False(ok.PublishedOrPushed);
            Assert.False(string.IsNullOrWhiteSpace(ok.Sha));

            var keepBody = File.ReadAllText(Path.Combine(root, "keep.txt"));
            Assert.Equal("keep-v2\n", keepBody);
            var show = RunGitCapture(root, "show", "HEAD:touch.txt");
            Assert.Contains("touch-v2", show);
            var headKeep = RunGitCapture(root, "show", "HEAD:keep.txt");
            Assert.Contains("keep-v1", headKeep); // keep change not in checkpoint commit

            var snap = await new GitChangesReader().ReadAsync(root);
            Assert.True(snap.Ok);
            Assert.Contains(snap.Entries, e => e.Path == "keep.txt" && e.InWorkingTree);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Secret_and_path_injection_are_refused_before_stage()
    {
        var root = MakeRepo();
        try
        {
            File.WriteAllText(Path.Combine(root, ".env"), "SECRET=1\n");
            File.WriteAllText(Path.Combine(root, "ok.txt"), "hello\n");
            File.WriteAllText(Path.Combine(root, "leak.txt"), "token = \"sk-" + new string('b', 26) + "\"\n");
            var boundary = new PathBoundary(root);
            var ops = new GitLocalOps();

            Assert.False((await ops.StageExactAsync(root, boundary, [".env"])).Ok);
            Assert.False((await ops.StageExactAsync(root, boundary, ["leak.txt"])).Ok);
            Assert.False((await ops.StageExactAsync(root, boundary, ["-u"])).Ok);
            Assert.False((await ops.StageExactAsync(root, boundary, ["../outside.txt"])).Ok);
            Assert.True((await ops.StageExactAsync(root, boundary, ["ok.txt"])).Ok);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Hooks_do_not_execute_on_guided_checkpoint()
    {
        var root = MakeRepo();
        try
        {
            var marker = Path.Combine(root, "hook-ran.txt");
            var hooks = Path.Combine(root, ".git", "hooks");
            Directory.CreateDirectory(hooks);
            var hook = Path.Combine(hooks, "pre-commit");
            // Windows: write a .bat-style won't run via hooksPath override; use sh for environments with bash, else a cmd script.
            // Our ops force core.hooksPath to an empty dir — marker must stay absent either way.
            await File.WriteAllTextAsync(hook, "#!/bin/sh\necho ran > hook-ran.txt\nexit 1\n");
            File.WriteAllText(Path.Combine(root, "n.txt"), "n\n");
            var boundary = new PathBoundary(root);
            var ops = new GitLocalOps();
            var safety = await ops.AssessAsync(root);
            Assert.True(safety.HooksPresent);
            Assert.Contains("hooks", safety.LimitsCopy, StringComparison.OrdinalIgnoreCase);

            var result = await ops.SaveCheckpointAsync(root, boundary, ["n.txt"], "no hooks", confirmed: true);
            Assert.True(result.Ok, result.Error);
            Assert.False(File.Exists(marker));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Revoked_or_outside_grant_fails_closed()
    {
        var root = MakeRepo();
        var outside = Path.Combine(Path.GetTempPath(), "hikyaku-cp10-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "x\n");
            var boundary = new PathBoundary(root);
            var ops = new GitLocalOps();
            // Absolute escape
            Assert.False(GitLocalOps.IsSafePathspec(Path.Combine(outside, "x.txt")));
            var abs = await ops.StageExactAsync(root, boundary, [Path.Combine(outside, "x.txt").Replace('\\', '/')]);
            Assert.False(abs.Ok);

            // Missing tree
            var missing = await ops.StageExactAsync(Path.Combine(root, "gone"), boundary, ["a.txt"]);
            Assert.False(missing.Ok);
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    [Fact]
    public async Task Conflicted_index_refuses_checkpoint()
    {
        var root = MakeRepo();
        try
        {
            File.WriteAllText(Path.Combine(root, "c.txt"), "base\n");
            RunGit(root, "add", "c.txt");
            RunGit(root, "commit", "-m", "c0");
            var main = RunGitCapture(root, "rev-parse", "--abbrev-ref", "HEAD").Trim();
            RunGit(root, "checkout", "-b", "other");
            File.WriteAllText(Path.Combine(root, "c.txt"), "other\n");
            RunGit(root, "commit", "-am", "c1");
            RunGit(root, "checkout", main);
            File.WriteAllText(Path.Combine(root, "c.txt"), "mainline\n");
            RunGit(root, "commit", "-am", "c2");
            var merge = StartGit(root, "merge", "--no-edit", "other");
            merge.WaitForExit(30_000);
            var snap = await new GitChangesReader().ReadAsync(root);
            Assert.True(snap.Ok);
            if (!snap.Entries.Any(e => e.StatusCode is "UU" or "AA" or "DD" or "AU" or "UA" or "DU" or "UD"))
                return; // merge did not conflict in this environment

            var ops = new GitLocalOps();
            var result = await ops.SaveCheckpointAsync(root, new PathBoundary(root), ["c.txt"], "nope", confirmed: true);
            Assert.False(result.Ok);
            Assert.Contains("Conflict", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(root); }
    }

    private static string MakeRepo()
    {
        var root = Path.Combine(Path.GetTempPath(), "hikyaku-cp10-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RunGit(root, "init");
        RunGit(root, "config", "user.email", "test@users.noreply.github.com");
        RunGit(root, "config", "user.name", "Test");
        // Ensure commits work without signing
        RunGit(root, "config", "commit.gpgsign", "false");
        return Path.GetFullPath(root);
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var p = StartGit(cwd, args);
        p.WaitForExit(30_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException(p.StandardError.ReadToEnd());
    }

    private static string RunGitCapture(string cwd, params string[] args)
    {
        var p = StartGit(cwd, args);
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException(p.StandardError.ReadToEnd());
        return stdout;
    }

    private static Process StartGit(string cwd, params string[] args)
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
        return Process.Start(psi)!;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { /* best-effort */ }
    }
}

public class Checkpoint10WorktreeTests
{
    [Fact]
    public async Task Create_list_remove_worktree_with_consent_and_deny_dirty()
    {
        var root = Checkpoint10GitStageCommitTests_Make();
        try
        {
            File.WriteAllText(Path.Combine(root, "w.txt"), "w\n");
            Run(root, "add", "w.txt");
            Run(root, "commit", "-m", "w0");
            var head = RunCapture(root, "rev-parse", "HEAD").Trim();

            var dest = Path.Combine(Path.GetTempPath(), "hikyaku-wt-" + Guid.NewGuid().ToString("N"));
            // parent exists; leaf must not exist yet for some git versions — create parent only
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            var ops = new GitLocalOps();
            var boundary = new PathBoundary(root);

            Assert.False((await ops.CreateWorktreeAsync(root, boundary, dest, destinationConsented: false, "feature/a", head)).Ok);

            var created = await ops.CreateWorktreeAsync(root, boundary, dest, destinationConsented: true, "feature/a", head);
            Assert.True(created.Ok, created.Error);
            Assert.True(Directory.Exists(dest));

            var list = await ops.ListWorktreesAsync(root);
            Assert.True(list.Ok);
            Assert.Contains(list.Entries, e => e.Path.Replace('/', Path.DirectorySeparatorChar)
                .Equals(dest, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFullPath(e.Path), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase));

            // Collision
            var again = await ops.CreateWorktreeAsync(root, boundary, dest, true, "feature/b", head);
            Assert.False(again.Ok);

            // Inside source refused
            var inside = Path.Combine(root, "nested-wt");
            Assert.False((await ops.CreateWorktreeAsync(root, boundary, inside, true, "feature/c", head)).Ok);

            // Dirty deny
            File.WriteAllText(Path.Combine(dest, "dirty.txt"), "x");
            var dirty = await ops.RemoveWorktreeAsync(root, boundary, dest, confirmed: true, force: false);
            Assert.False(dirty.Ok);
            Assert.Contains("uncommitted", dirty.Error!, StringComparison.OrdinalIgnoreCase);

            Assert.False((await ops.RemoveWorktreeAsync(root, boundary, dest, confirmed: true, force: true)).Ok);
            Assert.False((await ops.RemoveWorktreeAsync(root, boundary, dest, confirmed: false)).Ok);

            File.Delete(Path.Combine(dest, "dirty.txt"));
            var removed = await ops.RemoveWorktreeAsync(root, boundary, dest, confirmed: true);
            Assert.True(removed.Ok, removed.Error);
            Assert.False(Directory.Exists(dest));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void Pathspec_safety_rejects_options_and_traversal()
    {
        Assert.False(GitLocalOps.IsSafePathspec("-A"));
        Assert.False(GitLocalOps.IsSafePathspec("../x"));
        Assert.False(GitLocalOps.IsSafePathspec("/etc/passwd"));
        Assert.True(GitLocalOps.IsSafePathspec("src/App.cs"));
    }

    private static string Checkpoint10GitStageCommitTests_Make()
    {
        var root = Path.Combine(Path.GetTempPath(), "hikyaku-cp10wt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Run(root, "init");
        Run(root, "config", "user.email", "test@users.noreply.github.com");
        Run(root, "config", "user.name", "Test");
        Run(root, "config", "commit.gpgsign", "false");
        return Path.GetFullPath(root);
    }

    private static void Run(string cwd, params string[] args)
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
        p.WaitForExit(30_000);
        if (p.ExitCode != 0) throw new InvalidOperationException(p.StandardError.ReadToEnd());
    }

    private static string RunCapture(string cwd, params string[] args)
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
        p.WaitForExit(30_000);
        if (p.ExitCode != 0) throw new InvalidOperationException(p.StandardError.ReadToEnd());
        return o;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            // prune worktrees first if needed
            try
            {
                var psi = new ProcessStartInfo("git")
                {
                    WorkingDirectory = path,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("worktree");
                psi.ArgumentList.Add("prune");
                using var p = Process.Start(psi);
                p?.WaitForExit(10_000);
            }
            catch { /* ignore */ }
            Directory.Delete(path, true);
        }
        catch { /* best-effort */ }
    }
}

public class Checkpoint10DocsTests
{
    [Fact]
    public void Parity_marks_guided_commit_and_keys_exclusion()
    {
        var root = FindRepoRoot();
        var android = File.ReadAllText(Path.Combine(root, "docs", "ANDROID-PARITY.md"));
        Assert.Contains("Guided Git commit", android);
        Assert.Contains("worktree", android, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("keys", android, StringComparison.OrdinalIgnoreCase);

        var handoff = File.ReadAllText(Path.Combine(root, "docs", "HANDOFF.md"));
        Assert.Contains("Checkpoint 10", handoff);
        Assert.Contains("terminal", handoff, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pending", handoff, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Permissions_document_hooks_limits_and_keys_exclusion()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "PERMISSIONS-DESIGN.md"));
        Assert.Contains("hooks", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Acknowledge", text);
        Assert.Contains("does not share", text, StringComparison.OrdinalIgnoreCase);
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
