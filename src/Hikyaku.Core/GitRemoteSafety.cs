using System.Text.RegularExpressions;

namespace Hikyaku.Core;

/// <summary>
/// Validates Git remote URLs for guided push. Strict URI/path syntax; rejects credentials,
/// control chars, query/fragment tokens, ext/helper protocols, UNC, and option-like tokens.
/// </summary>
public static class GitRemoteSafety
{
    public const string LimitsCopy =
        "Guided push allows https, http, ssh, git@host:path, and local file/path remotes only. " +
        "URLs with embedded credentials, username-only HTTP userinfo, query/fragment tokens, control characters, " +
        "ext/helper protocols, UNC, or option-like tokens are refused. " +
        "url.*.insteadOf / pushInsteadOf and alternate pushurl destinations are assessed and refused when unsafe.";

    private static readonly Regex ControlOrNull = new(@"[\u0000-\u001F\u007F]", RegexOptions.Compiled);
    private static readonly Regex ScpLike = new(
        @"^(?<user>[A-Za-z0-9._-]+)@(?<host>[A-Za-z0-9][A-Za-z0-9.-]*[A-Za-z0-9]|[A-Za-z0-9]):(?<path>[^:\0]+)$",
        RegexOptions.Compiled);
    private static readonly Regex OptionLike = new(@"^\s*-", RegexOptions.Compiled);
    private static readonly Regex WindowsDrive = new(@"^[A-Za-z]:[\\/]", RegexOptions.Compiled);

    public static bool TryValidateRemoteUrl(string? rawUrl, out string? error, out string displayUrl)
    {
        displayUrl = "";
        error = null;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            error = "Remote URL is empty.";
            return false;
        }

        var url = rawUrl.Trim();
        if (ControlOrNull.IsMatch(url) || url.Contains('\0'))
        {
            error = "Remote URL contains control characters.";
            return false;
        }
        if (OptionLike.IsMatch(url) || url.Contains(" --", StringComparison.Ordinal))
        {
            error = "Remote URL looks like a git option injection.";
            return false;
        }
        if (url.Contains("ext::", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
        {
            error = "ext protocol remotes are not allowed.";
            return false;
        }
        if (url.StartsWith("helper:", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("helper::", StringComparison.OrdinalIgnoreCase))
        {
            error = "credential helper protocol remotes are not allowed.";
            return false;
        }
        // git remote-helper style (transport::address) — not scp-like (user@host:path)
        if (Regex.IsMatch(url, @"^[A-Za-z0-9][A-Za-z0-9+.-]*::"))
        {
            error = "Git remote-helper URLs are not allowed.";
            return false;
        }
        // user:token@host without scheme
        if (Regex.IsMatch(url, @"^[^\s/]+:[^\s/]+@[^\s/]+"))
        {
            error = "Remote URLs must not embed credentials or secrets.";
            return false;
        }

        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("git://", StringComparison.OrdinalIgnoreCase))
        {
            return TryValidateAbsoluteUri(url, out error, out displayUrl);
        }

        if (ScpLike.IsMatch(url))
        {
            // Preserve ssh git username in display; no password segment in scp form we accept.
            displayUrl = url;
            return true;
        }

        if (TryValidateLocalPath(url, out error, out displayUrl))
            return true;

        error ??= "Unsupported remote URL scheme. " + LimitsCopy;
        return false;
    }

    private static bool TryValidateAbsoluteUri(string url, out string? error, out string displayUrl)
    {
        displayUrl = "";
        error = null;
        // Reject path traversal tokens in the raw string before Uri normalization hides them.
        if (url.Contains("..", StringComparison.Ordinal))
        {
            error = "Remote URL path rejected.";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            error = "Remote URL failed strict URI parsing.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query))
        {
            error = "Remote URL must not include a query string.";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            error = "Remote URL must not include a fragment.";
            return false;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        switch (scheme)
        {
            case "http":
            case "https":
                if (string.IsNullOrEmpty(uri.Host) || uri.Host.StartsWith('-'))
                {
                    error = "Remote URL host is invalid.";
                    return false;
                }
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    // Reject any HTTP(S) userinfo (username-only or user:pass). SSH git@ is separate scheme.
                    error = "Remote URLs must not embed credentials or username userinfo.";
                    return false;
                }
                if (uri.AbsolutePath.Contains("..", StringComparison.Ordinal))
                {
                    error = "Remote URL path rejected.";
                    return false;
                }
                displayUrl = RedactForDisplay(uri.GetLeftPart(UriPartial.Path).TrimEnd('/'));
                if (string.IsNullOrEmpty(displayUrl))
                    displayUrl = scheme + "://" + uri.Host + uri.AbsolutePath;
                return true;

            case "ssh":
                if (string.IsNullOrEmpty(uri.Host) || uri.Host.StartsWith('-'))
                {
                    error = "SSH remote host is invalid.";
                    return false;
                }
                if (!string.IsNullOrEmpty(uri.UserInfo) &&
                    !string.Equals(uri.UserInfo, "git", StringComparison.Ordinal))
                {
                    // Allow only the conventional git username; reject tokens/passwords in userinfo.
                    if (uri.UserInfo.Contains(':', StringComparison.Ordinal))
                    {
                        error = "Remote URLs must not embed credentials or secrets.";
                        return false;
                    }
                    error = "SSH remotes may use the git username only.";
                    return false;
                }
                // Preserve conventional git@ username in display; never show other userinfo.
                displayUrl = string.Equals(uri.UserInfo, "git", StringComparison.Ordinal)
                    ? uri.GetLeftPart(UriPartial.Path).TrimEnd('/')
                    : RedactForDisplay(url);
                return true;

            case "git":
                if (string.IsNullOrEmpty(uri.Host) || uri.Host.StartsWith('-') || !string.IsNullOrEmpty(uri.UserInfo))
                {
                    error = "git:// remotes with userinfo or invalid host are not allowed.";
                    return false;
                }
                displayUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
                return true;

            case "file":
                // Only local file URIs without a host (file:///C:/… or file:///tmp/…).
                if (!string.IsNullOrEmpty(uri.Host) &&
                    !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    error = "file:// remotes with a network host are not allowed.";
                    return false;
                }
                // Reject file://localhost explicitly (ambiguous / non-canonical for publish).
                if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    error = "file://localhost remotes are not allowed; use a local path.";
                    return false;
                }
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    error = "file:// remotes must not include userinfo.";
                    return false;
                }
                displayUrl = uri.LocalPath;
                return TryValidateLocalPath(uri.LocalPath, out error, out displayUrl);

            default:
                error = "Unsupported remote URL scheme. " + LimitsCopy;
                return false;
        }
    }

