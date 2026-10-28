using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

public enum AttachmentKind { Image, Pdf, File }

/// <summary>Local file waiting to upload with the next prompt (BookahHermes OutgoingAttachment).</summary>
public sealed class OutgoingAttachment
{
    public const int MaxBytes = 25 * 1024 * 1024;
    public const int MaxCount = 10;

    public OutgoingAttachment(string id, string name, string mimeType, byte[] bytes)
    {
        if (bytes.Length > MaxBytes)
            throw new ArgumentException($"Attachment exceeds {MaxBytes} bytes.", nameof(bytes));
        Id = id;
        Name = SanitizeFileName(name);
        MimeType = mimeType;
        Bytes = bytes;
    }

    public string Id { get; }
    public string Name { get; }
    public string MimeType { get; }
    public byte[] Bytes { get; }

    public AttachmentKind Kind =>
        MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? AttachmentKind.Image
        : MimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
          || Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? AttachmentKind.Pdf
        : AttachmentKind.File;

    public static string SanitizeFileName(string name)
    {
        var leaf = name.Replace('\\', '/');
        var i = leaf.LastIndexOf('/');
        if (i >= 0) leaf = leaf[(i + 1)..];
        leaf = leaf.Trim();
        if (leaf is "" or "." or "..") leaf = "file";
        foreach (var c in Path.GetInvalidFileNameChars())
            leaf = leaf.Replace(c, '_');
        return leaf;
    }
}

public sealed record ShownAttachment(string Key, string Name, AttachmentKind Kind, string? GatewayPath = null);

public sealed record AttachResult(bool Ok, string? Path = null, string? RefText = null, string? Error = null);

/// <summary>JSON-RPC attachment upload (BookahHermes ChatSession.upload). No multipart.</summary>
public sealed class AttachmentUploader(Func<JsonRpcClient> client)
{
    public async Task<AttachResult> UploadAsync(string sessionId, OutgoingAttachment file, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sessionId))
            return new AttachResult(false, Error: "No session to attach to.");
        try
        {
            return file.Kind switch
            {
                AttachmentKind.Image => await AttachImageAsync(sessionId, file, ct).ConfigureAwait(false),
                AttachmentKind.Pdf => await AttachPdfAsync(sessionId, file, ct).ConfigureAwait(false),
                _ => await AttachFileAsync(sessionId, file, ct).ConfigureAwait(false)
            };
        }
        catch (RpcException ex)
        {
            return new AttachResult(false, Error: ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AttachResult(false, Error: ex.Message);
        }
    }

    public async Task DetachBestEffortAsync(string sessionId, string path, CancellationToken ct = default)
    {
        try
        {
            await client().RequestAsync("image.detach", new JsonObject
            {
                ["session_id"] = sessionId,
                ["path"] = path
            }, timeout: TimeSpan.FromSeconds(10), ct: ct).ConfigureAwait(false);
        }
        catch { /* best-effort cleanup */ }
    }

    private async Task<AttachResult> AttachImageAsync(string sessionId, OutgoingAttachment file, CancellationToken ct)
    {
        var reply = await client().RequestAsync("image.attach_bytes", new JsonObject
        {
            ["session_id"] = sessionId,
            ["content_base64"] = Convert.ToBase64String(file.Bytes),
            ["filename"] = file.Name
        }, timeout: TimeSpan.FromSeconds(120), ct: ct).ConfigureAwait(false) as JsonObject;
        if (reply?["attached"]?.GetValue<bool>() == true)
            return new AttachResult(true, Path: reply["path"]?.GetValue<string>());
        return new AttachResult(false, Error: reply?["error"]?.GetValue<string>() ?? "Image attach failed.");
    }

    private async Task<AttachResult> AttachPdfAsync(string sessionId, OutgoingAttachment file, CancellationToken ct)
    {
        try
        {
            var reply = await client().RequestAsync("pdf.attach", new JsonObject
            {
                ["session_id"] = sessionId,
                ["content_base64"] = Convert.ToBase64String(file.Bytes),
                ["filename"] = file.Name
            }, timeout: TimeSpan.FromSeconds(120), ct: ct).ConfigureAwait(false) as JsonObject;
            if (reply?["attached"]?.GetValue<bool>() == true || reply?["pages"] is JsonArray)
                return new AttachResult(true, Path: reply["pages"] is JsonArray pages && pages.Count > 0
                    ? pages[0]?["path"]?.GetValue<string>()
                    : reply["path"]?.GetValue<string>());
        }
        catch (RpcException ex) when (ex.Code == 5028)
        {
            return await AttachFileAsync(sessionId, file, ct).ConfigureAwait(false);
        }
        return await AttachFileAsync(sessionId, file, ct).ConfigureAwait(false);
    }

    private async Task<AttachResult> AttachFileAsync(string sessionId, OutgoingAttachment file, CancellationToken ct)
    {
        var mime = string.IsNullOrWhiteSpace(file.MimeType) ? "application/octet-stream" : file.MimeType;
        var dataUrl = "data:" + mime + ";base64," + Convert.ToBase64String(file.Bytes);
        var reply = await client().RequestAsync("file.attach", new JsonObject
        {
            ["session_id"] = sessionId,
            ["name"] = file.Name,
            ["data_url"] = dataUrl
        }, timeout: TimeSpan.FromSeconds(120), ct: ct).ConfigureAwait(false) as JsonObject;
        if (reply?["attached"]?.GetValue<bool>() == true)
            return new AttachResult(true, RefText: reply["ref_text"]?.GetValue<string>());
        return new AttachResult(false, Error: reply?["error"]?.GetValue<string>() ?? "File attach failed.");
    }
}

