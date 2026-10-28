using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

public sealed record ExtractLimits(int MaxEntries = 2000, long MaxTotalBytes = 400L << 20, long MaxEntryBytes = 200L << 20);

/// <summary>A signature-verified, fully extracted release waiting to be applied.</summary>
public sealed record StagedUpdate(ReleaseVersion Version, string StagingDir)
{
    public const string MarkerName = ".hikyaku-verified.json";

    public static void WriteMarker(string stagingDir, ReleaseVersion version) =>
        File.WriteAllText(Path.Combine(stagingDir, MarkerName),
            JsonSerializer.Serialize(new { format = 1, version = version.ToString() }));

    public static StagedUpdate? ReadMarker(string stagingDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(stagingDir, MarkerName)));
            var v = doc.RootElement.GetProperty("version").GetString();
            return ReleaseVersion.TryParseCurrent(v, out var ver) ? new StagedUpdate(ver, stagingDir) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Extracts a (already signature-verified) zip with traversal, reserved-name and zip-bomb protection.</summary>
public static partial class UpdateStaging
{
    public const string ExeName = "Hikyaku.exe";

    [GeneratedRegex(@"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();

    /// <summary>Extracts into <paramref name="stagingDir"/> (must not exist). On any failure nothing is left behind.</summary>
    public static void Extract(string zipPath, string stagingDir, ExtractLimits? limits = null)
    {
        limits ??= new ExtractLimits();
        if (Directory.Exists(stagingDir)) throw new UpdateException(UpdateFailure.Failed, "Staging folder already exists.");
        var tmp = stagingDir + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.Entries.Count > limits.MaxEntries) throw Bad("The package has too many entries.");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<(ZipArchiveEntry Entry, string Rel)>();
            foreach (var e in zip.Entries)
            {
                var isDir = e.FullName.EndsWith('/') || e.FullName.EndsWith('\\');
                var rel = Normalize(e.FullName);
                if (!seen.Add(rel)) throw Bad("The package has duplicate entries.");
                if (isDir) { if (e.Length != 0) throw Bad("A directory entry carries data."); continue; }
                files.Add((e, rel));
            }
            if (!files.Any(f => f.Rel.Equals(ExeName, StringComparison.OrdinalIgnoreCase)))
                throw Bad("The package does not contain " + ExeName + " at its root.");

            Directory.CreateDirectory(tmp);
            var root = Path.GetFullPath(tmp) + Path.DirectorySeparatorChar;
            long total = 0;
            foreach (var (entry, rel) in files)
            {
                var target = Path.GetFullPath(Path.Combine(tmp, rel));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw Bad("An entry escapes the staging folder.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // Count what is really written: declared sizes in a zip are attacker-controlled.
                using var src = entry.Open();
                using var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buf = new byte[81920];
                long entryBytes = 0;
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    entryBytes += n;
                    total += n;
                    if (entryBytes > limits.MaxEntryBytes || total > limits.MaxTotalBytes) throw Bad("The package expands beyond the allowed size.");
                    dst.Write(buf, 0, n);
                }
            }
            Directory.Move(tmp, stagingDir);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp);
            throw new UpdateException(UpdateFailure.Malformed, "The package could not be unpacked safely: " + ex.Message);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static UpdateException Bad(string why) => new(UpdateFailure.Malformed, why);

    private static string Normalize(string name)
    {
        var n = name.Replace('\\', '/');
        if (n.Length == 0 || n[0] == '/' || n.Contains(':')) throw Bad("An entry has an absolute or stream-style name.");
        var parts = n.TrimEnd('/').Split('/');
        foreach (var p in parts)
        {
            if (p.Length == 0 || p is "." or "..") throw Bad("An entry has a relative-path name.");
            if (p.EndsWith('.') || p.EndsWith(' ')) throw Bad("An entry name ends with a dot or space.");
            if (ReservedName().IsMatch(p)) throw Bad("An entry uses a reserved device name.");
            if (p.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || p.Any(char.IsControl)) throw Bad("An entry name has invalid characters.");
        }
        return string.Join('/', parts);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }
}