    private static bool TryValidateLocalPath(string url, out string? error, out string displayUrl)
    {
        displayUrl = "";
        error = null;
        if (url.StartsWith(@"\\", StringComparison.Ordinal) || url.StartsWith("//", StringComparison.Ordinal))
        {
            error = "UNC remotes are not allowed.";
            return false;
        }

        if (url.Contains("..", StringComparison.Ordinal))
        {
            error = "Local path remote rejected.";
            return false;
        }

        // Windows drive path
        if (WindowsDrive.IsMatch(url))
        {
            if (url.Contains("://", StringComparison.Ordinal) || url.Contains('@'))
            {
                error = "Local path remote rejected.";
                return false;
            }
            displayUrl = url;
            return true;
        }

        // POSIX absolute
        if (url.StartsWith("/", StringComparison.Ordinal) && !url.StartsWith("//", StringComparison.Ordinal))
        {
            if (url.Contains('@') || Regex.IsMatch(url, @"::"))
            {
                error = "Local path remote rejected.";
                return false;
            }
            displayUrl = url;
            return true;
        }

        // Relative ./ paths
        if (url.StartsWith("./", StringComparison.Ordinal) || url.StartsWith(".\\", StringComparison.Ordinal))
        {
            if (url.Contains('@') || url.Contains(':'))
            {
                error = "Local path remote rejected.";
                return false;
            }
            displayUrl = url;
            return true;
        }

        // Bare something.git — no scheme, no at-sign, no colon (blocks helper-ish tokens)
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("://", StringComparison.Ordinal) &&
            !url.Contains('@') &&
            !url.Contains(':'))
        {
            displayUrl = url;
            return true;
        }

        error = "Unsupported remote URL scheme. " + LimitsCopy;
        return false;
    }

    public static bool IsSafeRemoteName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.StartsWith("-", StringComparison.Ordinal)) return false;
        if (name.Contains('\0') || ControlOrNull.IsMatch(name)) return false;
        return Regex.IsMatch(name, @"^[A-Za-z0-9._-]+$");
    }

    public static bool IsSafeBranchName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("-", StringComparison.Ordinal)) return false;
        if (name.Contains('\0') || ControlOrNull.IsMatch(name)) return false;
        if (name.Contains("..", StringComparison.Ordinal)) return false;
        return Regex.IsMatch(name, @"^[A-Za-z0-9._/-]+$");
    }

    public static string RedactForDisplay(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        // Mask user:pass@ and username-only userinfo on URI forms. Preserve ssh git@host display.
        var redacted = Regex.Replace(url, @"(://)([^:/@\s]+):([^/@\s]+)@", "$1***:***@");
        redacted = Regex.Replace(redacted, @"(://)([^:/@\s]+)@", "$1***@");
        // Do not leave username-only visible for http(s)
        if (redacted.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            redacted.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            redacted = Regex.Replace(redacted, @"(https?://)\*\*\*@", "$1");
        }
        return redacted;
    }
}
