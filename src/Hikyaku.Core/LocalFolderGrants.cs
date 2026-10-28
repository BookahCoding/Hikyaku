using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

public enum FolderGrantMode
{
    Read,
    ReadWrite
}

public sealed class FolderGrantException(string message) : Exception(message);

/// <summary>Persisted per (gateway, profile) local folder grant. Files live on this PC — not the remote gateway.</summary>
public sealed record LocalFolderGrant(
    string GatewayId,
    string ProfileId,
    string CanonicalRoot,
    FolderGrantMode Mode,
    string MachineId,
    DateTimeOffset GrantedAtUtc);

public sealed class LocalFolderGrantStore(IPreferenceStore store, Func<string> machineId)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<LocalFolderGrant?> GetAsync(ProfileKey key, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct).ConfigureAwait(false);
        return all.TryGetValue(KeyOf(key), out var g) ? g : null;
    }

    public async Task<LocalFolderGrant> GrantAsync(ProfileKey key, string folderPath, FolderGrantMode mode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            throw new FolderGrantException("Choose a folder on this PC.");
        if (folderPath.StartsWith(@"\\", StringComparison.Ordinal) || folderPath.StartsWith("//", StringComparison.Ordinal))
            throw new FolderGrantException("Network (UNC) roots are not allowed.");

        string full;
        try { full = Path.GetFullPath(folderPath.Trim()); }
        catch (Exception ex) { throw new FolderGrantException("Could not resolve that path: " + ex.Message); }

        if (!Directory.Exists(full))
            throw new FolderGrantException("That folder does not exist on this PC.");

        var canonical = PathBoundary.CanonicalDirectory(full)
            ?? throw new FolderGrantException("Could not resolve a stable path for that folder.");

        if (IsWholeVolumeOrHome(canonical))
            throw new FolderGrantException("Whole drives and your user home folder cannot be granted. Pick a project subfolder.");

        var grant = new LocalFolderGrant(key.GatewayId, key.ProfileId, canonical, mode, machineId(), DateTimeOffset.UtcNow);
        var all = await LoadAsync(ct).ConfigureAwait(false);
        all[KeyOf(key)] = grant;
        await SaveAsync(all, ct).ConfigureAwait(false);
        return grant;
    }

    public async Task RevokeAsync(ProfileKey key, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct).ConfigureAwait(false);
        if (!all.Remove(KeyOf(key))) return;
        await SaveAsync(all, ct).ConfigureAwait(false);
    }

    private static string KeyOf(ProfileKey key) => key.GatewayId + "\u001f" + key.ProfileId;

    private static bool IsWholeVolumeOrHome(string canonical)
    {
        var root = Path.GetPathRoot(canonical);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(TrimSlash(canonical), TrimSlash(root), StringComparison.OrdinalIgnoreCase))
            return true;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var homeCanon = PathBoundary.CanonicalDirectory(home) ?? Path.GetFullPath(home);
            if (string.Equals(TrimSlash(canonical), TrimSlash(homeCanon), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string TrimSlash(string p) => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private async Task<Dictionary<string, LocalFolderGrant>> LoadAsync(CancellationToken ct)
    {
        var raw = await store.LoadAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw)) return new(StringComparer.Ordinal);
        try
        {
            var dto = JsonSerializer.Deserialize<GrantFile>(raw, JsonOpts) ?? new();
            var map = new Dictionary<string, LocalFolderGrant>(StringComparer.Ordinal);
            foreach (var g in dto.Grants)
            {
                if (string.IsNullOrWhiteSpace(g.GatewayId) || string.IsNullOrWhiteSpace(g.ProfileId) ||
                    string.IsNullOrWhiteSpace(g.CanonicalRoot)) continue;
                map[g.GatewayId + "\u001f" + g.ProfileId] = g;
            }
            return map;
        }
        catch (JsonException) { return new(StringComparer.Ordinal); }
    }

    private Task SaveAsync(Dictionary<string, LocalFolderGrant> all, CancellationToken ct)
        => store.SaveAsync(JsonSerializer.Serialize(new GrantFile { Grants = all.Values.ToList() }, JsonOpts), ct);

    private sealed class GrantFile
    {
        public List<LocalFolderGrant> Grants { get; set; } = new();
    }
}

/// <summary>Canonical path checks — refuse traversal, UNC, ADS, and reparse escapes outside the grant root.</summary>
public sealed class PathBoundary
{
    private readonly string _root;

