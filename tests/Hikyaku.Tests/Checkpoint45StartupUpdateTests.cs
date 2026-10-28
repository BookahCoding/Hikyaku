using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 45 — the start-up update check: only a newer release is reported, and a failure says nothing at all.
/// The version scheme starts at 0.5.0, so a 0.x version must parse and compare like any other.
/// </summary>
public class Checkpoint45StartupUpdateTests
{
    private static readonly UpdateRelease Release = new(new ReleaseVersion(0, 5, 1), "v0.5.1", "notes",
        new Uri("https://github.com/o/r/releases/download/v0.5.1/Hikyaku-win-x64.zip"),
        new Uri("https://github.com/o/r/releases/download/v0.5.1/Hikyaku-win-x64.zip.sig"),
        new Uri("https://github.com/o/r/releases/tag/v0.5.1"), 1);

    [Fact]
    public async Task Only_a_newer_release_is_reported_and_any_failure_stays_quiet()
    {
        Assert.Same(Release, await StartupUpdateCheck.FindNewerAsync(_ => Task.FromResult<UpdateCheckResult>(new UpdateAvailable(Release)), default));
        Assert.Null(await StartupUpdateCheck.FindNewerAsync(_ => Task.FromResult<UpdateCheckResult>(new UpdateUpToDate(null)), default));
        Assert.Null(await StartupUpdateCheck.FindNewerAsync(_ => Task.FromResult<UpdateCheckResult>(new UpdateCheckFailed(UpdateFailure.Failed, "404")), default));
        Assert.Null(await StartupUpdateCheck.FindNewerAsync(_ => throw new HttpRequestException("offline"), default));
        Assert.Null(await StartupUpdateCheck.FindNewerAsync(ct => Task.FromCanceled<UpdateCheckResult>(new CancellationToken(true)), default));
    }

    [Fact]
    public void A_zero_major_version_parses_and_orders()
    {
        Assert.True(ReleaseVersion.TryParseCurrent("0.5.0+07a8d41", out var current));
        Assert.Equal(new ReleaseVersion(0, 5, 0), current);
        Assert.True(ReleaseVersion.TryParseTag("v0.5.1", out var next));
        Assert.True(next > current);
        Assert.True(new ReleaseVersion(1, 0, 0) > next);
    }
}
