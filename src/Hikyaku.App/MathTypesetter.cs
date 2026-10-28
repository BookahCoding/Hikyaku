using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hikyaku.Core;
using WpfMath.Parsers;
using WpfMath.Rendering;
using XamlMath;
using XamlMath.Exceptions;

namespace Hikyaku.App;

/// <summary>
/// Bounded local WPF-Math typesetting. No network, no process spawn, no filesystem from equations.
/// Unsupported/oversize/timeout/backpressure → safe text fallback with reason; original TeX preserved.
/// WPF-Math cannot be cooperatively cancelled; timeouts abandon publish (cache/UI), they do not stop CPU.
/// </summary>
public static class MathTypesetter
{
    public const int MaxCacheEntries = 96;
    public const long MaxCacheBytes = 32L * 1024 * 1024;
    public const int MaxActiveRenders = 2;
    public const int MaxPending = 24;
    public const double MaxEmSize = 96;
    public const int MaxTokens = 512;
    public static readonly TimeSpan RenderBudget = TimeSpan.FromMilliseconds(250);
    public const long MaxPixels = 4096L * 2048L;
    private const double DefaultDpi = 96;

    private readonly record struct CacheKey(string Latex, double Em, bool Display, int Argb);
    private static readonly LinkedList<CacheKey> LruOrder = new();
    private static readonly Dictionary<CacheKey, MathRenderResult> Cache = new();
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<CacheKey, Task<MathRenderResult>> InFlight = new();
    private static readonly ConcurrentDictionary<CacheKey, byte> Abandoned = new();
    private static readonly SemaphoreSlim Workers = new(MaxActiveRenders, MaxActiveRenders);

    private static long _generation;
    private static long _cacheBytes;
    private static int _pending;

    // Test seams (InternalsVisibleTo App.Tests) — never used in production paths unless set by tests.
    internal static int TestStallMs;
    internal static Action? TestOnRenderCoreEntered;
    internal static long? TestMaxPixelsOverride;

    public static long Generation => Interlocked.Read(ref _generation);
    public static int InFlightCount => InFlight.Count;
    public static long CacheBytes
    {
        get { lock (Gate) return _cacheBytes; }
    }

    public static int CacheCount
    {
        get { lock (Gate) return Cache.Count; }
    }

    internal static void ResetTestHooks()
    {
        TestStallMs = 0;
        TestOnRenderCoreEntered = null;
        TestMaxPixelsOverride = null;
    }

    private static long EffectiveMaxPixels => TestMaxPixelsOverride ?? MaxPixels;

    /// <summary>Drop cached bitmaps and abandon in-flight publish (profile/theme switch).</summary>
    public static void ClearCache()
    {
        Interlocked.Increment(ref _generation);
        lock (Gate)
        {
            Cache.Clear();
            LruOrder.Clear();
            _cacheBytes = 0;
        }
    }

