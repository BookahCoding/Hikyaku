using System.Diagnostics;
using System.Text;

namespace Hikyaku.Core;

public sealed record GitChangeEntry(string Path, bool InIndex, bool InWorkingTree, string StatusCode);

public sealed record GitSnapshot(bool Ok, IReadOnlyList<GitChangeEntry> Entries, string? Error = null, string? Branch = null);

public sealed record GitDiffResult(bool Ok, string Text, string? Error = null);

/// <summary>
/// Read-only Git inspection via a trusted executable and ArgumentList (never a shell command string).
/// Mutating actions live in <see cref="GitLocalOps"/> (exact-file stage/commit/worktree). No push here.
/// </summary>
public sealed class GitChangesReader(string gitExecutable = "git")
{
    public static IReadOnlyList<string> BuildArgs(string workingTree, params string[] gitArgs)
    {
        var list = new List<string> { "-C", workingTree };
        list.AddRange(gitArgs);
        return list;
    }

    public async Task<GitSnapshot> ReadAsync(string workingTree, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workingTree) || !Directory.Exists(workingTree))
            return new GitSnapshot(false, [], "Working tree path missing.");

        var branch = await RunAsync(workingTree, ["rev-parse", "--abbrev-ref", "HEAD"], ct).ConfigureAwait(false);
        var status = await RunAsync(workingTree, ["status", "--porcelain=v1"], ct).ConfigureAwait(false);
        if (!status.Ok)
            return new GitSnapshot(false, [], status.Error ?? "git status failed.");

        var entries = new List<GitChangeEntry>();
        using var reader = new StringReader(status.Text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length < 3) continue;
            var x = line[0];
            var y = line[1];
            var path = line[3..].Trim();
            if (path.Contains(" -> ", StringComparison.Ordinal))
                path = path.Split(" -> ", 2)[^1];
            entries.Add(new GitChangeEntry(path.Replace('\\', '/'),
                InIndex: x is not ' ' and not '?',
                InWorkingTree: y is not ' ' || x == '?',
                StatusCode: $"{x}{y}"));
        }

        return new GitSnapshot(true, entries, Branch: branch.Ok ? branch.Text.Trim() : null);
    }

    public Task<GitDiffResult> DiffUnstagedAsync(string workingTree, string path, CancellationToken ct = default)
    {
        if (!GitLocalOps.IsSafePathspec(path))
            return Task.FromResult(new GitDiffResult(false, "", "Rejected unsafe pathspec."));
        return DiffAsync(workingTree, ["diff", "--no-ext-diff", "--", path], ct);
    }

    public Task<GitDiffResult> DiffStagedAsync(string workingTree, string path, CancellationToken ct = default)
    {
        if (!GitLocalOps.IsSafePathspec(path))
            return Task.FromResult(new GitDiffResult(false, "", "Rejected unsafe pathspec."));
        return DiffAsync(workingTree, ["diff", "--no-ext-diff", "--cached", "--", path], ct);
    }

    private async Task<GitDiffResult> DiffAsync(string workingTree, string[] gitArgs, CancellationToken ct)
    {
        var run = await RunAsync(workingTree, gitArgs, ct).ConfigureAwait(false);
        return run.Ok
            ? new GitDiffResult(true, run.Text)
            : new GitDiffResult(false, "", run.Error);
    }

    private async Task<(bool Ok, string Text, string? Error)> RunAsync(string workingTree, string[] gitArgs, CancellationToken ct)
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
        foreach (var a in BuildArgs(workingTree, gitArgs))
            psi.ArgumentList.Add(a);

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
}