    public PathBoundary(string grantRoot)
    {
        _root = CanonicalDirectory(grantRoot)
            ?? throw new ArgumentException("Grant root is not a usable directory.", nameof(grantRoot));
    }

    public string Root => _root;

    public static bool AreSameRoot(string a, string b) =>
        string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);

    public bool TryResolve(string candidate, out string canonical, out string? denyReason)
    {
        canonical = "";
        denyReason = null;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            denyReason = "Empty path.";
            return false;
        }

        var raw = candidate.Trim();
        if (raw.StartsWith(@"\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal))
        {
            denyReason = "UNC / network paths are outside local grants.";
            return false;
        }

        if (HasAlternateDataStream(raw))
        {
            denyReason = "Alternate data streams are not allowed.";
            return false;
        }

        string full;
        try
        {
            full = Path.IsPathRooted(raw)
                ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(_root, raw));
        }
        catch (Exception)
        {
            denyReason = "Path could not be resolved.";
            return false;
        }

        if (HasAlternateDataStream(full))
        {
            denyReason = "Alternate data streams are not allowed.";
            return false;
        }

        var resolved = ResolveFinal(full);
        if (resolved is null)
        {
            denyReason = "Path could not be canonicalized.";
            return false;
        }

        if (!IsUnderRoot(resolved, _root))
        {
            denyReason = "Path leaves the granted folder.";
            return false;
        }

        canonical = resolved;
        return true;
    }

    public static string? CanonicalDirectory(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!Directory.Exists(full)) return null;
            return ResolveFinal(full) ?? full;
        }
        catch { return null; }
    }

    private static string? ResolveFinal(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                var target = di.ResolveLinkTarget(returnFinalTarget: true);
                return Path.GetFullPath(target?.FullName ?? di.FullName);
            }
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                var target = fi.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null) return Path.GetFullPath(target.FullName);
                // Resolve parent junctions even when the leaf is a normal file.
                var parent = CanonicalDirectory(fi.DirectoryName ?? Path.GetDirectoryName(path)!)
                    ?? Path.GetFullPath(fi.DirectoryName!);
                return Path.GetFullPath(Path.Combine(parent, fi.Name));
            }

            // Target may not exist yet (create/overwrite) — resolve existing parents.
            var dir = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return Path.GetFullPath(path);
            var parentDir = CanonicalDirectory(dir) ?? Path.GetFullPath(dir);
            return Path.GetFullPath(Path.Combine(parentDir, name));
        }
        catch { return null; }
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var p = Trim(path);
        var r = Trim(root);
        if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = r + Path.DirectorySeparatorChar;
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAlternateDataStream(string path)
    {
        // Drive letter colon at index 1 is fine; any later colon is an ADS / device hint on Windows.
        var start = path.Length >= 2 && path[1] == ':' ? 2 : 0;
        return path.IndexOf(':', start) >= 0;
    }

    private static string Trim(string p) => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

public static class SensitivePathPolicy
{
    private static readonly Regex SensitiveLeaf = new(
        @"^(?i)(\.env(\..*)?|.*\.(pem|key|pfx|snk|p12)|(.*credentials.*)|(.*secret.*)|id_rsa|id_ed25519|id_ecdsa|Cookies|login data|Local State)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".cs", ".json", ".xml", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".config",
        ".csproj", ".sln", ".props", ".targets", ".gitignore", ".editorconfig", ".ps1", ".sh", ".bat",
        ".cmd", ".js", ".ts", ".tsx", ".jsx", ".css", ".html", ".py", ".rs", ".go", ".java", ".kt"
    };

    public static bool IsSensitiveLeaf(string leafName)
    {
        var leaf = leafName.Replace('\\', '/');
        var i = leaf.LastIndexOf('/');
        if (i >= 0) leaf = leaf[(i + 1)..];
        return SensitiveLeaf.IsMatch(leaf.Trim());
    }

    public static bool IsTextPreviewable(string path)
    {
        var leaf = Path.GetFileName(path);
        if (IsSensitiveLeaf(leaf)) return false;
        if (leaf.StartsWith(".", StringComparison.Ordinal) && !TextExtensions.Contains(Path.GetExtension(leaf))
            && leaf is not ".gitignore" and not ".editorconfig")
            return false;
        var ext = Path.GetExtension(leaf);
        return string.IsNullOrEmpty(ext) || TextExtensions.Contains(ext);
    }
}

