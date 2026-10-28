using System.Net;
using System.Net.Http;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

internal sealed class MemoryStore(string? initial = null) : IPreferenceStore
{
    public string? Content = initial;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Content);
    public Task SaveAsync(string content, CancellationToken ct = default) { Content = content; return Task.CompletedTask; }
}

public class ProfileSelectionTests
{
    private static readonly ProfileKey A = new("g1", "a");
    private static readonly ProfileKey B = new("g1", "b");
    private static readonly ProfileKey AOtherGateway = new("g2", "a");

    [Fact]
    public void DeniesEverythingBeforeLoad()
    {
        var s = new ProfileSelectionService(new MemoryStore());
        Assert.False(s.IsLoaded);
        Assert.False(s.IsAllowed(A));
        Assert.Empty(s.Filter(new[] { A, B }, k => k));
        Assert.False(s.NeedsOnboarding); // unknown until loaded
    }

    [Fact]
    public async Task EmptySelectionMeansOnboardingNotAll()
    {
        var s = new ProfileSelectionService(new MemoryStore());
        await s.LoadAsync();
        Assert.True(s.NeedsOnboarding);
        Assert.Empty(s.Filter(new[] { A, B }, k => k));
    }

    [Fact]
    public async Task PersistsAcrossInstancesAndLoadsAtStartup()
    {
        var store = new MemoryStore();
        var s1 = new ProfileSelectionService(store);
        await s1.SetSelectedAsync(new[] { A });
        var s2 = new ProfileSelectionService(store);
        Assert.False(s2.IsAllowed(A)); // startup, before load: still denied
        await s2.LoadAsync();
        Assert.True(s2.IsAllowed(A));
        Assert.False(s2.IsAllowed(B));
    }

    [Fact]
    public async Task IdentityIsGatewayAndProfileNotProfileAlone()
    {
        var s = new ProfileSelectionService(new MemoryStore());
        await s.SetSelectedAsync(new[] { A });
        Assert.False(s.IsAllowed(AOtherGateway));
    }

    [Fact]
    public async Task NewlyDiscoveredProfileIsNotAutoSelected()
    {
        var s = new ProfileSelectionService(new MemoryStore());
        await s.SetSelectedAsync(new[] { A });
        var fresh = s.UnselectedAmong(new[] { A, B });
        Assert.Equal(new[] { B }, fresh);
        Assert.False(s.IsAllowed(B));
        Assert.Single(s.Selected);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Version\":1,\"Selected\":[{\"GatewayId\":\"\",\"ProfileId\":\"x\"}]}")]
    public async Task CorruptOrInvalidPreferencesFailClosed(string raw)
    {
        var s = new ProfileSelectionService(new MemoryStore(raw));
        await s.LoadAsync();
        Assert.True(s.IsLoaded);
        Assert.True(s.NeedsOnboarding);
    }

    [Fact]
    public async Task InstallationsAreIndependent()
    {
        var one = new ProfileSelectionService(new MemoryStore());
        var two = new ProfileSelectionService(new MemoryStore());
        await one.SetSelectedAsync(new[] { A });
        await two.LoadAsync();
        Assert.True(one.IsAllowed(A));
        Assert.False(two.IsAllowed(A));
    }

    [Fact]
    public async Task FilePreferenceStoreRoundTripsAndReplacesAtomically()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hikyaku-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "prefs.json");
            var s = new ProfileSelectionService(new FilePreferenceStore(path));
            await s.SetSelectedAsync(new[] { A, B });
            await s.SetSelectedAsync(new[] { B });
            Assert.False(File.Exists(path + ".tmp"));
            var s2 = new ProfileSelectionService(new FilePreferenceStore(path));
            await s2.LoadAsync();
            Assert.Equal(new[] { B }, s2.Selected);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

public class NotificationGateTests
{
    private static readonly ProfileKey A = new("g1", "a");
    private static readonly ProfileKey B = new("g1", "b");

    [Fact]
    public async Task GateCoversEverySurfaceForSelectedOnly_IncludingReconnectAndColdLaunch()
    {
        var store = new MemoryStore();
        await new ProfileSelectionService(store).SetSelectedAsync(new[] { A });

        // cold launch / reconnect: fresh service on the same store, gate closed until loaded
        var s = new ProfileSelectionService(store);
        var gate = new NotificationGate(s);
        foreach (var surface in Enum.GetValues<Surface>())
            Assert.False(gate.Allow(surface, A));
        await s.LoadAsync();
        foreach (var surface in Enum.GetValues<Surface>())
        {
            Assert.True(gate.Allow(surface, A));
            Assert.False(gate.Allow(surface, B));
            Assert.False(gate.Allow(surface, null));
        }
    }

    [Fact]
    public async Task DeselectingClosesTheGateImmediately()
    {
        var s = new ProfileSelectionService(new MemoryStore());
        var gate = new NotificationGate(s);
        await s.SetSelectedAsync(new[] { A });
        Assert.True(gate.Allow(Surface.Toast, A));
        await s.SetSelectedAsync(Array.Empty<ProfileKey>());
        Assert.False(gate.Allow(Surface.Toast, A));
    }
}

