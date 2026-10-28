using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

[CollectionDefinition("StaMath", DisableParallelization = true)]
public class StaMathCollection;

/// <summary>
/// Checkpoint 13b — bounded renderer regressions (prealloc, timeout abandon, cache gen, UI dispatch).
/// Timings/traces recorded; not boolean-policy-only.
/// </summary>
[Collection("StaMath")]
public class Checkpoint13bWpfTests
{
    [Fact]
    public void Em_size_above_cap_rejected_without_render()
    {
        RunSta(() =>
        {
            MathTypesetter.ResetTestHooks();
            MathTypesetter.ClearCache();
            var result = MathTypesetter.Render(@"E=mc^2", emSize: 500, display: true, Colors.Black);
            Assert.False(result.Success);
            Assert.Equal("em-size", result.FallbackReason);
            Assert.Contains(@"E=mc^2", result.FallbackLabel, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Prealloc_check_rejects_oversize_without_huge_bitmap_allocation()
    {
        RunSta(() =>
        {
            MathTypesetter.ResetTestHooks();
            MathTypesetter.ClearCache();
            // Cap MaxPixels tiny so a normal fraction's measured size fails BEFORE RTB allocation.
            MathTypesetter.TestMaxPixelsOverride = 64; // 8×8
            var latex = @"\frac{a}{b}";

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetTotalMemory(true);
            var sw = Stopwatch.StartNew();
            var result = MathTypesetter.Render(latex, emSize: 18, display: true, Colors.Black);
            sw.Stop();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var after = GC.GetTotalMemory(true);
            var delta = after - before;

            Assert.False(result.Success, "must reject before publishing oversize bitmap");
            Assert.Null(result.Bitmap);
            Assert.Contains(latex, result.FallbackLabel, StringComparison.Ordinal);
            Assert.Equal("oversize-bitmap", result.FallbackReason);
            // Must not have allocated a normal formula bitmap then discarded; stay well under 1MB delta.
            Assert.True(delta < 1_500_000, $"GC delta {delta} suggests RTB allocated before reject; ms={sw.ElapsedMilliseconds}");
            TraceLine($"prealloc: reason={result.FallbackReason} ms={sw.ElapsedMilliseconds} gcDelta={delta}");
            MathTypesetter.ResetTestHooks();
        });
    }

    [Fact]
    public void Timeout_does_not_publish_abandoned_work_to_cache()
    {
        RunSta(() =>
        {
            MathTypesetter.ResetTestHooks();
            MathTypesetter.ClearCache();
            MathTypesetter.TestStallMs = 600;
            var latex = @"\alpha+\beta";
            var sw = Stopwatch.StartNew();
            var result = MathTypesetter.Render(latex, 16, false, Colors.Black);
            sw.Stop();
            Assert.False(result.Success);
            Assert.Equal("timeout", result.FallbackReason);
            Assert.True(sw.ElapsedMilliseconds >= 200, $"expected Wait budget, got {sw.ElapsedMilliseconds}ms");
            Assert.DoesNotContain("cancel", result.FallbackReason, StringComparison.OrdinalIgnoreCase);

            // Worker still running (stall); must not land in cache when it finishes.
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && MathTypesetter.InFlightCount > 0)
                Thread.Sleep(50);
            Thread.Sleep(100);
            Assert.Equal(0, MathTypesetter.CacheCount);
            Assert.Equal(0, MathTypesetter.InFlightCount);
            TraceLine($"timeout-abandon: waitMs={sw.ElapsedMilliseconds} cache={MathTypesetter.CacheCount}");
            MathTypesetter.ResetTestHooks();
        });
    }

    [Fact]
    public void ClearCache_bumps_generation_and_drops_inflight_completion()
    {
        RunSta(() =>
        {
            MathTypesetter.ResetTestHooks();
            MathTypesetter.ClearCache();
            var gen0 = MathTypesetter.Generation;
            MathTypesetter.TestStallMs = 400;
            var started = new ManualResetEventSlim(false);
            MathTypesetter.TestOnRenderCoreEntered = () => started.Set();

            var renderTask = Task.Run(() =>
            {
                Exception? caught = null;
                MathRenderResult? box = null;
                var t = new Thread(() =>
                {
                    try { box = MathTypesetter.Render(@"\gamma", 16, false, Colors.Black); }
                    catch (Exception ex) { caught = ex; }
                });
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join();
                if (caught is not null) throw caught;
                return box!.Value;
            });

            Assert.True(started.Wait(2000), "render core did not start");
            MathTypesetter.ClearCache();
            Assert.True(MathTypesetter.Generation > gen0);

            var result = renderTask.Result;
            Assert.False(result.Success);
            // timeout or abandoned — either way must not repopulate cache after clear
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && MathTypesetter.InFlightCount > 0)
                Thread.Sleep(50);
            Thread.Sleep(100);
            Assert.Equal(0, MathTypesetter.CacheCount);
            TraceLine($"clear-gen: gen0={gen0} gen={MathTypesetter.Generation} reason={result.FallbackReason}");
            MathTypesetter.ResetTestHooks();
        });
    }

    [Fact]
    public void Malformed_nested_and_adversarial_fail_closed_with_source()
    {
        RunSta(() =>
        {
            MathTypesetter.ResetTestHooks();
            MathTypesetter.ClearCache();
            var nested = @"\frac{" + string.Concat(Enumerable.Repeat(@"\frac{1}{2}", 40)) + "}{3}";
            var r1 = MathTypesetter.Render(nested, 18, true, Colors.Black);
            Assert.Contains(nested, r1.FallbackLabel, StringComparison.Ordinal);
            // May succeed if library handles it; must never throw or return empty success bitmap
            if (r1.Success)
            {
                Assert.NotNull(r1.Bitmap);
                Assert.True(r1.Bitmap!.PixelWidth > 0 && r1.Bitmap.PixelHeight > 0);
                Assert.True((long)r1.Bitmap.PixelWidth * r1.Bitmap.PixelHeight <= MathTypesetter.MaxPixels);
            }
            else
                Assert.False(string.IsNullOrWhiteSpace(r1.FallbackReason));

            var bad = @"\frac{a{b";
            var r2 = MathTypesetter.Render(bad, 16, false, Colors.Black);
            Assert.False(r2.Success);
            Assert.Contains(bad, r2.FallbackLabel, StringComparison.Ordinal);

            var md = new MarkdownBlock
            {
                Width = 400,
                Markdown = "Bad $$\\frac{a{b}$$ and $\\zzzz{1}$ and $5."
            };
            md.Measure(new Size(400, 400));
            md.Arrange(new Rect(0, 0, 400, 400));
            var text = new TextRange(md.Document.ContentStart, md.Document.ContentEnd).Text;
            Assert.Contains("$5", text, StringComparison.Ordinal);
            Assert.DoesNotContain(MarkdownLite.MathPendingLabel, text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Streaming_markdown_updates_keep_dispatcher_responsive()
    {
        RunSta(() =>
        {
            MathTypesetter.ResetTestHooks();
            MathTypesetter.ClearCache();
            var frame = new DispatcherFrame();
            var pumped = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(5)
            };
            timer.Tick += (_, _) =>
            {
                pumped++;
                if (pumped >= 8) frame.Continue = false;
            };
            timer.Start();

            var md = new MarkdownBlock { Width = 640 };
            var sw = Stopwatch.StartNew();
            // Simulate streaming: many incremental markdown sets with formulas
            for (var i = 1; i <= 20; i++)
            {
                var body = string.Join(" ", Enumerable.Range(0, i).Select(k => $"$x_{{{k}}}$"));
                md.Markdown = $"Stream {i}: {body}";
                md.Dispatcher.Invoke(DispatcherPriority.Background, () => { });
            }
            // Pump while any background typesetting finishes
            Dispatcher.PushFrame(frame);
            timer.Stop();
            sw.Stop();

            Assert.True(pumped >= 8, $"dispatcher starved; pumped={pumped} elapsedMs={sw.ElapsedMilliseconds}");
            // Document build itself must not serialize 20×250ms waits on UI
            Assert.True(sw.ElapsedMilliseconds < 4000, $"streaming UI path too slow: {sw.ElapsedMilliseconds}ms");
            var text = new TextRange(md.Document.ContentStart, md.Document.ContentEnd).Text;
            Assert.Contains("Stream 20", text, StringComparison.Ordinal);
            TraceLine($"streaming-ui: pumped={pumped} ms={sw.ElapsedMilliseconds} cache={MathTypesetter.CacheCount}");
            // Allow background work to settle / abandon
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && MathTypesetter.InFlightCount > 0)
            {
                md.Dispatcher.Invoke(DispatcherPriority.Background, () => { });
                Thread.Sleep(20);
            }
        });
    }

    [Fact]
    public void Cache_hit_still_renders_fraction_bitmap()
    {
        RunSta(() =>
        {
            MathTypesetter.ResetTestHooks();
            MathTypesetter.ClearCache();
            // The first draw on a cold runner can miss the 250 ms budget. A timed-out draw is not cached.
            MathRenderResult a = default;
            for (var i = 0; i < 8; i++)
            {
                a = MathTypesetter.Render(@"\frac{a}{b}", 18, true, Colors.Black);
                if (a.Success && MathTypesetter.CacheCount > 0) break;
            }
            Assert.True(a.Success, a.FallbackReason);
            Assert.True(MathTypesetter.CacheCount > 0);
            var b = MathTypesetter.Render(@"\frac{a}{b}", 18, true, Colors.Black);
            Assert.True(b.Success, b.FallbackReason);
            Assert.True(ReferenceEquals(a.Bitmap, b.Bitmap), "cache must return same bitmap instance");
            Assert.True(HasNonTransparentPixels(b.Bitmap!));
        });
    }

    [Fact]
    public void Dedupe_same_formula_single_inflight()
    {
        RunSta(() =>
        {
            MathTypesetter.ResetTestHooks();
            MathTypesetter.ClearCache();
            MathTypesetter.TestStallMs = 150;
            var keyLatex = @"\delta+\epsilon";
            var barrier = new ManualResetEventSlim(false);
            MathTypesetter.TestOnRenderCoreEntered = () => barrier.Wait(2000);

            var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                MathRenderResult? r = null;
                Exception? ex = null;
                var th = new Thread(() =>
                {
                    try { r = MathTypesetter.Render(keyLatex, 16, false, Colors.Black); }
                    catch (Exception e) { ex = e; }
                });
                th.SetApartmentState(ApartmentState.STA);
                th.Start();
                th.Join();
                if (ex is not null) throw ex;
                return r!.Value;
            })).ToArray();

            Thread.Sleep(80);
            Assert.True(MathTypesetter.InFlightCount <= 1, $"expected single-flight, inflight={MathTypesetter.InFlightCount}");
            barrier.Set();
            Task.WaitAll(tasks);
            MathTypesetter.ResetTestHooks();
            Assert.True(tasks.All(t => t.Result.Success || t.Result.FallbackReason is "timeout" or "backpressure"),
                string.Join(",", tasks.Select(t => t.Result.FallbackReason)));
            TraceLine($"dedupe: inflightMaxObserved<=1 successes={tasks.Count(t => t.Result.Success)}");
        });
    }

    private static bool HasNonTransparentPixels(BitmapSource bmp)
    {
        var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] > 8) return true;
        return false;
    }

    private static void TraceLine(string line)
    {
        var dir = Path.Combine("artifacts", "checkpoint13b");
        try
        {
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "test-traces.txt"), line + Environment.NewLine);
        }
        catch { /* evidence best-effort */ }
        Debug.WriteLine(line);
    }

    private static void RunSta(Action action)
    {
        Exception? caught = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { caught = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (caught is not null) throw caught;
    }
}
