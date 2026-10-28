using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

public sealed record GitOpResult(bool Ok, string? Error = null, string? Detail = null);

public sealed record GitCommitResult(
    bool Ok,
    string? Sha = null,
    string? Error = null,
    bool PublishedOrPushed = false);

public sealed record GitWorktreeEntry(string Path, string Head, string Branch, bool Bare, bool Locked);

public sealed record GitWorktreeListResult(bool Ok, IReadOnlyList<GitWorktreeEntry> Entries, string? Error = null);

public sealed record GitSafetyReport(
    bool HooksPresent,
    bool SigningConfigured,
    bool ExternalDiffRisk,
    bool FilterAttributesPresent,
    string Summary,
    string LimitsCopy);

/// <summary>
/// Exact-file stage/unstage, local Save checkpoint (commit), and worktree create/list/remove.
/// Trusted git + ArgumentList only. Never push, amend, reset --hard, clean, or force-remove by default.
/// Hooks/signing/external-diff are assessed and disabled via -c overrides where possible; clean/smudge
/// filters in the chosen repo remain a documented residual risk (fail closed when attributes mention filter for a path).
/// </summary>
public sealed class GitLocalOps(string gitExecutable = "git")
{
    public const string HooksLimitsCopy =
        "Hikyaku disables repo hooks and commit signing for guided actions via git -c overrides. " +
        "Clean/smudge filters declared in .gitattributes may still run on add - those paths are refused. " +
        "Credential helpers are irrelevant for local commit (no network). Global Git config is never rewritten.";

    public const string PushDeferredCopy =
        "Push is a separate guided action with its own network grant and confirmation. Save checkpoint only creates a local commit - it is not publication.";

    private static readonly Regex ConflictCode = new(@"^(UU|AA|DD|AU|UA|DU|UD)$", RegexOptions.Compiled);

    public static bool IsSafePathspec(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        var p = relativePath.Replace('\\', '/').Trim();
        if (p.StartsWith("-", StringComparison.Ordinal)) return false;
        if (p.Contains('\0')) return false;
        if (p is "." or ".." or "./" or "../") return false;
        if (p.StartsWith("../", StringComparison.Ordinal) || p.Contains("/../", StringComparison.Ordinal)) return false;
        if (p.StartsWith("/", StringComparison.Ordinal) || Regex.IsMatch(p, @"^[A-Za-z]:/")) return false;
        return true;
    }

    public async Task<GitSafetyReport> AssessAsync(string workingTree, CancellationToken ct = default)
    {
        var hooks = false;
        var hooksDir = Path.Combine(workingTree, ".git", "hooks");
        if (Directory.Exists(hooksDir))
        {
            foreach (var f in Directory.EnumerateFiles(hooksDir))
            {
                var name = Path.GetFileName(f);
                if (name.EndsWith(".sample", StringComparison.OrdinalIgnoreCase)) continue;
                hooks = true;
                break;
            }
        }

        var sign = await RunAsync(workingTree, ["config", "--get", "commit.gpgsign"], ct, safetyOverrides: false).ConfigureAwait(false);
        var signing = sign.Ok && sign.Text.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

        var ext = await RunAsync(workingTree, ["config", "--get", "diff.external"], ct, safetyOverrides: false).ConfigureAwait(false);
        var external = ext.Ok && !string.IsNullOrWhiteSpace(ext.Text);

        var filterAttrs = false;
        var attrFile = Path.Combine(workingTree, ".gitattributes");
        if (File.Exists(attrFile))
        {
            var text = await File.ReadAllTextAsync(attrFile, ct).ConfigureAwait(false);
            if (Regex.IsMatch(text, @"\bfilter\s*=", RegexOptions.IgnoreCase))
                filterAttrs = true;
        }

        var summary = hooks || signing || external || filterAttrs
            ? $"Assessed: hooks={(hooks ? "present (will disable via core.hooksPath)" : "none")}, " +
              $"signing={(signing ? "on (will disable for guided commit)" : "off")}, " +
              $"externalDiff={(external ? "set (diffs use --no-ext-diff)" : "off")}, " +
              $"filters={(filterAttrs ? "gitattributes filter lines - matching paths refused" : "none detected")}."
            : "Assessed: no hooks/signing/external-diff/filter attributes detected.";

        return new GitSafetyReport(hooks, signing, external, filterAttrs, summary, HooksLimitsCopy);
    }