public sealed record SecretReview(bool Suspicious, string Category, string UserMessage);

public static class SecretContentReview
{
    private static readonly (string Category, string Pattern)[] Patterns =
    [
        ("private key block", @"-----BEGIN [A-Z ]*PRIVATE KEY-----"),
        ("bearer/api token", @"(?i)(sk-[A-Za-z0-9]{20,}|ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|xox[baprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16})"),
        ("assigned secret", @"(?i)(password|passwd|secret|api[_-]?key|token)\s*[:=]\s*[""'][^""']{8,}[""']")
    ];

    public const string LimitsCopy =
        "We try to spot secrets; we can miss some. You are responsible for what you send.";

    public const string KeysExclusionCopy =
        "Credential-shaped paths and suspected keys stay blocked from share and stage. " +
        "Acknowledging a warning only records that you saw it — it does not share, stage, or upload the secret. " +
        "Detection is incomplete.";

    public static SecretReview Inspect(string content, string displayName)
    {
        foreach (var (category, pattern) in Patterns)
        {
            if (Regex.IsMatch(content, pattern))
                return new SecretReview(true, category,
                    $"Possible {category} in {displayName}. {LimitsCopy} {KeysExclusionCopy}");
        }
        return new SecretReview(false, "", LimitsCopy);
    }
}

public sealed record ShareDecision(
    bool Allowed,
    bool RequiresAcknowledge,
    DateTimeOffset? ConsentTimestampUtc,
    string Message,
    bool HardBlocked = false);

public static class ProjectShareGate
{
    /// <summary>
    /// Keys / credential leaves and secret-heuristic hits are hard-blocked.
    /// Acknowledge never upgrades them to Allowed (keys-exclusion objective).
    /// </summary>
    public static ShareDecision Decide(string content, string displayName, bool acknowledgeRisk)
    {
        if (SensitivePathPolicy.IsSensitiveLeaf(displayName))
        {
            _ = acknowledgeRisk; // may record UI understanding only — never allows share
            return new ShareDecision(false, false, acknowledgeRisk ? DateTimeOffset.UtcNow : null,
                $"“{displayName}” looks like credential material and cannot be shared. {SecretContentReview.KeysExclusionCopy}",
                HardBlocked: true);
        }

        var review = SecretContentReview.Inspect(content, displayName);
        if (review.Suspicious)
        {
            _ = acknowledgeRisk;
            return new ShareDecision(false, false, acknowledgeRisk ? DateTimeOffset.UtcNow : null,
                review.UserMessage + " Sharing remains blocked.",
                HardBlocked: true);
        }
        return new ShareDecision(true, false, null, review.UserMessage);
    }
}

public sealed record MutationResult(bool Ok, string? Error = null);

/// <summary>
/// Overwrite/delete require an explicit per-call confirmation. No automatic backups.
/// Independent of Smart/Manual/Off and per-chat YOLO — those never bypass this gate.
/// </summary>
public sealed class FileMutationGate(PathBoundary boundary, FolderGrantMode mode)
{
    public MutationResult Overwrite(string path, byte[] bytes, bool confirmed)
    {
        if (mode != FolderGrantMode.ReadWrite)
            return new MutationResult(false, "This folder grant is read-only.");
        if (!confirmed)
            return new MutationResult(false, "Overwrite requires an explicit confirmation for this path.");
        if (!boundary.TryResolve(path, out var canonical, out var deny))
            return new MutationResult(false, deny);
        if (SensitivePathPolicy.IsSensitiveLeaf(Path.GetFileName(canonical)))
            return new MutationResult(false, "Sensitive credential paths cannot be overwritten through Projects.");
        try
        {
            var dir = Path.GetDirectoryName(canonical);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(canonical, bytes);
            return new MutationResult(true);
        }
        catch (Exception ex) { return new MutationResult(false, ex.Message); }
    }

    public MutationResult Delete(string path, bool confirmed)
    {
        if (mode != FolderGrantMode.ReadWrite)
            return new MutationResult(false, "This folder grant is read-only.");
        if (!confirmed)
            return new MutationResult(false, "Delete requires an explicit confirmation for this path.");
        if (!boundary.TryResolve(path, out var canonical, out var deny))
            return new MutationResult(false, deny);
        try
        {
            if (File.Exists(canonical)) File.Delete(canonical);
            else if (Directory.Exists(canonical))
                return new MutationResult(false, "Directory delete is not offered in this checkpoint.");
            else return new MutationResult(false, "Path not found.");
            return new MutationResult(true);
        }
        catch (Exception ex) { return new MutationResult(false, ex.Message); }
    }
}

