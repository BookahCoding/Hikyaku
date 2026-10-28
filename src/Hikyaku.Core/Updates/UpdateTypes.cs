using System.Text.RegularExpressions;

namespace Hikyaku.Core;

public enum UpdateFailure
{
    Offline,
    RateLimited,
    AuthRequired,
    NoRelease,
    Malformed,
    Failed,
    /// <summary>No trusted publisher key, or the signature did not verify. Never executed.</summary>
    NotTrusted,
    /// <summary>The running app has not exited, so files are still in use.</summary>
    Busy
}

public sealed class UpdateException(UpdateFailure kind, string message) : Exception(message)
{
    public UpdateFailure Kind { get; } = kind;
}

/// <summary>Strict <c>MAJOR.MINOR.PATCH</c>. Pre-release tags are deliberately not parsed, so never offered.</summary>
public readonly partial record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    [GeneratedRegex(@"^v(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})$")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^([0-9]{1,6})\.([0-9]{1,6})\.([0-9]{1,6})(\.[0-9]{1,6})?$")]
    private static partial Regex CurrentPattern();

    public static bool TryParseTag(string? tag, out ReleaseVersion version)
    {
        version = default;
        if (tag is null) return false;
        var m = TagPattern().Match(tag);
        if (!m.Success) return false;
        version = new ReleaseVersion(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        return true;
    }

    /// <summary>Assembly informational versions such as <c>0.1.0+abc123</c> or <c>1.2.3.0</c>.</summary>
    public static bool TryParseCurrent(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var core = text.Split('+', '-')[0];
        var m = CurrentPattern().Match(core);
        if (!m.Success) return false;
        version = new ReleaseVersion(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        return true;
    }

    public string Tag => "v" + this;
    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    public int CompareTo(ReleaseVersion o) =>
        Major != o.Major ? Major.CompareTo(o.Major) : Minor != o.Minor ? Minor.CompareTo(o.Minor) : Patch.CompareTo(o.Patch);

    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;
}

public sealed record UpdateRelease(
    ReleaseVersion Version, string Tag, string Notes, Uri ZipUrl, Uri SigUrl, Uri PageUrl, long ZipSize);

public abstract record UpdateCheckResult;
public sealed record UpdateUpToDate(ReleaseVersion? Latest) : UpdateCheckResult;
public sealed record UpdateAvailable(UpdateRelease Release) : UpdateCheckResult;
public sealed record UpdateCheckFailed(UpdateFailure Kind, string Message) : UpdateCheckResult;

/// <summary>Only these hosts, over https, may serve a release (GitHub's API, web and release-asset CDN).</summary>
public static class GitHubReleasePolicy
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"
    };

    public static bool IsAllowedDownload(Uri u) =>
        u.IsAbsoluteUri && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && string.IsNullOrEmpty(u.UserInfo) && Hosts.Contains(u.Host);

    /// <summary>The release page the user may open in a browser.</summary>
    public static bool IsReleasePage(Uri u, string repo, string tag) =>
        u.IsAbsoluteUri && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort
        && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && u.AbsolutePath == $"/{repo}/releases/tag/{tag}";
}