    public async Task<GitOpResult> StageExactAsync(
        string workingTree,
        PathBoundary grantBoundary,
        IReadOnlyList<string> relativePaths,
        CancellationToken ct = default)
    {
        var gate = await PreflightAsync(workingTree, grantBoundary, ct).ConfigureAwait(false);
        if (!gate.Ok) return gate;

        if (relativePaths.Count == 0)
            return new GitOpResult(false, "Select at least one file to stage.");

        var validated = new List<string>();
        foreach (var raw in relativePaths)
        {
            var check = await ValidatePathForIndexAsync(workingTree, grantBoundary, raw, forStage: true, ct).ConfigureAwait(false);
            if (!check.Ok) return check;
            validated.Add(NormalizeRel(raw));
        }

        var args = new List<string> { "add", "--" };
        args.AddRange(validated);
        var run = await RunAsync(workingTree, args.ToArray(), ct).ConfigureAwait(false);
        return run.Ok
            ? new GitOpResult(true, Detail: $"Staged {validated.Count} path(s).")
            : new GitOpResult(false, run.Error ?? "git add failed.");
    }

    public async Task<GitOpResult> UnstageExactAsync(
        string workingTree,
        PathBoundary grantBoundary,
        IReadOnlyList<string> relativePaths,
        CancellationToken ct = default)
    {
        var gate = await PreflightAsync(workingTree, grantBoundary, ct).ConfigureAwait(false);
        if (!gate.Ok) return gate;

        if (relativePaths.Count == 0)
            return new GitOpResult(false, "Select at least one file to unstage.");

        var validated = new List<string>();
        foreach (var raw in relativePaths)
        {
            if (!IsSafePathspec(raw))
                return new GitOpResult(false, "Rejected unsafe pathspec.");
            if (!grantBoundary.TryResolve(raw, out _, out var deny))
                return new GitOpResult(false, deny ?? "Path outside grant.");
            validated.Add(NormalizeRel(raw));
        }

        // restore --staged works on modern git; fall back message if unavailable.
        var args = new List<string> { "restore", "--staged", "--" };
        args.AddRange(validated);
        var run = await RunAsync(workingTree, args.ToArray(), ct).ConfigureAwait(false);
        if (!run.Ok)
        {
            var reset = new List<string> { "reset", "HEAD", "--" };
            reset.AddRange(validated);
            run = await RunAsync(workingTree, reset.ToArray(), ct).ConfigureAwait(false);
        }
        return run.Ok
            ? new GitOpResult(true, Detail: $"Unstaged {validated.Count} path(s).")
            : new GitOpResult(false, run.Error ?? "Unstage failed.");
    }

    /// <summary>
    /// Local commit of exact paths only. Never claims push. Requires confirmed=true and a meaningful message.
    /// Refuses when unrelated index entries would be recorded, conflicts exist, or secrets are present.
    /// </summary>
    public async Task<GitCommitResult> SaveCheckpointAsync(
        string workingTree,
        PathBoundary grantBoundary,
        IReadOnlyList<string> relativePaths,
        string message,
        bool confirmed,
        CancellationToken ct = default)
    {
        if (!confirmed)
            return new GitCommitResult(false, Error: "Save checkpoint requires explicit confirmation.");

        var msg = (message ?? "").Trim();
        if (msg.Length < 3)
            return new GitCommitResult(false, Error: "Enter a meaningful commit message (at least 3 characters).");
        if (msg.Contains('\0'))
            return new GitCommitResult(false, Error: "Commit message rejected.");

        var gate = await PreflightAsync(workingTree, grantBoundary, ct).ConfigureAwait(false);
        if (!gate.Ok)
            return new GitCommitResult(false, Error: gate.Error);

        if (relativePaths.Count == 0)
            return new GitCommitResult(false, Error: "Select the exact files to include in this checkpoint.");

        var intended = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in relativePaths)
        {
            var check = await ValidatePathForIndexAsync(workingTree, grantBoundary, raw, forStage: true, ct).ConfigureAwait(false);
            if (!check.Ok)
                return new GitCommitResult(false, Error: check.Error);
            intended.Add(NormalizeRel(raw));
        }

