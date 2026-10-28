using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

public sealed record GitHubPrRequest(
    string Owner,
    string Repo,
    string Title,
    string Body,
    string HeadBranch,
    string BaseBranch);

public sealed record GitHubPrResult(
    bool Ok,
    string? Error = null,
    bool SetupRequired = false,
    bool Duplicate = false,
    bool Cancelled = false,
    int? Number = null,
    string? HtmlUrl = null,
    string? ApiUrl = null,
    string? HeadRef = null,
    string? BaseRef = null,
    string? Detail = null);

public sealed record GitHubPrIdentity(string Owner, string Repo, string HeadBranch, string BaseBranch);

/// <summary>Source-backed GitHub pull-request contract. Opening a compare URL is not a created PR.</summary>
public interface IGitHubPullRequestApi
{
    Task<GitHubPrResult> CreateAsync(GitHubPrRequest request, bool confirmed, CancellationToken ct = default);
    Task<GitHubPrResult> GetAsync(string owner, string repo, int number, CancellationToken ct = default);
    Task<GitHubPrResult> FindOpenByHeadAsync(string owner, string repo, string headBranch, CancellationToken ct = default);
}

public static class GitHubPrIdentityParser
{
    private static readonly Regex GithubHttps = new(
        @"^https?://(?:www\.)?github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+?)(?:\.git)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GithubSsh = new(
        @"^(?:git@github\.com:|ssh://git@github\.com/)(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+?)(?:\.git)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryParseRemote(string? remoteUrlDisplay, out string owner, out string repo, out string? error)
    {
        owner = "";
        repo = "";
        error = null;
        if (string.IsNullOrWhiteSpace(remoteUrlDisplay))
        {
            error = "Remote URL missing.";
            return false;
        }
        var url = remoteUrlDisplay.Trim().TrimEnd('/');
        var m = GithubHttps.Match(url);
        if (!m.Success) m = GithubSsh.Match(url);
        if (!m.Success)
        {
            error = "Pull requests require a github.com remote. Local/file remotes cannot create GitHub PRs.";
            return false;
        }
        owner = m.Groups["owner"].Value;
        repo = m.Groups["repo"].Value;
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repo = repo[..^4];
        return true;
    }

    public const string CompareUrlIsNotPrCopy =
        "Opening a compare URL is not the same as creating a pull request.";
}

/// <summary>Truthful disabled state when no user-owned GitHub auth is configured. No embedded PAT.</summary>
public sealed class SetupRequiredGitHubPullRequestApi : IGitHubPullRequestApi
{
    public const string SetupCopy =
        "Create PR requires user-owned GitHub authentication that is not configured in this build. " +
        "Hikyaku does not embed or share a PAT. Fixture/loopback transport is tested; live PR create stays disabled until auth setup exists. " +
        GitHubPrIdentityParser.CompareUrlIsNotPrCopy;

    public Task<GitHubPrResult> CreateAsync(GitHubPrRequest request, bool confirmed, CancellationToken ct = default)
        => Task.FromResult(new GitHubPrResult(false, SetupCopy, SetupRequired: true));

    public Task<GitHubPrResult> GetAsync(string owner, string repo, int number, CancellationToken ct = default)
        => Task.FromResult(new GitHubPrResult(false, SetupCopy, SetupRequired: true));

    public Task<GitHubPrResult> FindOpenByHeadAsync(string owner, string repo, string headBranch, CancellationToken ct = default)
        => Task.FromResult(new GitHubPrResult(false, SetupCopy, SetupRequired: true));
}

/// <summary>In-memory / loopback fixture API for tests — not live GitHub.</summary>
public sealed class FixtureGitHubPullRequestApi : IGitHubPullRequestApi
{
    private readonly Dictionary<string, PrRec> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private int _next = 1;
    public bool IsSynthetic => true;
    public Uri? BaseAddress { get; init; }

    public Task<GitHubPrResult> CreateAsync(GitHubPrRequest request, bool confirmed, CancellationToken ct = default)
    {
        if (!confirmed)
            return Task.FromResult(new GitHubPrResult(false, "Create PR requires explicit confirmation.", Cancelled: true));
        if (!IsSafe(request.Owner) || !IsSafe(request.Repo) || !GitRemoteSafety.IsSafeBranchName(request.HeadBranch)
            || !GitRemoteSafety.IsSafeBranchName(request.BaseBranch))
            return Task.FromResult(new GitHubPrResult(false, "PR identity rejected."));
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length < 3)
            return Task.FromResult(new GitHubPrResult(false, "Enter a meaningful PR title."));

        var key = Key(request.Owner, request.Repo, request.HeadBranch);
        if (_byKey.TryGetValue(key, out var existing) && existing.State == "open")
        {
            return Task.FromResult(new GitHubPrResult(false,
                $"An open pull request already exists (#{existing.Number}).",
                Duplicate: true,
                Number: existing.Number,
                HtmlUrl: existing.HtmlUrl,
                HeadRef: existing.Head,
                BaseRef: existing.Base));
        }