    public static bool TryGetCached(string latex, double emSize, bool display, Color foreground, out MathRenderResult result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(latex)) return false;
        var em = ClampEm(emSize);
        var key = new CacheKey(latex, em, display, PackArgb(foreground));
        lock (Gate)
        {
            if (!Cache.TryGetValue(key, out result)) return false;
            Touch(key);
            return true;
        }
    }

    /// <summary>
    /// Synchronous render for tests and callers that need a result now.
    /// May Wait up to <see cref="RenderBudget"/> for worker admission; timeout abandons cache publish.
    /// Prefer <see cref="Request"/> on the UI/document path to avoid per-formula main-thread waits.
    /// </summary>
    public static MathRenderResult Render(string latex, double emSize, bool display, Color foreground)
    {
        if (string.IsNullOrWhiteSpace(latex))
            return MathRenderResult.Fallback(latex ?? "", "empty");
        if (MathMarkup.IsOversize(latex))
            return MathRenderResult.Fallback(latex, "oversize");
        if (MathMarkup.ExceedsNesting(latex))
            return MathRenderResult.Fallback(latex, "nesting");
        if (ExceedsTokens(latex))
            return MathRenderResult.Fallback(latex, "tokens");
        if (!IsFinitePositiveEm(emSize) || emSize > MaxEmSize)
            return MathRenderResult.Fallback(latex, "em-size");

        var em = ClampEm(emSize);
        var key = new CacheKey(latex, em, display, PackArgb(foreground));
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var hit))
            {
                Touch(key);
                return hit;
            }
        }

        var gen = Generation;
        var task = InFlight.GetOrAdd(key, _ => StartWork(key, latex, em, display, foreground, gen));
        try
        {
            if (!task.Wait(RenderBudget))
            {
                // Honest timeout — not cancellation. Worker may continue CPU; forbid cache/UI publish.
                Abandoned[key] = 0;
                return MathRenderResult.Fallback(latex, "timeout");
            }
            return task.Result;
        }
        catch (Exception ex) when (ex is TexException or AggregateException)
        {
            var reason = ex is AggregateException ae && ae.InnerException is TexException te
                ? "unsupported: " + te.Message
                : ex is TexException tx ? "unsupported: " + tx.Message : "unsupported";
            return MathRenderResult.Fallback(latex, TruncateReason(reason));
        }
        catch
        {
            return MathRenderResult.Fallback(latex, "unsupported");
        }
    }

    /// <summary>
    /// Non-blocking render for Markdown/UI. Cache hits invoke <paramref name="onReady"/> synchronously.
    /// Misses queue bounded work; completion is dropped when <paramref name="isCurrent"/> is false
    /// or global generation no longer matches (theme/profile clear).
    /// </summary>
    public static void Request(
        string latex,
        double emSize,
        bool display,
        Color foreground,
        long capturedGeneration,
        Func<bool> isCurrent,
        Dispatcher dispatcher,
        Action<MathRenderResult> onReady)
    {
        if (TryGetCached(latex, emSize, display, foreground, out var hit))
        {
            onReady(hit);
            return;
        }

        if (string.IsNullOrWhiteSpace(latex))
        {
            onReady(MathRenderResult.Fallback(latex ?? "", "empty"));
            return;
        }
        if (MathMarkup.IsOversize(latex))
        {
            onReady(MathRenderResult.Fallback(latex, "oversize"));
            return;
        }
        if (MathMarkup.ExceedsNesting(latex))
        {
            onReady(MathRenderResult.Fallback(latex, "nesting"));
            return;
        }
        if (ExceedsTokens(latex))
        {
            onReady(MathRenderResult.Fallback(latex, "tokens"));
            return;
        }
        if (!IsFinitePositiveEm(emSize) || emSize > MaxEmSize)
        {
            onReady(MathRenderResult.Fallback(latex, "em-size"));
            return;
        }

        var em = ClampEm(emSize);
        var key = new CacheKey(latex, em, display, PackArgb(foreground));
        if (Volatile.Read(ref _pending) >= MaxPending && !InFlight.ContainsKey(key))
        {
            onReady(MathRenderResult.Fallback(latex, "backpressure"));
            return;
        }

        var gen = capturedGeneration;
        var task = InFlight.GetOrAdd(key, _ => StartWork(key, latex, em, display, foreground, gen));
        _ = task.ContinueWith(t =>
        {
            MathRenderResult result;
            try { result = t.Status == TaskStatus.RanToCompletion ? t.Result : MathRenderResult.Fallback(latex, "unsupported"); }
            catch { result = MathRenderResult.Fallback(latex, "unsupported"); }

            if (gen != Generation || !isCurrent())
                return;

            try
            {
                dispatcher.BeginInvoke(() =>
                {
                    if (gen != Generation || !isCurrent()) return;
                    onReady(result);
                }, DispatcherPriority.Background);
            }
            catch
            {
                /* dispatcher shut down */
            }
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    private static Task<MathRenderResult> StartWork(
        CacheKey key, string latex, double em, bool display, Color foreground, long gen)
    {
        Interlocked.Increment(ref _pending);
        return Task.Run(async () =>
        {
            try
            {
                if (!await Workers.WaitAsync(RenderBudget).ConfigureAwait(false))
                    return MathRenderResult.Fallback(latex, "backpressure");

                try
                {
                    if (gen != Generation)
                        return MathRenderResult.Fallback(latex, "stale");

                    var result = RenderCore(latex, em, display, foreground);
                    TryPublish(key, result, gen);
                    return result;
                }
                finally
                {
                    Workers.Release();
                }
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
                InFlight.TryRemove(key, out _);
            }
        });
    }

    private static void TryPublish(CacheKey key, MathRenderResult result, long gen)
    {
        if (gen != Generation) return;
        if (Abandoned.TryRemove(key, out _)) return;
        // Do not cache pure timeout/backpressure/stale placeholders from other paths.
        if (!result.Success && result.FallbackReason is "timeout" or "backpressure" or "stale")
            return;

        lock (Gate)
        {
            if (gen != Generation) return;
            if (Abandoned.ContainsKey(key)) return;
            var bytes = EstimateBytes(result);
            if (Cache.TryGetValue(key, out var existing))
            {
                _cacheBytes -= EstimateBytes(existing);
                Cache[key] = result;
                _cacheBytes += bytes;
                Touch(key);
            }
            else
            {
                Cache[key] = result;
                _cacheBytes += bytes;
                Touch(key);
            }

            while ((Cache.Count > MaxCacheEntries || _cacheBytes > MaxCacheBytes) && LruOrder.First is { } node)
            {
                if (Cache.Remove(node.Value, out var evicted))
                    _cacheBytes -= EstimateBytes(evicted);
                LruOrder.RemoveFirst();
            }
            if (_cacheBytes < 0) _cacheBytes = 0;
        }
    }

    private static MathRenderResult RenderCore(string latex, double emSize, bool display, Color foreground)
    {
        TestOnRenderCoreEntered?.Invoke();
        if (TestStallMs > 0)
            Thread.Sleep(TestStallMs);

        try
        {
            var parser = WpfTeXFormulaParser.Instance;
            var formula = parser.Parse(latex);
            var style = display ? TexStyle.Display : TexStyle.Text;
            var fg = new SolidColorBrush(foreground);
            if (fg.CanFreeze) fg.Freeze();
            var env = WpfTeXEnvironment.Create(style, emSize, "Segoe UI", fg, Brushes.Transparent);

            // Measure via DrawingVisual (same first phase as WpfMath RenderToBitmap) BEFORE RTB alloc.
            if (!TryMeasure(formula, env, emSize, out var width, out var height, out var visual))
                return MathRenderResult.Fallback(latex, "oversize-layout");

            var maxPx = EffectiveMaxPixels;
            if (width <= 0 || height <= 0 || (long)width * height > maxPx)
                return MathRenderResult.Fallback(latex, "oversize-bitmap");

            var bmp = new RenderTargetBitmap(width, height, DefaultDpi, DefaultDpi, PixelFormats.Default);
            bmp.Render(visual);
            if (bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0
                || (long)bmp.PixelWidth * bmp.PixelHeight > maxPx)
                return MathRenderResult.Fallback(latex, "oversize-bitmap");
            if (bmp.CanFreeze) bmp.Freeze();
            return MathRenderResult.Ok(latex, bmp, display);
        }
        catch (TexException ex)
        {
            return MathRenderResult.Fallback(latex, TruncateReason("unsupported: " + ex.Message));
        }
        catch (Exception)
        {
            return MathRenderResult.Fallback(latex, "unsupported");
        }
    }

    /// <summary>
    /// Layout formula into a DrawingVisual and compute pixel size. Rejects NaN/Inf/overflow.
    /// Mirrors WpfMath RenderWithPositiveCoordinates offset behaviour without allocating RTB.
    /// </summary>
    private static bool TryMeasure(
        TexFormula formula,
        TexEnvironment env,
        double scale,
        out int width,
        out int height,
        out DrawingVisual visual)
    {
        width = 0;
        height = 0;
        visual = new DrawingVisual();
        try
        {
            using (var dc = visual.RenderOpen())
                formula.RenderTo(dc, env, scale, 0, 0);

            var bounds = visual.ContentBounds;
            if (!IsFiniteRect(bounds))
                return false;

            if (bounds is { X: < 0 } or { Y: < 0 })
            {
                visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.PushTransform(new TranslateTransform(Math.Max(0.0, -bounds.X), Math.Max(0.0, -bounds.Y)));
                    formula.RenderTo(dc, env, scale, 0, 0);
                }
                bounds = visual.ContentBounds;
                if (!IsFiniteRect(bounds))
                    return false;
            }

            var w = (bounds.Right) * (DefaultDpi / DefaultDpi);
            var h = (bounds.Bottom) * (DefaultDpi / DefaultDpi);
            if (!IsFinite(w) || !IsFinite(h) || w <= 0 || h <= 0)
                return false;
            if (w > int.MaxValue || h > int.MaxValue)
                return false;

            width = Math.Max(1, (int)Math.Ceiling(w));
            height = Math.Max(1, (int)Math.Ceiling(h));
            // Reject only arithmetic overflow here; MaxPixels is enforced by caller before RTB alloc.
            if (width > 0 && height > long.MaxValue / width)
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFiniteRect(Rect r) =>
        IsFinite(r.X) && IsFinite(r.Y) && IsFinite(r.Width) && IsFinite(r.Height)
        && IsFinite(r.Left) && IsFinite(r.Right) && IsFinite(r.Top) && IsFinite(r.Bottom);

    private static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);

    private static bool IsFinitePositiveEm(double em) => IsFinite(em) && em > 0;

    private static double ClampEm(double emSize) => Math.Min(emSize, MaxEmSize);

    private static bool ExceedsTokens(string latex)
    {
        var tokens = 0;
        for (var i = 0; i < latex.Length; i++)
        {
            if (latex[i] == '\\')
            {
                tokens++;
                if (tokens > MaxTokens) return true;
            }
        }
        return false;
    }

    private static long EstimateBytes(MathRenderResult result)
    {
        if (result.Bitmap is null) return 64 + (result.SourceLatex?.Length ?? 0) * 2L;
        return 256L + (long)result.Bitmap.PixelWidth * result.Bitmap.PixelHeight * 4L
               + (result.SourceLatex?.Length ?? 0) * 2L;
    }

    private static void Touch(CacheKey key)
    {
        LruOrder.Remove(key);
        LruOrder.AddLast(key);
    }

    private static int PackArgb(Color c) => (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;

    private static string TruncateReason(string reason) =>
        reason.Length <= 120 ? reason : reason[..117] + "...";
}

public readonly record struct MathRenderResult(
    string SourceLatex,
    BitmapSource? Bitmap,
    bool Display,
    bool Success,
    string? FallbackReason)
{
    public static MathRenderResult Ok(string latex, BitmapSource bmp, bool display) =>
        new(latex, bmp, display, true, null);

    public static MathRenderResult Fallback(string latex, string reason) =>
        new(latex, null, false, false, reason);

    public string FallbackLabel =>
        FallbackReason is null
            ? SourceLatex
            : $"[Math: {FallbackReason}] {SourceLatex}";
}