        var snap = await new GitChangesReader(gitExecutable).ReadAsync(workingTree, ct).ConfigureAwait(false);
        if (!snap.Ok)
            return new GitCommitResult(false, Error: snap.Error ?? "Could not re-read index.");

        foreach (var e in snap.Entries)
        {
            if (ConflictCode.IsMatch(e.StatusCode))
                return new GitCommitResult(false, Error: $"Conflicted path present ({e.Path}). Resolve conflicts before saving a checkpoint.");
        }

        var stagedOthers = snap.Entries
            .Where(e => e.InIndex && !intended.Contains(e.Path))
            .Select(e => e.Path)
            .ToList();
        if (stagedOthers.Count > 0)
            return new GitCommitResult(false,
                Error: "Index has other staged paths not in this checkpoint. Unstage them first so unrelated material is not committed: "
                       + string.Join(", ", stagedOthers.Take(8))
                       + (stagedOthers.Count > 8 ? "â€¦" : ""));

        // Ensure intended paths are staged (exact add - never -A / .).
        var stage = await StageExactAsync(workingTree, grantBoundary, relativePaths, ct).ConfigureAwait(false);
        if (!stage.Ok)
            return new GitCommitResult(false, Error: stage.Error);

        // Re-check index after stage (race).
        snap = await new GitChangesReader(gitExecutable).ReadAsync(workingTree, ct).ConfigureAwait(false);
        if (!snap.Ok)
            return new GitCommitResult(false, Error: snap.Error ?? "Index re-check failed.");
        stagedOthers = snap.Entries
            .Where(e => e.InIndex && !intended.Contains(e.Path))
            .Select(e => e.Path)
            .ToList();
        if (stagedOthers.Count > 0)
            return new GitCommitResult(false, Error: "Index changed during checkpoint - refused unrelated staged paths.");

        var commitArgs = new List<string> { "commit", "-m", msg, "--" };
        commitArgs.AddRange(intended.OrderBy(p => p, StringComparer.Ordinal));
        var run = await RunAsync(workingTree, commitArgs.ToArray(), ct).ConfigureAwait(false);
        if (!run.Ok)
            return new GitCommitResult(false, Error: run.Error ?? "git commit failed.", PublishedOrPushed: false);

