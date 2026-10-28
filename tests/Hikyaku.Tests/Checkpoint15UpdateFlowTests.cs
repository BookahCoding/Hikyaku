using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 15c — the update screen's state machine (no network, no files).</summary>
public class Checkpoint15UpdateFlowTests
{
    private static readonly ReleaseVersion V2 = new(0, 2, 0);
    private static readonly UpdateRelease Rel = new(V2, "v0.2.0", "n",
        new Uri("https://github.com/o/r/releases/download/v0.2.0/Hikyaku-win-x64.zip"),
        new Uri("https://github.com/o/r/releases/download/v0.2.0/Hikyaku-win-x64.zip.sig"),
        new Uri("https://github.com/o/r/releases/tag/v0.2.0"), 10);

    private static UpdateFlow Flow(UpdateCheckResult result, bool trusted, Func<Task<StagedUpdate>>? prepare = null,
        TaskCompletionSource<UpdateCheckResult>? gate = null)
        => new(ct => gate is null ? Task.FromResult(result) : gate.Task,
               (_, _, _) => prepare is null ? Task.FromResult(new StagedUpdate(V2, "x")) : prepare(), trusted);

    [Fact]
    public async Task Nothing_happens_until_the_user_checks_and_download_requires_a_separate_click()
    {
        var prepares = 0;
        var f = Flow(new UpdateAvailable(Rel), trusted: true, prepare: () => { prepares++; return Task.FromResult(new StagedUpdate(V2, "x")); });
        Assert.Equal(UpdatePhase.Idle, f.Phase);
        await f.PrepareAsync(null, default);            // not available yet: ignored
        Assert.Equal(0, prepares);

        await f.CheckAsync(default);
        Assert.Equal(UpdatePhase.Available, f.Phase);
        Assert.Equal(0, prepares);                      // check never downloads
        Assert.True(f.CanDownload);
        Assert.False(f.CanInstall);

        await f.PrepareAsync(null, default);
        Assert.Equal(UpdatePhase.Ready, f.Phase);
        Assert.True(f.CanInstall);
        Assert.Equal(1, prepares);
    }

    [Fact]
    public async Task Without_a_publisher_key_download_is_disabled_and_the_reason_is_shown()
    {
        var prepares = 0;
        var f = Flow(new UpdateAvailable(Rel), trusted: false, prepare: () => { prepares++; return Task.FromResult(new StagedUpdate(V2, "x")); });
        await f.CheckAsync(default);
        Assert.False(f.CanDownload);
        Assert.Contains("disabled", f.Message, StringComparison.OrdinalIgnoreCase);
        await f.PrepareAsync(null, default);
        Assert.Equal(0, prepares);
        Assert.Equal(UpdatePhase.Available, f.Phase);
        Assert.False(f.CanInstall);
    }

    [Fact]
    public async Task Up_to_date_and_failure_results_are_reported()
    {
        var up = Flow(new UpdateUpToDate(new ReleaseVersion(0, 1, 0)), true);
        await up.CheckAsync(default);
        Assert.Equal(UpdatePhase.UpToDate, up.Phase);
        var bad = Flow(new UpdateCheckFailed(UpdateFailure.Offline, "offline"), true);
        await bad.CheckAsync(default);
        Assert.Equal(UpdatePhase.Failed, bad.Phase);
        Assert.Equal("offline", bad.Message);
    }

    [Fact]
    public async Task Concurrent_check_is_ignored_while_checking()
    {
        var gate = new TaskCompletionSource<UpdateCheckResult>();
        var f = Flow(null!, true, gate: gate);
        var first = f.CheckAsync(default);
        Assert.Equal(UpdatePhase.Checking, f.Phase);
        Assert.False(f.CanCheck);
        await f.CheckAsync(default);                    // returns immediately, no second request
        gate.SetResult(new UpdateUpToDate(null));
        await first;
        Assert.Equal(UpdatePhase.UpToDate, f.Phase);
    }

    [Fact]
    public async Task Failed_or_cancelled_download_returns_to_available_and_is_retryable()
    {
        var attempts = 0;
        var f = Flow(new UpdateAvailable(Rel), true, prepare: () =>
        {
            attempts++;
            return attempts switch
            {
                1 => throw new UpdateException(UpdateFailure.NotTrusted, "signature did not verify"),
                2 => throw new OperationCanceledException(),
                _ => Task.FromResult(new StagedUpdate(V2, "x"))
            };
        });
        await f.CheckAsync(default);
        await f.PrepareAsync(null, default);
        Assert.Equal(UpdatePhase.Available, f.Phase);
        Assert.Contains("signature", f.Message);
        Assert.Null(f.Staged);
        await f.PrepareAsync(null, default);
        Assert.Equal(UpdatePhase.Available, f.Phase);
        Assert.Contains("cancelled", f.Message, StringComparison.OrdinalIgnoreCase);
        await f.PrepareAsync(null, default);
        Assert.Equal(UpdatePhase.Ready, f.Phase);
    }

    [Fact]
    public async Task Declining_the_final_confirmation_discards_the_prepared_update()
    {
        var f = Flow(new UpdateAvailable(Rel), true);
        await f.CheckAsync(default);
        await f.PrepareAsync(null, default);
        f.Discard();
        Assert.Equal(UpdatePhase.Available, f.Phase);
        Assert.Null(f.Staged);
        Assert.False(f.CanInstall);
    }
}
