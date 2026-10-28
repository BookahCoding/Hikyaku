using System.Text;

namespace Hikyaku.Core;

public enum BridgeAsk { None, ChooseFolder, AllowNetwork, AllowKeyFile, ConfirmDelete }

public sealed record BridgeDecision(BridgeAsk Ask, string Path, string Detail);

public sealed record BridgeResult(
    bool Ok,
    string Error,
    string Encoding,
    string Text,
    byte[]? Bytes,
    IReadOnlyList<(string Name, bool Dir)>? Entries)
{
    public static BridgeResult Fail(string error) => new(false, error, "", "", null, null);
    public static BridgeResult Done() => new(true, "", "", "", null, null);
}

/// <summary>Decides and performs one folder-bridge operation on this PC. Network roots are allowed; a link that leaves the folder is not.</summary>
public static class FolderBridgeGate
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static BridgeDecision Decide(BridgeProfile access, string op, string path)
    {
        var shown = Present(path);
        if (shown is null)
            return new BridgeDecision(BridgeAsk.None, path ?? "", "invalid");
        if (!TryMatch(access, shown, out var root, out var final, out var problem))
            return problem ?? new BridgeDecision(IsNetwork(shown) ? BridgeAsk.AllowNetwork : BridgeAsk.ChooseFolder, shown, "");

        if (problem is not null) return problem;

        if (IsKeyBlocked(access, final, op))
            return new BridgeDecision(BridgeAsk.AllowKeyFile, final, Path.GetFileName(final));

        if (op is "delete_file" or "delete_folder")
        {
            var detail = op == "delete_folder"
                ? "Delete this folder. Everything inside it goes."
                : "Delete this file.";
            return new BridgeDecision(BridgeAsk.ConfirmDelete, final, detail);
        }

        _ = root;
        return new BridgeDecision(BridgeAsk.None, final, "");
    }

    public static BridgeResult Apply(BridgeProfile access, string op, string path, byte[]? write, bool deleteYes)
    {
        var shown = Present(path);
        if (shown is null || HasAds(shown)) return BridgeResult.Fail("invalid");
        if (op is not ("list" or "read" or "write" or "delete_file" or "delete_folder"))
            return BridgeResult.Fail("invalid");

        if (!TryMatch(access, shown, out var root, out var final, out var problem))
        {
            if (problem is not null) return BridgeResult.Fail(problem.Detail.Length > 0 ? problem.Detail : "outside");
            return BridgeResult.Fail("missing");
        }

        if (!RootExists(root)) return BridgeResult.Fail("missing");
        if (IsKeyBlocked(access, final, op)) return BridgeResult.Fail("blocked");
        if (!IsUnder(final, RootFinal(root))) return BridgeResult.Fail("outside");

        try
        {
            return op switch
            {
                "list" => List(final),
                "read" => Read(final),
                "write" => Write(root, final, write),
                "delete_file" => DeleteFile(final, deleteYes),
                "delete_folder" => DeleteFolder(final, deleteYes),
                _ => BridgeResult.Fail("invalid")
            };
        }
        catch (Exception) { return BridgeResult.Fail("invalid"); }
    }

    private static bool IsKeyBlocked(BridgeProfile access, string final, string op)
    {
        if (op is not ("read" or "write")) return false;
        if (!SensitivePathPolicy.IsSensitiveLeaf(final)) return false;
        if (access.KeyFilesOpen) return false;
        return !access.KeyFiles.Any(f => string.Equals(f.CanonicalPath, final, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFullPath(f.CanonicalPath), final, StringComparison.OrdinalIgnoreCase));
    }

    private static BridgeResult List(string dir)
    {
        if (!Directory.Exists(dir)) return BridgeResult.Fail("missing");
        var entries = Directory.EnumerateFileSystemEntries(dir)
            .Select(p => (Name: Path.GetFileName(p), Dir: Directory.Exists(p)))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new BridgeResult(true, "", "", "", null, entries);
    }

    private static BridgeResult Read(string file)
    {
        if (!File.Exists(file)) return BridgeResult.Fail("missing");
        var bytes = File.ReadAllBytes(file);
        try
        {
            var text = Utf8.GetString(bytes);
            return new BridgeResult(true, "", "utf-8", text, bytes, null);
        }
        catch (DecoderFallbackException)
        {
            return new BridgeResult(true, "", "base64", "", bytes, null);
        }
    }

    private static BridgeResult Write(BridgeFolder root, string final, byte[]? write)
    {
        if (write is null) return BridgeResult.Fail("invalid");
        var parent = Path.GetDirectoryName(final);
        if (string.IsNullOrEmpty(parent) || !IsUnder(parent, RootFinal(root)) && !Same(parent, RootFinal(root)))
            return BridgeResult.Fail("outside");
        Directory.CreateDirectory(parent);
        if (!IsUnder(FinalOf(parent) ?? parent, RootFinal(root))) return BridgeResult.Fail("outside");
        File.WriteAllBytes(final, write);
        return BridgeResult.Done();
    }

    private static BridgeResult DeleteFile(string final, bool yes)
    {
        if (!yes) return BridgeResult.Fail("cancelled");
        if (!File.Exists(final)) return BridgeResult.Fail("missing");
        File.Delete(final);
        return BridgeResult.Done();
    }

    private static BridgeResult DeleteFolder(string final, bool yes)
    {
        if (!yes) return BridgeResult.Fail("cancelled");
        if (!Directory.Exists(final)) return BridgeResult.Fail("missing");
        Directory.Delete(final, recursive: true);
        return BridgeResult.Done();
    }

    private static bool TryMatch(BridgeProfile access, string path, out BridgeFolder root, out string final, out BridgeDecision? problem)
    {
        root = null!;
        final = path ?? "";
        problem = null;
        if (string.IsNullOrWhiteSpace(path) || HasAds(path))
        {
            problem = new BridgeDecision(BridgeAsk.None, path ?? "", "invalid");
            return false;
        }

        foreach (var folder in access.Folders)
        {
            if (!Contained(folder, path, out var resolved)) continue;
            root = folder;
            final = resolved;
            if (!IsUnder(resolved, RootFinal(folder)))
            {
                problem = new BridgeDecision(BridgeAsk.None, path, "outside");
                return false;
            }
            return true;
        }

        return false;
    }

    private static bool Contained(BridgeFolder folder, string path, out string resolved)
    {
        resolved = path;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return false; }
        var final = FinalOf(full) ?? full;
        resolved = final;
        var root = RootFinal(folder);
        return IsUnder(final, root) || IsUnder(full, root);
    }

    private static string RootFinal(BridgeFolder folder)
    {
        try
        {
            var full = Path.GetFullPath(folder.CanonicalRoot);
            return FinalOf(full) ?? full;
        }
        catch { return folder.CanonicalRoot; }
    }

    private static bool RootExists(BridgeFolder folder)
    {
        if (folder.Network || IsNetwork(folder.CanonicalRoot))
            return Directory.Exists(folder.CanonicalRoot);
        return Directory.Exists(folder.CanonicalRoot);
    }

    private static string? FinalOf(string path)
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
                var parent = Path.GetDirectoryName(path);
                var parentFinal = parent is null ? null : FinalOf(parent);
                return Path.GetFullPath(Path.Combine(parentFinal ?? parent ?? "", fi.Name));
            }
            var dir = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return Path.GetFullPath(path);
            var parentDir = Directory.Exists(dir) ? FinalOf(dir) ?? Path.GetFullPath(dir) : Path.GetFullPath(dir);
            return Path.GetFullPath(Path.Combine(parentDir, name));
        }
        catch { return null; }
    }

    /// <summary>
    /// The path the person is shown. An extended local path becomes a drive path. An extended UNC path becomes a normal share path.
    /// A device path that is neither is not a folder.
    /// </summary>
    private static string? Present(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var p = path.Replace('/', '\\');
        const string unc = @"\\?\UNC\";
        if (p.StartsWith(unc, StringComparison.OrdinalIgnoreCase))
            p = @"\\" + p[unc.Length..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            var rest = p[4..];
            if (rest.Length >= 2 && char.IsLetter(rest[0]) && rest[1] == ':') p = rest;
            else return null;
        }
        if (HasAds(p)) return null;
        try { return Path.GetFullPath(p); }
        catch { return null; }
    }

    private static bool IsNetwork(string path)
    {
        var p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
            return false;
        return p.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');

    private static bool IsUnder(string path, string root)
    {
        var p = Normalize(path);
        var r = Normalize(root);
        if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase)) return true;
        return p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Same(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static bool HasAds(string path)
    {
        var start = path.Length >= 2 && path[1] == ':' ? 2 : 0;
        return path.IndexOf(':', start) >= 0;
    }
}
