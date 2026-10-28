using System.Diagnostics;
using System.Text;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Checkpoint 9: local folder grants, path boundary, mutations, secrets, read-only Git — synthetic temps only.</summary>
public class Checkpoint9GrantTests
{
    private static MemoryStore Store() => new();

    [Fact]
    public async Task No_grant_means_workspace_unavailable()
    {
        var grants = new LocalFolderGrantStore(Store(), () => "machine-a");
        var key = new ProfileKey("gw", "alpha");
        Assert.Null(await grants.GetAsync(key));
        var ws = new ProjectWorkspace(grants, key);
        var list = await ws.ListAsync(".");
        Assert.False(list.Ok);
        Assert.Contains("grant", list.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Grant_and_revoke_are_per_profile()
    {
        var root = MakeTempProject();
        try
        {
            var grants = new LocalFolderGrantStore(Store(), () => "machine-a");
            var a = new ProfileKey("gw", "alpha");
            var b = new ProfileKey("gw", "beta");
            var g = await grants.GrantAsync(a, root, FolderGrantMode.Read);
            Assert.Equal(root, g.CanonicalRoot, ignoreCase: true);
            Assert.Equal("machine-a", g.MachineId);
            Assert.NotNull(await grants.GetAsync(a));
            Assert.Null(await grants.GetAsync(b));

            await grants.RevokeAsync(a);
            Assert.Null(await grants.GetAsync(a));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Profile_switch_does_not_broaden_other_grants()
    {
        var rootA = MakeTempProject();
        var rootB = MakeTempProject();
        try
        {
            var grants = new LocalFolderGrantStore(Store(), () => "machine-a");
            var a = new ProfileKey("gw", "alpha");
            var b = new ProfileKey("gw", "beta");
            await grants.GrantAsync(a, rootA, FolderGrantMode.ReadWrite);
            await grants.GrantAsync(b, rootB, FolderGrantMode.Read);
            Assert.Equal(FolderGrantMode.ReadWrite, (await grants.GetAsync(a))!.Mode);
            Assert.Equal(FolderGrantMode.Read, (await grants.GetAsync(b))!.Mode);
            Assert.False(PathBoundary.AreSameRoot(
                (await grants.GetAsync(a))!.CanonicalRoot,
                (await grants.GetAsync(b))!.CanonicalRoot));
        }
        finally
        {
            TryDelete(rootA);
            TryDelete(rootB);
        }
    }

    [Fact]
    public async Task Whole_drive_and_user_home_are_rejected()
    {
        var grants = new LocalFolderGrantStore(Store(), () => "machine-a");
        var key = new ProfileKey("gw", "alpha");
        await Assert.ThrowsAsync<FolderGrantException>(() => grants.GrantAsync(key, @"C:\", FolderGrantMode.Read));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            await Assert.ThrowsAsync<FolderGrantException>(() => grants.GrantAsync(key, home, FolderGrantMode.Read));
    }

    private static string MakeTempProject()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hikyaku-cp9-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "hello");
        return Path.GetFullPath(dir);
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { /* best-effort */ }
    }
}

public class Checkpoint9PathBoundaryTests
{
    [Fact]
    public void Traversal_outside_grant_is_denied()
    {
        var root = Path.Combine(Path.GetTempPath(), "hikyaku-bound-" + Guid.NewGuid().ToString("N"));
        var sibling = Path.Combine(Path.GetTempPath(), "hikyaku-sib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "secret.txt"), "nope");
        try
        {
            var b = new PathBoundary(root);
            Assert.False(b.TryResolve(Path.Combine(root, "..", Path.GetFileName(sibling), "secret.txt"), out _, out var reason));
            Assert.Contains("grant", reason!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(sibling, true); } catch { }
        }
    }

    [Fact]
    public void Unc_and_ads_paths_are_denied()
    {
        var root = Path.Combine(Path.GetTempPath(), "hikyaku-unc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var b = new PathBoundary(root);
            Assert.False(b.TryResolve(@"\\server\share\file.txt", out _, out var unc));
            Assert.Contains("UNC", unc!, StringComparison.OrdinalIgnoreCase);
            Assert.False(b.TryResolve(Path.Combine(root, "notes.txt:Zone.Identifier"), out _, out var ads));
            Assert.Contains("stream", ads!, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Junction_escape_is_denied_when_reparse_leaves_root()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "hikyaku-junc-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "hikyaku-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "leak.txt"), "x");
        var link = Path.Combine(root, "escape");
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            if (p.ExitCode != 0 || !Directory.Exists(link))
                return; // environment cannot create junctions — skip without failing suite

            var b = new PathBoundary(root);
            Assert.False(b.TryResolve(Path.Combine(link, "leak.txt"), out _, out var reason));
            Assert.Contains("grant", reason!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { if (Directory.Exists(link)) Directory.Delete(link); } catch { }
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(outside, true); } catch { }
        }
    }
}

public class Checkpoint9SensitiveAndShareTests
{
    [Theory]
    [InlineData(".env")]
    [InlineData("prod.pem")]
    [InlineData("aws-credentials.json")]
    [InlineData("id_rsa")]
    [InlineData("Cookies")]
    public void Sensitive_names_are_blocked_from_share_defaults(string name)
        => Assert.True(SensitivePathPolicy.IsSensitiveLeaf(name));

    [Fact]
    public void Secret_heuristic_redacts_and_warns_without_printing_value()
    {
        var token = "sk-" + new string('a', 26);
        var text = "token = \"" + token + "\"";
        var review = SecretContentReview.Inspect(text, "notes.txt");
        Assert.True(review.Suspicious);
        Assert.DoesNotContain(token, review.UserMessage);
        Assert.Contains("try to spot secrets", review.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("bearer/api token", review.Category);
    }

    [Fact]
    public void Share_keeps_suspected_keys_hard_blocked_even_with_ack()
    {
        var payload = "-----BEGIN " + "RSA PRIVATE KEY-----" + "\nMII...\n" + "-----END " + "RSA PRIVATE KEY-----";
        var decision = ProjectShareGate.Decide(payload, "key.pem", acknowledgeRisk: false);
        Assert.False(decision.Allowed);
        Assert.True(decision.HardBlocked);
        var still = ProjectShareGate.Decide(payload, "key.pem", acknowledgeRisk: true);
        Assert.False(still.Allowed);
        Assert.True(still.HardBlocked);
    }
}

public class Checkpoint9MutationTests
{
    [Fact]
    public void Overwrite_and_delete_fail_closed_without_explicit_confirmation()
    {
        var root = Path.Combine(Path.GetTempPath(), "hikyaku-mut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "a.txt");
        File.WriteAllText(file, "v1");
        try
        {
            var gate = new FileMutationGate(new PathBoundary(root), FolderGrantMode.ReadWrite);
            var denied = gate.Overwrite(file, Encoding.UTF8.GetBytes("v2"), confirmed: false);
            Assert.False(denied.Ok);
            Assert.Equal("v1", File.ReadAllText(file));
            Assert.False(File.Exists(Path.Combine(root, "a.txt.bak")));
            Assert.False(Directory.EnumerateFiles(root, "*.bak").Any());

            var delDenied = gate.Delete(file, confirmed: false);
            Assert.False(delDenied.Ok);
            Assert.True(File.Exists(file));

            Assert.True(gate.Overwrite(file, Encoding.UTF8.GetBytes("v2"), confirmed: true).Ok);
            Assert.Equal("v2", File.ReadAllText(file));
            Assert.True(gate.Delete(file, confirmed: true).Ok);
            Assert.False(File.Exists(file));
            Assert.DoesNotContain(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories), f => f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Read_only_grant_rejects_mutations_even_when_confirmed()
    {
        var root = Path.Combine(Path.GetTempPath(), "hikyaku-ro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "a.txt");
        File.WriteAllText(file, "keep");
        try
        {
            var gate = new FileMutationGate(new PathBoundary(root), FolderGrantMode.Read);
            Assert.False(gate.Overwrite(file, Encoding.UTF8.GetBytes("x"), confirmed: true).Ok);
            Assert.False(gate.Delete(file, confirmed: true).Ok);
            Assert.Equal("keep", File.ReadAllText(file));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Yolo_flag_does_not_bypass_mutation_confirmation()
    {
        var root = Path.Combine(Path.GetTempPath(), "hikyaku-yolo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "a.txt");
        File.WriteAllText(file, "keep");
        try
        {
            var gate = new FileMutationGate(new PathBoundary(root), FolderGrantMode.ReadWrite);
            // Session YOLO is independent — API has no yolo bypass parameter.
            Assert.False(gate.Overwrite(file, Encoding.UTF8.GetBytes("x"), confirmed: false).Ok);
            Assert.Equal("keep", File.ReadAllText(file));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

public class Checkpoint9WorkspaceAndGitTests
{
    [Fact]
    public async Task Safe_list_and_text_preview_stay_inside_grant()
    {
        var root = Path.Combine(Path.GetTempPath(), "hikyaku-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "hi.txt"), "preview-me");
        File.WriteAllText(Path.Combine(root, ".env"), "SECRET=1");
        try
        {
            var store = new MemoryStore();
            var grants = new LocalFolderGrantStore(store, () => "m1");
            var key = new ProfileKey("gw", "p");
            await grants.GrantAsync(key, root, FolderGrantMode.Read);
            var ws = new ProjectWorkspace(grants, key);
            var list = await ws.ListAsync("src");
            Assert.True(list.Ok);
            Assert.Contains(list.Entries, e => e.Name == "hi.txt");

            var preview = await ws.PreviewTextAsync(Path.Combine("src", "hi.txt"));
            Assert.True(preview.Ok);
            Assert.Equal("preview-me", preview.Text);

            var env = await ws.PreviewTextAsync(".env");
            Assert.False(env.Ok);
            Assert.Contains("sensitive", env.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task Expired_or_moved_root_fails_closed()
    {
        var root = Path.Combine(Path.GetTempPath(), "hikyaku-gone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new MemoryStore();
        var grants = new LocalFolderGrantStore(store, () => "m1");
        var key = new ProfileKey("gw", "p");
        await grants.GrantAsync(key, root, FolderGrantMode.Read);
        Directory.Delete(root, true);
        var ws = new ProjectWorkspace(grants, key);
        var list = await ws.ListAsync(".");
        Assert.False(list.Ok);
        Assert.Contains("path", list.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Git_status_and_diffs_match_fixture_without_shell_string()
    {
        var root = Path.Combine(Path.GetTempPath(), "hikyaku-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RunGit(root, "init");
            RunGit(root, "config", "user.email", "test@users.noreply.github.com");
            RunGit(root, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(root, "tracked.txt"), "one\n");
            RunGit(root, "add", "tracked.txt");
            RunGit(root, "commit", "-m", "init");
            File.WriteAllText(Path.Combine(root, "tracked.txt"), "two\n");
            File.WriteAllText(Path.Combine(root, "unstaged-new.txt"), "new\n");
            RunGit(root, "add", "unstaged-new.txt");
            File.WriteAllText(Path.Combine(root, "unstaged-new.txt"), "new2\n");

            var reader = new GitChangesReader();
            var snap = await reader.ReadAsync(root);
            Assert.True(snap.Ok, snap.Error);
            Assert.Contains(snap.Entries, e => e.Path == "tracked.txt" && e.InWorkingTree);
            Assert.Contains(snap.Entries, e => e.Path == "unstaged-new.txt" && e.InIndex);
            var unstaged = await reader.DiffUnstagedAsync(root, "tracked.txt");
            Assert.True(unstaged.Ok, unstaged.Error);
            Assert.Contains("-one", unstaged.Text);
            Assert.Contains("+two", unstaged.Text);
            var staged = await reader.DiffStagedAsync(root, "unstaged-new.txt");
            Assert.True(staged.Ok, staged.Error);
            Assert.Contains("unstaged-new.txt", staged.Text);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Git_reader_builds_argument_list_not_shell()
    {
        var args = GitChangesReader.BuildArgs(@"D:\proj", "status", "--porcelain=v1");
        Assert.Equal(["-C", @"D:\proj", "status", "--porcelain=v1"], args);
        Assert.DoesNotContain(args, a => a.Contains("&&") || a.Contains("|") || a.Contains(";"));
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
        p.WaitForExit(30_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException(p.StandardError.ReadToEnd());
    }
}

public class Checkpoint9DocsAndBridgeTests
{
    [Fact]
    public void Gateway_fs_bridge_is_documented_and_not_deployed()
    {
        var root = FindRepoRoot();
        var proposal = Path.Combine(root, "docs", "GATEWAY-FS-BRIDGE-PROPOSAL.md");
        Assert.True(File.Exists(proposal));
        var text = File.ReadAllText(proposal);
        Assert.Contains("NOT deployed", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("local UI", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tool registered", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parity_marks_folder_grant_complete_and_commit_pending()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "ANDROID-PARITY.md"));
        Assert.Contains("Explicit folder grant", text);
        Assert.Contains("**complete**", text);
        Assert.Contains("terminal", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pending", text, StringComparison.OrdinalIgnoreCase);
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