public sealed record ProjectEntry(string Name, string RelativePath, bool IsDirectory, bool Sensitive);

public sealed record ProjectListResult(bool Ok, IReadOnlyList<ProjectEntry> Entries, string? Error = null);

public sealed record ProjectPreviewResult(bool Ok, string? Text = null, string? Error = null);

/// <summary>Browse/preview under a profile's local grant. Selecting a folder never uploads.</summary>
public sealed class ProjectWorkspace(LocalFolderGrantStore grants, ProfileKey profile)
{
    public const int MaxPreviewBytes = 256 * 1024;

    public async Task<ProjectListResult> ListAsync(string relativeDir, CancellationToken ct = default)
    {
        var grant = await grants.GetAsync(profile, ct).ConfigureAwait(false);
        if (grant is null) return new ProjectListResult(false, [], "No folder grant for this profile.");
        if (!Directory.Exists(grant.CanonicalRoot))
            return new ProjectListResult(false, [], "Granted path is missing or moved — grant fails closed until you pick again.");

        var boundary = new PathBoundary(grant.CanonicalRoot);
        if (!boundary.TryResolve(string.IsNullOrWhiteSpace(relativeDir) ? "." : relativeDir, out var dir, out var deny))
            return new ProjectListResult(false, [], deny);
        if (!Directory.Exists(dir))
            return new ProjectListResult(false, [], "Folder not found inside the grant.");

        try
        {
            var entries = new List<ProjectEntry>();
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(d);
                var rel = Rel(grant.CanonicalRoot, d);
                entries.Add(new ProjectEntry(name, rel, true, SensitivePathPolicy.IsSensitiveLeaf(name)));
            }
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(f);
                var rel = Rel(grant.CanonicalRoot, f);
                entries.Add(new ProjectEntry(name, rel, false, SensitivePathPolicy.IsSensitiveLeaf(name)));
            }
            return new ProjectListResult(true, entries.OrderBy(e => e.IsDirectory ? 0 : 1).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch (Exception ex) { return new ProjectListResult(false, [], ex.Message); }
    }

    public async Task<ProjectPreviewResult> PreviewTextAsync(string relativePath, CancellationToken ct = default)
    {
        var grant = await grants.GetAsync(profile, ct).ConfigureAwait(false);
        if (grant is null) return new ProjectPreviewResult(false, Error: "No folder grant for this profile.");
        if (!Directory.Exists(grant.CanonicalRoot))
            return new ProjectPreviewResult(false, Error: "Granted path is missing or moved.");

        if (SensitivePathPolicy.IsSensitiveLeaf(Path.GetFileName(relativePath)))
            return new ProjectPreviewResult(false, Error: "Sensitive credential paths cannot be previewed.");

        var boundary = new PathBoundary(grant.CanonicalRoot);
        if (!boundary.TryResolve(relativePath, out var path, out var deny))
            return new ProjectPreviewResult(false, Error: deny);
        if (!File.Exists(path))
            return new ProjectPreviewResult(false, Error: "File not found.");
        if (!SensitivePathPolicy.IsTextPreviewable(path))
            return new ProjectPreviewResult(false, Error: "Only appropriate text files can be previewed.");

        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxPreviewBytes)
                return new ProjectPreviewResult(false, Error: "File too large to preview.");
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return new ProjectPreviewResult(true, text);
        }
        catch (Exception ex) { return new ProjectPreviewResult(false, Error: ex.Message); }
    }

    public async Task<(LocalFolderGrant? Grant, PathBoundary? Boundary, FileMutationGate? Mutations)> OpenMutationsAsync(CancellationToken ct = default)
    {
        var grant = await grants.GetAsync(profile, ct).ConfigureAwait(false);
        if (grant is null || !Directory.Exists(grant.CanonicalRoot)) return (grant, null, null);
        var boundary = new PathBoundary(grant.CanonicalRoot);
        return (grant, boundary, new FileMutationGate(boundary, grant.Mode));
    }

    private static string Rel(string root, string full)
    {
        var r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
        var f = Path.GetFullPath(full);
        return f.StartsWith(r, StringComparison.OrdinalIgnoreCase)
            ? f[r.Length..].Replace('\\', '/')
            : Path.GetFileName(full);
    }
}