/// <summary>Gateway path parsing (BookahHermes GatewayFile.fromLink). Rejects web/untrusted schemes.</summary>
public static partial class GatewayFile
{
    public static bool TryParse(string link, out string path, out int? line)
    {
        path = "";
        line = null;
        var rest = link.Trim();
        if (rest.Length == 0) return false;

        var schemeMatch = SchemeRegex().Match(rest);
        string? scheme = schemeMatch.Success ? schemeMatch.Groups[1].Value.ToLowerInvariant() : null;
        if (scheme is not null && scheme != "file") return false;

        if (scheme == "file")
        {
            rest = rest[5..];
            if (rest.StartsWith("//", StringComparison.Ordinal))
            {
                var after = rest[2..];
                var host = after.Split('/')[0];
                if (host.Length > 0 && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                    return false;
                rest = after[host.Length..];
            }
            var q = rest.IndexOf('?');
            if (q >= 0) rest = rest[..q];
            if (!rest.StartsWith('/')) return false;
        }
        else if (!(rest.StartsWith('/') && !rest.StartsWith("//", StringComparison.Ordinal)) && !rest.StartsWith("~/"))
            return false;

        var hash = scheme == "file" ? rest.IndexOf('#') : rest.LastIndexOf('#');
        if (hash >= 0)
        {
            var fragment = rest[(hash + 1)..];
            var fragMatch = LineFragmentRegex().Match(fragment);
            if (fragMatch.Success && int.TryParse(fragMatch.Groups[1].Value, out var ln) && ln > 0)
                line = ln;
            rest = rest[..hash];
        }

        path = PercentDecode(rest);
        if (WindowsDriveRegex().IsMatch(path)) path = path[1..];
        var suffix = LineSuffixRegex().Match(path);
        if (suffix.Success)
        {
            if (line is null && int.TryParse(suffix.Groups[1].Value, out var ln2) && ln2 > 0)
                line = ln2;
            path = path[..suffix.Index];
        }

        path = Normalize(path);
        if (path is "" or "/" or "~/") return false;
        return true;
    }

    public static string Normalize(string path)
    {
        string root;
        if (path.StartsWith("~/", StringComparison.Ordinal)) root = "~/";
        else if (path.StartsWith('/')) root = "/";
        else if (DriveRootRegex().IsMatch(path)) root = path[..3];
        else root = "";

        var segments = new List<string>();
        foreach (var segment in path[root.Length..].Split('/'))
        {
            switch (segment)
            {
                case "" or ".": break;
                case "..":
                    if (segments.Count > 0 && segments[^1] != "..") segments.RemoveAt(segments.Count - 1);
                    else if (root is "~/" or "") segments.Add(segment);
                    break;
                default: segments.Add(segment); break;
            }
        }
        return root + string.Join("/", segments);
    }

    public static string SafeDownloadFileName(string path)
    {
        var name = path.TrimEnd('/');
        var i = name.LastIndexOf('/');
        if (i >= 0) name = name[(i + 1)..];
        return OutgoingAttachment.SanitizeFileName(name);
    }

    private static string PercentDecode(string text)
    {
        if (!text.Contains('%')) return text;
        try { return Uri.UnescapeDataString(text.Replace("+", "%2B")); }
        catch { return text; }
    }

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9+.-]*):")]
    private static partial Regex SchemeRegex();
    [GeneratedRegex(@"^L(\d+)(?:-L?\d+)?(?:C\d+)?")]
    private static partial Regex LineFragmentRegex();
    [GeneratedRegex(@":(\d+)(?::\d+)?$")]
    private static partial Regex LineSuffixRegex();
    [GeneratedRegex(@"^/[A-Za-z]:/")]
    private static partial Regex WindowsDriveRegex();
    [GeneratedRegex(@"^[A-Za-z]:/")]
    private static partial Regex DriveRootRegex();
}