        var n = _next++;
        var html = (BaseAddress?.ToString().TrimEnd('/') ?? "https://fixture.test") +
                   $"/{request.Owner}/{request.Repo}/pull/{n}";
        var rec = new PrRec(n, request.Owner, request.Repo, request.Title.Trim(), request.Body ?? "",
            request.HeadBranch, request.BaseBranch, "open", html);
        _byKey[key] = rec;
        return Task.FromResult(new GitHubPrResult(true, Number: n, HtmlUrl: html, ApiUrl: html,
            HeadRef: request.HeadBranch, BaseRef: request.BaseBranch,
            Detail: "Fixture PR created (synthetic — not live GitHub)."));
    }

    public Task<GitHubPrResult> GetAsync(string owner, string repo, int number, CancellationToken ct = default)
    {
        var hit = _byKey.Values.FirstOrDefault(p =>
            p.Number == number &&
            string.Equals(p.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Repo, repo, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
            return Task.FromResult(new GitHubPrResult(false, "PR not found in fixture."));
        return Task.FromResult(new GitHubPrResult(true, Number: hit.Number, HtmlUrl: hit.HtmlUrl,
            HeadRef: hit.Head, BaseRef: hit.Base, Detail: hit.Title));
    }

    public Task<GitHubPrResult> FindOpenByHeadAsync(string owner, string repo, string headBranch, CancellationToken ct = default)
    {
        var key = Key(owner, repo, headBranch);
        if (_byKey.TryGetValue(key, out var existing) && existing.State == "open")
            return Task.FromResult(new GitHubPrResult(true, Number: existing.Number, HtmlUrl: existing.HtmlUrl,
                Duplicate: true, HeadRef: existing.Head, BaseRef: existing.Base));
        return Task.FromResult(new GitHubPrResult(false, "No open PR for that head."));
    }

    private static string Key(string owner, string repo, string head) =>
        owner + "/" + repo + ":" + head;

    private static bool IsSafe(string s) =>
        !string.IsNullOrWhiteSpace(s) && Regex.IsMatch(s, @"^[A-Za-z0-9_.-]+$");

    private sealed record PrRec(
        int Number, string Owner, string Repo, string Title, string Body,
        string Head, string Base, string State, string HtmlUrl);
}

/// <summary>
/// HTTP GitHub API client. Requires an explicit token provider; never logs the token.
/// When token is null/empty, behaves as setup-required.
/// </summary>
public sealed class HttpGitHubPullRequestApi(HttpClient http, Func<string?> tokenProvider) : IGitHubPullRequestApi
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<GitHubPrResult> CreateAsync(GitHubPrRequest request, bool confirmed, CancellationToken ct = default)
    {
        if (!confirmed)
            return new GitHubPrResult(false, "Create PR requires explicit confirmation.", Cancelled: true);
        var token = tokenProvider();
        if (string.IsNullOrWhiteSpace(token))
            return new GitHubPrResult(false, SetupRequiredGitHubPullRequestApi.SetupCopy, SetupRequired: true);

        var payload = JsonSerializer.Serialize(new
        {
            title = request.Title.Trim(),
            body = request.Body ?? "",
            head = request.HeadBranch,
            @base = request.BaseBranch
        });

        using var msg = new HttpRequestMessage(HttpMethod.Post,
            $"repos/{request.Owner}/{request.Repo}/pulls");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        msg.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new GitHubPrResult(false, "PR create transport failed: " + ex.Message);
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if ((int)resp.StatusCode == 422 && body.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            var existing = await FindOpenByHeadAsync(request.Owner, request.Repo, request.HeadBranch, ct)
                .ConfigureAwait(false);
            return new GitHubPrResult(false, "An open pull request already exists for this head.",
                Duplicate: true, Number: existing.Number, HtmlUrl: existing.HtmlUrl,
                HeadRef: request.HeadBranch, BaseRef: request.BaseBranch);
        }
        if (!resp.IsSuccessStatusCode)
            return new GitHubPrResult(false, $"GitHub API {(int)resp.StatusCode}: {TrimErr(body)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var number = root.TryGetProperty("number", out var n) ? n.GetInt32() : (int?)null;
        var html = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
        var api = root.TryGetProperty("url", out var u) ? u.GetString() : null;
        var head = root.TryGetProperty("head", out var hd) && hd.TryGetProperty("ref", out var hr) ? hr.GetString() : request.HeadBranch;
        var bref = root.TryGetProperty("base", out var bs) && bs.TryGetProperty("ref", out var br) ? br.GetString() : request.BaseBranch;

        // Exact-target readback
        if (number is int num)
        {
            var readback = await GetAsync(request.Owner, request.Repo, num, ct).ConfigureAwait(false);
            if (!readback.Ok)
                return new GitHubPrResult(false,
                    "PR create response received but exact-target readback failed: " + readback.Error,
                    Number: number, HtmlUrl: html);
            if (!string.Equals(readback.HeadRef, request.HeadBranch, StringComparison.Ordinal) ||
                !string.Equals(readback.BaseRef, request.BaseBranch, StringComparison.Ordinal))
                return new GitHubPrResult(false, "PR readback destination did not match requested head/base.",
                    Number: number, HtmlUrl: html, HeadRef: readback.HeadRef, BaseRef: readback.BaseRef);
        }

        return new GitHubPrResult(true, Number: number, HtmlUrl: html, ApiUrl: api, HeadRef: head, BaseRef: bref,
            Detail: "Pull request created and read back.");
    }

    public async Task<GitHubPrResult> GetAsync(string owner, string repo, int number, CancellationToken ct = default)
    {
        var token = tokenProvider();
        if (string.IsNullOrWhiteSpace(token))
            return new GitHubPrResult(false, SetupRequiredGitHubPullRequestApi.SetupCopy, SetupRequired: true);

        using var msg = new HttpRequestMessage(HttpMethod.Get, $"repos/{owner}/{repo}/pulls/{number}");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        try
        {
            var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new GitHubPrResult(false, $"GitHub API {(int)resp.StatusCode}: {TrimErr(body)}");
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return new GitHubPrResult(true,
                Number: root.GetProperty("number").GetInt32(),
                HtmlUrl: root.TryGetProperty("html_url", out var h) ? h.GetString() : null,
                HeadRef: root.GetProperty("head").GetProperty("ref").GetString(),
                BaseRef: root.GetProperty("base").GetProperty("ref").GetString());
        }
        catch (Exception ex)
        {
            return new GitHubPrResult(false, ex.Message);
        }
    }

    public async Task<GitHubPrResult> FindOpenByHeadAsync(string owner, string repo, string headBranch, CancellationToken ct = default)
    {
        var token = tokenProvider();
        if (string.IsNullOrWhiteSpace(token))
            return new GitHubPrResult(false, SetupRequiredGitHubPullRequestApi.SetupCopy, SetupRequired: true);

        using var msg = new HttpRequestMessage(HttpMethod.Get,
            $"repos/{owner}/{repo}/pulls?state=open&head={Uri.EscapeDataString(owner + ":" + headBranch)}");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        try
        {
            var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new GitHubPrResult(false, $"GitHub API {(int)resp.StatusCode}: {TrimErr(body)}");
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.GetArrayLength() == 0)
                return new GitHubPrResult(false, "No open PR for that head.");
            var first = doc.RootElement[0];
            return new GitHubPrResult(true,
                Number: first.GetProperty("number").GetInt32(),
                HtmlUrl: first.TryGetProperty("html_url", out var h) ? h.GetString() : null,
                Duplicate: true,
                HeadRef: first.GetProperty("head").GetProperty("ref").GetString(),
                BaseRef: first.GetProperty("base").GetProperty("ref").GetString());
        }
        catch (Exception ex)
        {
            return new GitHubPrResult(false, ex.Message);
        }
    }

    private static string TrimErr(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(empty)";
        // Never echo Authorization headers; body may include message only.
        var t = body.Length > 240 ? body[..240] + "…" : body;
        return Regex.Replace(t, @"(?i)(token|bearer|authorization)[^\s,}]{0,40}", "[redacted]");
    }
}

/// <summary>Guided PR orchestration: requires verified push first; separate confirm; setup-required when no auth.</summary>
public sealed class GuidedPullRequestOps(IGitHubPullRequestApi api)
{
    public IGitHubPullRequestApi Api => api;

    public async Task<GitHubPrResult> CreateAfterVerifiedPushAsync(
        GitPushResult pushResult,
        string remoteUrlDisplay,
        string headBranch,
        string baseBranch,
        string title,
        string body,
        bool confirmed,
        CancellationToken ct = default)
    {
        if (!pushResult.Ok || !pushResult.Published || string.IsNullOrWhiteSpace(pushResult.RemoteShaVerified))
            return new GitHubPrResult(false, "Create PR requires a verified successful push first (exact remote SHA).");
        if (pushResult.OutcomeUncertain)
            return new GitHubPrResult(false, "Cannot create PR while push outcome is uncertain.");
        if (!confirmed)
            return new GitHubPrResult(false, "Create PR requires explicit confirmation.", Cancelled: true);
        if (!GitHubPrIdentityParser.TryParseRemote(remoteUrlDisplay, out var owner, out var repo, out var parseErr))
            return new GitHubPrResult(false, parseErr);

        var req = new GitHubPrRequest(owner, repo, title, body, headBranch, baseBranch);
        return await api.CreateAsync(req, confirmed: true, ct).ConfigureAwait(false);
    }
}