public class TransportTests
{
    [Fact]
    public async Task SseParserJoinsMultilineDataSkipsCommentsAndFlushesTail()
    {
        var text = ": keepalive\ndata: one\n\ndata: a\ndata: b\n\ndata: tail";
        var got = new List<string>();
        await foreach (var d in SseParser.ReadDataAsync(new StringReader(text))) got.Add(d);
        Assert.Equal(new[] { "one", "a\nb", "tail" }, got);
    }

    [Fact]
    public void ExtractsDeltaAndIgnoresMalformed()
    {
        Assert.Equal("hi", HttpGatewayTransport.TryExtractDelta("{\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}"));
        Assert.Null(HttpGatewayTransport.TryExtractDelta("{\"choices\":[]}"));
        Assert.Null(HttpGatewayTransport.TryExtractDelta("garbage"));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => f(r, ct);
    }

    [Fact]
    public async Task HttpTransportStreamsTokensSendsBearerAndNeverLeaksItInUrl()
    {
        HttpRequestMessage? seen = null;
        var body = "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\ndata: [DONE]\n\n";
        var http = new HttpClient(new FakeHandler((r, _) =>
        {
            seen = r;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }));
        var t = new HttpGatewayTransport(http, new Uri("https://gateway.example.invalid"), () => "placeholder-token");
        var req = new ChatRequest(new ProfileKey("g", "p"), new[] { new ChatMessage("user", "hi") });
        var events = new List<ChatEvent>();
        await foreach (var e in t.StreamChatAsync(req, default)) events.Add(e);
        Assert.Equal("Hello", string.Concat(events.OfType<TokenDelta>().Select(d => d.Text)));
        Assert.IsType<StreamCompleted>(events[^1]);
        Assert.Equal("Bearer", seen!.Headers.Authorization!.Scheme);
        Assert.DoesNotContain("placeholder-token", seen.RequestUri!.ToString());
    }

    [Fact]
    public async Task CancellationAbortsTheStream()
    {
        var http = new HttpClient(new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }));
        var t = new HttpGatewayTransport(http, new Uri("https://gateway.example.invalid"), () => null);
        using var cts = new CancellationTokenSource(50);
        var req = new ChatRequest(new ProfileKey("g", "p"), Array.Empty<ChatMessage>());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in t.StreamChatAsync(req, cts.Token)) { }
        });
    }

    [Fact]
    public async Task ProfileDiscoveryIsHonestlyUnsupported()
    {
        var t = new HttpGatewayTransport(new HttpClient(), new Uri("https://gateway.example.invalid"), () => null);
        await Assert.ThrowsAsync<NotSupportedException>(() => t.ListProfilesAsync(default));
    }
}

public class ReconnectAndRenderingTests
{
    [Fact]
    public void BackoffIsExponentialCappedAndBounded()
    {
        var p = new ReconnectPolicy(4, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(1), p.DelayFor(0));
        Assert.Equal(TimeSpan.FromSeconds(2), p.DelayFor(1));
        Assert.Equal(TimeSpan.FromSeconds(3), p.DelayFor(2));
        Assert.Equal(TimeSpan.FromSeconds(3), p.DelayFor(3));
        Assert.Null(p.DelayFor(4));
    }

    [Fact]
    public async Task RetriesTransientThenSucceeds_AndGivesUpWhenExhausted()
    {
        var p = new ReconnectPolicy(3, TimeSpan.Zero, TimeSpan.Zero);
        var calls = 0;
        await p.RunAsync(_ => ++calls < 3 ? throw new HttpRequestException() : Task.CompletedTask,
            (_, _) => Task.CompletedTask, default);
        Assert.Equal(3, calls);

        calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            p.RunAsync(_ => { calls++; throw new HttpRequestException(); }, (_, _) => Task.CompletedTask, default));
        Assert.Equal(4, calls); // first try + 3 retries
    }

    [Fact]
    public async Task CancellationStopsRetrying()
    {
        var p = new ReconnectPolicy(10, TimeSpan.Zero, TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            p.RunAsync(_ => { calls++; cts.Cancel(); throw new HttpRequestException(); }, (_, _) => Task.CompletedTask, cts.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CoalescerBoundsUiUpdatesAndLosesNoText()
    {
        var now = new DateTime(2026, 1, 1);
        var flushed = new List<string>();
        var c = new TokenCoalescer(flushed.Add, TimeSpan.FromMilliseconds(50), () => now);
        for (var i = 0; i < 1000; i++)
        {
            c.Append("x");
            now = now.AddMilliseconds(1); // 1000 tokens over 1 s
        }
        c.Flush();
        Assert.Equal(1000, string.Concat(flushed).Length);
        Assert.InRange(c.FlushCount, 1, 25); // ~20 flushes, not 1000
    }
}