/// <summary>Authenticated file download (BookahHermes MediaApi). Requires explicit user gesture in UI.</summary>
public sealed class MediaApi(DashboardAuth auth)
{
    public const long MaxDownloadBytes = 64L * 1024 * 1024;

    public async Task<byte[]> DownloadAsync(string path, string? profile, CancellationToken ct = default)
    {
        string normalized;
        if (GatewayFile.TryParse(path, out normalized, out _)) { }
        else if (!path.Contains("://", StringComparison.Ordinal) &&
                 GatewayFile.TryParse(path.StartsWith('/') || path.StartsWith("~/") ? path : "/" + path, out normalized, out _)) { }
        else
            throw new InvalidOperationException("Refusing download of untrusted path.");
        // Prefer fs/download with profile; fall back to files/download.
        try
        {
            return await GetBytesAsync(BuildFsDownload(normalized, profile), ct).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (string.IsNullOrEmpty(profile))
        {
            return await GetBytesAsync("api/files/download?path=" + Uri.EscapeDataString(normalized), ct).ConfigureAwait(false);
        }
    }

    private static string BuildFsDownload(string path, string? profile)
    {
        var q = "path=" + Uri.EscapeDataString(path);
        if (!string.IsNullOrEmpty(profile)) q += "&profile=" + Uri.EscapeDataString(profile);
        return "api/fs/download?" + q;
    }

    private async Task<byte[]> GetBytesAsync(string relative, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(auth.BaseUri, relative));
        using var resp = await auth.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new AuthFailedException("session_expired", "session_expired");
        if (resp.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            throw new InvalidOperationException("File too large to download.");
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Download failed: {(int)resp.StatusCode}");
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxDownloadBytes)
                throw new InvalidOperationException("File exceeds download size limit.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }
}

/// <summary>Pending uploads scoped to a (profile, session). Clears on profile deselect.</summary>
public sealed class AttachmentQueue
{
    private readonly List<OutgoingAttachment> _items = new();
    private readonly object _gate = new();
    private ProfileKey? _owner;
    private string? _sessionId;

    public IReadOnlyList<OutgoingAttachment> Items
    {
        get { lock (_gate) return _items.ToList(); }
    }

    public void Bind(ProfileKey profile, string? sessionId)
    {
        lock (_gate)
        {
            if (_owner == profile && _sessionId == sessionId) return;
            _owner = profile;
            _sessionId = sessionId;
            _items.Clear();
        }
    }

    public void Clear()
    {
        lock (_gate) _items.Clear();
    }

    public void EnsureOwner(ProfileKey profile, NotificationGate gate)
    {
        lock (_gate)
        {
            if (_owner is null || _owner != profile || !gate.Allow(Surface.Conversation, profile))
            {
                _items.Clear();
                _owner = null;
                _sessionId = null;
            }
        }
    }

    public string? TryAdd(ProfileKey profile, NotificationGate gate, OutgoingAttachment file)
    {
        lock (_gate)
        {
            if (!gate.Allow(Surface.Conversation, profile))
                return "Profile not allowed to attach files.";
            if (_owner is not null && _owner != profile)
                return "Attachment queue belongs to another profile.";
            _owner = profile;
            if (_items.Count >= OutgoingAttachment.MaxCount)
                return $"At most {OutgoingAttachment.MaxCount} attachments.";
            if (_items.Any(i => i.Id == file.Id)) return null;
            _items.Add(file);
            return null;
        }
    }

    public void Remove(string id)
    {
        lock (_gate) _items.RemoveAll(i => i.Id == id);
    }
}
