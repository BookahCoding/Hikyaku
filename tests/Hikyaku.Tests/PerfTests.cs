using System.Diagnostics;
using Hikyaku.Core;
using Xunit;
using Xunit.Abstractions;

namespace Hikyaku.Tests;

/// <summary>Measurements are printed (see test output), not asserted tightly: hardware-dependent.</summary>
public class PerfTests(ITestOutputHelper output)
{
    [Fact]
    public void CoalescerCostForLongStream()
    {
        var chars = 0; var flushes = 0;
        var c = new TokenCoalescer(s => { chars += s.Length; flushes++; }, TimeSpan.FromMilliseconds(50));
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 200_000; i++) c.Append("tok ");
        c.Flush();
        sw.Stop();
        output.WriteLine($"MEASURED coalescer: 200000 tokens in {sw.Elapsed.TotalMilliseconds:F1} ms, {flushes} flushes");
        Assert.Equal(800_000, chars);
    }

    [Fact]
    public async Task SelectionFilterCostOnLargeList()
    {
        var s = new ProfileSelectionService(new MemoryStore());
        await s.SetSelectedAsync(new[] { new ProfileKey("g", "p0") });
        var items = Enumerable.Range(0, 100_000).Select(i => new ProfileKey("g", "p" + (i % 50))).ToList();
        var sw = Stopwatch.StartNew();
        var kept = s.Filter(items, k => k);
        sw.Stop();
        output.WriteLine($"MEASURED filter: 100000 items in {sw.Elapsed.TotalMilliseconds:F1} ms, kept {kept.Count}");
        Assert.Equal(2000, kept.Count);
    }
}
