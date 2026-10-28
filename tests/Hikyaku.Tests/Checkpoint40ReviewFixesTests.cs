using System.Net;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Release review (v1.0.0) — defects found by the final findings-first review, each reproduced here before it was fixed.
/// The update buttons await the flow from an async void click handler, so anything the flow lets escape closes the whole app.
/// </summary>
public class Checkpoint40ReviewFixesTests
{
    private static readonly ReleaseVersion V2 = new(1, 0, 1);
    private static readonly UpdateRelease Rel = new(V2, "v1.0.1", "n",
        new Uri("https://github.com/o/r/releases/download/v1.0.1/Hikyaku-win-x64.zip"),
        new Uri("https://github.com/o/r/releases/download/v1.0.1/Hikyaku-win-x64.zip.sig"),
        new Uri("https://github.com/o/r/releases/tag/v1.0.1"), 10);

    public static TheoryData<string> Failures => new() { "network", "disk", "disposed" };

    private static Exception Make(string kind) => kind switch
    {
        "network" => new HttpRequestException("The connection was reset."),
        "disk" => new IOException("There is not enough space on the disk."),
        _ => new ObjectDisposedException("SocketsHttpHandler")
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_failed_download_is_reported_and_can_be_retried_instead_of_escaping(string kind)
    {
        var f = new UpdateFlow(_ => Task.FromResult<UpdateCheckResult>(new UpdateAvailable(Rel)),
            (_, _, _) => Task.FromException<StagedUpdate>(Make(kind)), installerTrusted: true);
        await f.CheckAsync(default);

        await f.PrepareAsync(null, default);                                   // must not throw

        Assert.Equal(UpdatePhase.Available, f.Phase);                          // not stuck on "Downloading"
        Assert.True(f.CanDownload);                                           // the person can try again
        Assert.False(f.CanInstall);
        Assert.Contains("Nothing was installed", f.Message);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_failed_check_is_reported_instead_of_escaping(string kind)
    {
        var f = new UpdateFlow(_ => Task.FromException<UpdateCheckResult>(Make(kind)),
            (_, _, _) => Task.FromResult(new StagedUpdate(V2, "x")), installerTrusted: true);

        await f.CheckAsync(default);                                          // must not throw

        Assert.Equal(UpdatePhase.Failed, f.Phase);
        Assert.True(f.CanCheck);
        Assert.Null(f.Release);
    }

    private sealed class BrokenBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) });
    }

    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Unable to read data from the transport connection.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_connection_that_drops_while_the_release_answer_is_read_is_reported_as_offline()
    {
        using var handler = new BrokenBodyHandler();
        var result = await new UpdateChecker(handler, "o/r", new ReleaseVersion(1, 0, 0)).CheckAsync(default);
        var failed = Assert.IsType<UpdateCheckFailed>(result);
        Assert.Equal(UpdateFailure.Offline, failed.Kind);
    }
}
