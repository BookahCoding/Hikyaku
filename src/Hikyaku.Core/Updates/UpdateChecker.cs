using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Hikyaku.Core;

/// <summary>
/// Read-only check against <c>GET /repos/{repo}/releases/latest</c>. Anonymous: public releases need no token;
/// a private repo answers 401/403/404 and is reported as such (the private-repo token path is a later stage).
/// Only strict <c>vMAJOR.MINOR.PATCH</c> releases with an exact-name, this-repo, this-tag zip and signature asset
/// are offered, and never a downgrade.
/// </summary>
public sealed class UpdateChecker(HttpMessageHandler handler, string repo, ReleaseVersion current)
{
    public const string ZipName = "Hikyaku-win-x64.zip";
    public const string SigName = ZipName + ".sig";
    public const int MaxNotesChars = 4000;
    public const int MaxResponseBytes = 512 * 1024;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Hikyaku-Updater", current.ToString()));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return new UpdateCheckFailed(UpdateFailure.Offline, "Could not reach GitHub. Check your connection.");
        }

        using (resp)
        {
            switch (resp.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    return new UpdateCheckFailed(UpdateFailure.NoRelease, "No published release was found (or the repository is private).");
                case HttpStatusCode.Unauthorized:
                    return new UpdateCheckFailed(UpdateFailure.AuthRequired, "GitHub requires sign-in for this repository.");
                case HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests:
                    return resp.Headers.TryGetValues("x-ratelimit-remaining", out var rem) && rem.FirstOrDefault() == "0"
                        ? new UpdateCheckFailed(UpdateFailure.RateLimited, "GitHub's anonymous rate limit is used up. Try again later.")
                        : new UpdateCheckFailed(UpdateFailure.AuthRequired, "GitHub refused the request; the repository may be private.");
            }
            if (!resp.IsSuccessStatusCode)
                return new UpdateCheckFailed(UpdateFailure.Failed, $"GitHub answered HTTP {(int)resp.StatusCode}.");

            var text = await BoundedText.ReadAsync(resp.Content, MaxResponseBytes, ct).ConfigureAwait(false);
            if (text is null) return Bad("The release answer was too large.");
            try { return Interpret(text); }
            catch (JsonException) { return Bad("The release answer was not valid JSON."); }
        }
    }

    private static UpdateCheckFailed Bad(string why) => new(UpdateFailure.Malformed, why);

    private UpdateCheckResult Interpret(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return Bad("The release answer was not an object.");
        if (Flag(r, "draft") || Flag(r, "prerelease"))
            return new UpdateCheckFailed(UpdateFailure.NoRelease, "The latest release is not a stable release.");

        var tag = Str(r, "tag_name");
        if (!ReleaseVersion.TryParseTag(tag, out var version))
            return Bad("The release tag is not a plain vMAJOR.MINOR.PATCH version.");
        if (version <= current) return new UpdateUpToDate(version);

        var assets = r.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : [];
        var zip = Asset(assets, ZipName, tag!, out var zipSize);
        if (zip is null) return Bad("The release has no " + ZipName + " asset at the expected address.");
        var sig = Asset(assets, SigName, tag!, out _);
        if (sig is null) return Bad("The release has no signature asset, so it cannot be installed safely.");
        if (zipSize is <= 0 or > UpdateInstaller.MaxZipBytes) return Bad("The release package has an unacceptable size.");

        var page = new Uri($"https://github.com/{repo}/releases/tag/{tag}");
        return new UpdateAvailable(new UpdateRelease(version, tag!, Sanitize(Str(r, "body")), zip, sig, page, zipSize));
    }

    private Uri? Asset(List<JsonElement> assets, string name, string tag, out long size)
    {
        size = 0;
        foreach (var asset in assets)
        {
            if (Str(asset, "name") != name) continue;
            if (asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var l)) size = l;
            var url = Str(asset, "browser_download_url");
            // Exact address only: never follow a URL the JSON merely claims.
            return Uri.TryCreate(url, UriKind.Absolute, out var u)
                && u.AbsoluteUri == $"https://github.com/{repo}/releases/download/{tag}/{name}"
                ? u : null;
        }
        return null;
    }

    private static string Sanitize(string? notes)
    {
        if (string.IsNullOrEmpty(notes)) return "";
        var sb = new StringBuilder(Math.Min(notes.Length, MaxNotesChars + 1));
        foreach (var c in notes.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (sb.Length >= MaxNotesChars) { sb.Append('…'); break; }
            if (c == '\n' || c == '\t' || !char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private static string? Str(JsonElement e, string n) =>
        e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Flag(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.True;
}

/// <summary>
/// Bounded, allow-listed download. Redirects are followed by hand so every hop is re-checked (https, host list,
/// default port); no credentials are ever sent; partial files are removed on any failure.
/// </summary>
public sealed class UpdateDownloader(HttpMessageHandler handler)
{
    public const int MaxRedirects = 5;

    public async Task DownloadAsync(Uri url, string destPath, long maxBytes, IProgress<long>? progress, CancellationToken ct)
    {
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var part = destPath + ".part";
        try
        {
            var current = url;
            for (var hop = 0; ; hop++)
            {
                if (hop > MaxRedirects) throw new UpdateException(UpdateFailure.Failed, "Too many redirects.");
                if (!GitHubReleasePolicy.IsAllowedDownload(current))
                    throw new UpdateException(UpdateFailure.Failed, "Refusing to download from outside GitHub over https.");

                using var req = new HttpRequestMessage(HttpMethod.Get, current);
                req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Hikyaku-Updater", "1"));
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if ((int)resp.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (resp.Headers.Location is not { } next)
                        throw new UpdateException(UpdateFailure.Failed, "Redirect without a location.");
                    current = next.IsAbsoluteUri ? next : new Uri(current, next);
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                    throw new UpdateException(UpdateFailure.Failed, $"Download failed: HTTP {(int)resp.StatusCode}.");
                if (resp.Content.Headers.ContentLength is { } len && len > maxBytes)
                    throw new UpdateException(UpdateFailure.Malformed, "The download is larger than allowed.");

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);
                await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buf = new byte[81920];
                    long total = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        total += n;
                        if (total > maxBytes) throw new UpdateException(UpdateFailure.Malformed, "The download is larger than allowed.");
                        await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        progress?.Report(total);
                    }
                }
                File.Move(part, destPath, overwrite: true);
                return;
            }
        }
        catch
        {
            try { File.Delete(part); } catch { /* best effort */ }
            throw;
        }
    }
}