        var sha = await RunAsync(workingTree, ["rev-parse", "--short", "HEAD"], ct).ConfigureAwait(false);
        return new GitCommitResult(true, Sha: sha.Ok ? sha.Text.Trim() : null, PublishedOrPushed: false);
    }

    public async Task<GitWorktreeListResult> ListWorktreesAsync(string workingTree, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workingTree) || !Directory.Exists(workingTree))
            return new GitWorktreeListResult(false, [], "Working tree path missing.");

        var run = await RunAsync(workingTree, ["worktree", "list", "--porcelain"], ct).ConfigureAwait(false);
        if (!run.Ok)
            return new GitWorktreeListResult(false, [], run.Error ?? "worktree list failed.");

        var entries = new List<GitWorktreeEntry>();
        string? path = null, head = "", branch = "";
        var bare = false;
        var locked = false;
        void Flush()
        {
            if (path is null) return;
            entries.Add(new GitWorktreeEntry(path, head, branch, bare, locked));
            path = null; head = ""; branch = ""; bare = false; locked = false;
        }
        using var reader = new StringReader(run.Text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) path = line["worktree ".Length..];
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal)) head = line["HEAD ".Length..];
            else if (line.StartsWith("branch ", StringComparison.Ordinal)) branch = line["branch ".Length..];
            else if (line == "bare") bare = true;
            else if (line.StartsWith("locked", StringComparison.Ordinal)) locked = true;
        }
        Flush();
        return new GitWorktreeListResult(true, entries);
    }

    /// <summary>
    /// Create a separate working copy. Destination must be an explicit consented absolute folder
    /// (separate from silently extending the source grant), empty/nonexistent leaf, not inside the source tree.
    /// </summary>
    public async Task<GitOpResult> CreateWorktreeAsync(
        string sourceWorkingTree,
        PathBoundary sourceGrant,
        string destinationAbsolute,
        bool destinationConsented,
        string newBranchName,
        string baseRevision,
        CancellationToken ct = default)
    {
        if (!destinationConsented)
            return new GitOpResult(false, "Separate working copy destination requires explicit folder consent.");

        var gate = await PreflightAsync(sourceWorkingTree, sourceGrant, ct).ConfigureAwait(false);
        if (!gate.Ok) return gate;

        if (string.IsNullOrWhiteSpace(newBranchName) || !Regex.IsMatch(newBranchName, @"^[A-Za-z0-9._/-]+$") || newBranchName.StartsWith("-", StringComparison.Ordinal))
            return new GitOpResult(false, "Branch name rejected. Use letters, digits, . _ / - only.");
        if (string.IsNullOrWhiteSpace(baseRevision) || baseRevision.StartsWith("-", StringComparison.Ordinal) || baseRevision.Contains('\0'))
            return new GitOpResult(false, "Base revision rejected.");
        // Refuse option-like and path injection in revision (keep to rev-parse-able tokens).
        if (!Regex.IsMatch(baseRevision, @"^[A-Za-z0-9._/-]+$"))
            return new GitOpResult(false, "Base revision must be a simple ref or SHA.");

        string dest;
        try { dest = Path.GetFullPath(destinationAbsolute.Trim()); }
        catch (Exception ex) { return new GitOpResult(false, "Destination path invalid: " + ex.Message); }

        if (dest.StartsWith(@"\\", StringComparison.Ordinal) || dest.StartsWith("//", StringComparison.Ordinal))
            return new GitOpResult(false, "UNC destinations are not allowed.");
        if (HasAds(dest))
            return new GitOpResult(false, "Alternate data streams are not allowed.");

        var destCanon = PathBoundary.CanonicalDirectory(dest)
                        ?? (Directory.Exists(Path.GetDirectoryName(dest)!)
                            ? Path.GetFullPath(dest)
                            : null);
        if (destCanon is null && !Directory.Exists(Path.GetDirectoryName(dest) ?? ""))
            return new GitOpResult(false, "Destination parent folder must exist.");
        destCanon ??= Path.GetFullPath(dest);

        var sourceCanon = PathBoundary.CanonicalDirectory(sourceWorkingTree) ?? Path.GetFullPath(sourceWorkingTree);
        if (IsUnderOrEqual(destCanon, sourceCanon))
            return new GitOpResult(false, "Destination cannot be inside the source working copy.");

        // Destination must NOT silently use the source grant - require it outside grant root
        // OR under a separately consented path (picker consent already provided). Still refuse
        // if destination resolves via reparse outside the consented parent.
        var parent = Path.GetDirectoryName(destCanon);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            return new GitOpResult(false, "Destination parent folder must exist.");

        var parentCanon = PathBoundary.CanonicalDirectory(parent);
        if (parentCanon is null)
            return new GitOpResult(false, "Could not canonicalize destination parent.");
        // Junction escape: leaf must stay under consented parent after resolve.
        if (Directory.Exists(destCanon))
        {
            if (Directory.EnumerateFileSystemEntries(destCanon).Any())
                return new GitOpResult(false, "Destination folder must be empty.");
        }
        else if (File.Exists(destCanon))
            return new GitOpResult(false, "Destination path is an existing file.");

        var existing = await ListWorktreesAsync(sourceWorkingTree, ct).ConfigureAwait(false);
        if (existing.Ok && existing.Entries.Any(e =>
                string.Equals(TrimSlash(e.Path), TrimSlash(destCanon), StringComparison.OrdinalIgnoreCase)))
            return new GitOpResult(false, "A worktree already exists at that path.");

        var verify = await RunAsync(sourceWorkingTree, ["rev-parse", "--verify", baseRevision], ct).ConfigureAwait(false);
        if (!verify.Ok)
            return new GitOpResult(false, "Base revision not found: " + (verify.Error ?? baseRevision));

        var args = new[] { "worktree", "add", "-b", newBranchName, destCanon, baseRevision };
        var run = await RunAsync(sourceWorkingTree, args, ct).ConfigureAwait(false);
        return run.Ok
            ? new GitOpResult(true, Detail: $"Created separate working copy at {destCanon} on branch {newBranchName}.")
            : new GitOpResult(false, run.Error ?? "worktree add failed.");
    }

    public async Task<GitOpResult> RemoveWorktreeAsync(
        string sourceWorkingTree,
        PathBoundary sourceGrant,
        string worktreePath,
        bool confirmed,
        bool force = false,
        CancellationToken ct = default)
    {
        if (!confirmed)
            return new GitOpResult(false, "Removing a separate working copy requires explicit confirmation.");
        if (force)
            return new GitOpResult(false, "Force-remove is not offered by default.");

        var gate = await PreflightAsync(sourceWorkingTree, sourceGrant, ct).ConfigureAwait(false);
        if (!gate.Ok) return gate;

        string dest;
        try { dest = Path.GetFullPath(worktreePath.Trim()); }
        catch (Exception ex) { return new GitOpResult(false, ex.Message); }

        var sourceCanon = PathBoundary.CanonicalDirectory(sourceWorkingTree) ?? Path.GetFullPath(sourceWorkingTree);
        if (string.Equals(TrimSlash(dest), TrimSlash(sourceCanon), StringComparison.OrdinalIgnoreCase))
            return new GitOpResult(false, "Refusing to remove the source checkout.");

        // Dirty refusal: if status inside worktree is non-empty, deny.
        if (Directory.Exists(dest))
        {
            var dirty = await RunAsync(dest, ["status", "--porcelain=v1"], ct).ConfigureAwait(false);
            if (dirty.Ok && !string.IsNullOrWhiteSpace(dirty.Text))
                return new GitOpResult(false, "Working copy has uncommitted changes - remove refused (no force). Commit, stash, or clean it first.");
        }

        var run = await RunAsync(sourceWorkingTree, ["worktree", "remove", dest], ct).ConfigureAwait(false);
        return run.Ok
            ? new GitOpResult(true, Detail: "Removed separate working copy (branch left intact).")
            : new GitOpResult(false, run.Error ?? "worktree remove failed.");
    }

    private async Task<GitOpResult> PreflightAsync(string workingTree, PathBoundary grantBoundary, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workingTree) || !Directory.Exists(workingTree))
            return new GitOpResult(false, "Working tree path missing.");
        if (!PathBoundary.AreSameRoot(grantBoundary.Root, workingTree) &&
            !IsUnderOrEqual(workingTree, grantBoundary.Root))
            return new GitOpResult(false, "Git root is outside the folder grant.");
        if (!Directory.Exists(Path.Combine(workingTree, ".git")) &&
            !File.Exists(Path.Combine(workingTree, ".git")))
            return new GitOpResult(false, "Not a Git repository.");
        _ = await AssessAsync(workingTree, ct).ConfigureAwait(false);
        return new GitOpResult(true);
    }

    private async Task<GitOpResult> ValidatePathForIndexAsync(
        string workingTree,
        PathBoundary grantBoundary,
        string relativePath,
        bool forStage,
        CancellationToken ct)
    {
        if (!IsSafePathspec(relativePath))
            return new GitOpResult(false, "Rejected unsafe pathspec (options/traversal/absolute).");

        var rel = NormalizeRel(relativePath);
        if (!grantBoundary.TryResolve(rel, out var canonical, out var deny))
            return new GitOpResult(false, deny ?? "Path outside grant.");

        var leaf = Path.GetFileName(canonical);
        if (SensitivePathPolicy.IsSensitiveLeaf(leaf))
            return new GitOpResult(false,
                $"Refused to {(forStage ? "stage" : "include")} â€œ{leaf}â€ - credential-shaped paths stay excluded. {SecretContentReview.LimitsCopy}");

        if (File.Exists(canonical) && SensitivePathPolicy.IsTextPreviewable(canonical))
        {
            try
            {
                var text = await File.ReadAllTextAsync(canonical, ct).ConfigureAwait(false);
                var review = SecretContentReview.Inspect(text, leaf);
                if (review.Suspicious)
                    return new GitOpResult(false,
                        $"Refused to {(forStage ? "stage" : "include")} â€œ{leaf}â€: {review.UserMessage} Acknowledgement does not stage or share keys.");
            }
            catch { /* binary / locked - still allow non-sensitive names; heuristics incomplete */ }
        }

        // Refuse when .gitattributes assigns a filter to this path.
        var attr = await RunAsync(workingTree, ["check-attr", "filter", "--", rel], ct).ConfigureAwait(false);
        if (attr.Ok && attr.Text.Contains("filter: ", StringComparison.Ordinal) &&
            !attr.Text.Contains("filter: unspecified", StringComparison.OrdinalIgnoreCase) &&
            !attr.Text.Contains("filter: unset", StringComparison.OrdinalIgnoreCase))
        {
            return new GitOpResult(false,
                $"Refused â€œ{rel}â€ because a clean/smudge filter is configured - residual execution risk. {HooksLimitsCopy}");
        }

        return new GitOpResult(true);
    }

    private async Task<(bool Ok, string Text, string? Error)> RunAsync(
        string workingTree,
        string[] gitArgs,
        CancellationToken ct,
        bool safetyOverrides = true)
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

        // Never interpolate a shell command. Owned empty hooks + pinned env; no shared contaminated temp.
        GitSafeInvocation.ApplyPinnedEnvironment(psi, promptNever: false);
        if (safetyOverrides)
            GitSafeInvocation.ApplySafetyArguments(psi, forGuidedPush: false);

        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(workingTree);

        // Prefer --no-ext-diff on diff-like commands.
        if (gitArgs.Length > 0 && gitArgs[0] is "diff" or "show")
        {
            psi.ArgumentList.Add(gitArgs[0]);
            psi.ArgumentList.Add("--no-ext-diff");
            for (var i = 1; i < gitArgs.Length; i++)
                psi.ArgumentList.Add(gitArgs[i]);
        }
        else
        {
            foreach (var a in gitArgs)
                psi.ArgumentList.Add(a);
        }

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

    private static string NormalizeRel(string p) => p.Replace('\\', '/').Trim().TrimStart('/');

    private static bool IsUnderOrEqual(string path, string root)
    {
        var p = TrimSlash(Path.GetFullPath(path));
        var r = TrimSlash(Path.GetFullPath(root));
        if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase)) return true;
        return p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAds(string path)
    {
        var start = path.Length >= 2 && path[1] == ':' ? 2 : 0;
        return path.IndexOf(':', start) >= 0;
    }

    private static string TrimSlash(string p) =>
        p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
