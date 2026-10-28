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

/// <summary>
/// Actual WPF typesetting acceptance — bitmap/visual-tree evidence, not source-string presence alone.
/// </summary>
[Collection("StaMath")]
public class Checkpoint13WpfTests
{
    [Fact]
    public void Typesetter_renders_fraction_bitmap_with_nonzero_pixels()
    {
        RunSta(() =>
        {
            MathTypesetter.ClearCache();
            var result = MathTypesetter.Render(@"\frac{a}{b}", 18, display: true, Colors.Black);
            Assert.True(result.Success, result.FallbackReason);
            Assert.NotNull(result.Bitmap);
            Assert.True(result.Bitmap!.PixelWidth > 4);
            Assert.True(result.Bitmap.PixelHeight > 4);
            Assert.True(HasNonTransparentPixels(result.Bitmap));
        });
    }

    [Fact]
    public void Typesetter_unsupported_macro_falls_back_with_source()
    {
        RunSta(() =>
        {
            var latex = @"\notarealmacro{x}";
            var result = MathTypesetter.Render(latex, 16, false, Colors.Black);
            Assert.False(result.Success);
            Assert.Null(result.Bitmap);
            Assert.Contains(latex, result.FallbackLabel, StringComparison.Ordinal);
            Assert.Contains("unsupported", result.FallbackReason!, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Typesetter_oversize_falls_back()
    {
        RunSta(() =>
        {
            var latex = new string('a', MathMarkup.MaxLatexChars + 10) + "^2";
            var result = MathTypesetter.Render(latex, 16, false, Colors.Black);
            Assert.False(result.Success);
            Assert.Equal("oversize", result.FallbackReason);
            Assert.Contains(latex, result.FallbackLabel, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void MarkdownBlock_contains_Image_for_inline_and_display_math()
    {
        RunSta(() =>
        {
            MathTypesetter.ClearCache();
            var md = new MarkdownBlock
            {
                Width = 640,
                Markdown = "Inline $E=mc^2$ and display:\n$$\n\\sum_{i=1}^{n} i\n$$\nCode keeps `$x$`: `$x$` and price $5."
            };
            md.Measure(new Size(640, 1200));
            md.Arrange(new Rect(0, 0, 640, 1200));
            md.UpdateLayout();

            // Async typesetting: pump dispatcher until bitmaps land (or timeout).
            var images = WaitForMathImages(md, minCount: 2, TimeSpan.FromSeconds(5));
            Assert.True(images.Count >= 2, $"expected ≥2 formula images, got {images.Count}");
            foreach (var img in images)
            {
                var bmp = Assert.IsAssignableFrom<BitmapSource>(img.Source);
                Assert.True(bmp.PixelWidth > 2 && bmp.PixelHeight > 2);
                Assert.True(img.Tag is string s && s.Length > 0);
            }

            // Currency / inline code must remain as text runs, not images tagged as prices.
            var text = new TextRange(md.Document.ContentStart, md.Document.ContentEnd).Text;
            Assert.Contains("$5", text, StringComparison.Ordinal);
            Assert.Contains("$x$", text, StringComparison.Ordinal);

            var outDir = Path.Combine(Path.GetTempPath(), "hikyaku-cp13-wpf");
            Directory.CreateDirectory(outDir);
            var path = Path.Combine(outDir, "markdown-math.png");
            Capture(md, path);
            Assert.True(new FileInfo(path).Length > 500);
        });
    }

    [Fact]
    public void MarkdownBlock_malformed_does_not_throw_and_keeps_source()
    {
        RunSta(() =>
        {
            var md = new MarkdownBlock
            {
                Width = 400,
                Markdown = "Bad $$\\frac{a{b}$$ and unknown $\\zzzz{1}$."
            };
            md.Measure(new Size(400, 400));
            md.Arrange(new Rect(0, 0, 400, 400));
            var text = new TextRange(md.Document.ContentStart, md.Document.ContentEnd).Text;
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain(MarkdownLite.MathPendingLabel, text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Cache_clears_on_demand()
    {
        RunSta(() =>
        {
            MathTypesetter.ClearCache();
            _ = MathTypesetter.Render(@"\alpha", 14, false, Colors.Black);
            Assert.True(MathTypesetter.CacheCount >= 1);
            MathTypesetter.ClearCache();
            Assert.Equal(0, MathTypesetter.CacheCount);
        });
    }

    private static List<Image> WaitForMathImages(MarkdownBlock md, int minCount, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        List<Image> images;
        do
        {
            md.Dispatcher.Invoke(DispatcherPriority.Background, static () => { });
            images = EnumerateLogical(md).OfType<Image>()
                .Where(i => i.Source is BitmapSource && i.Visibility == Visibility.Visible)
                .ToList();
            if (images.Count >= minCount) return images;
            Thread.Sleep(25);
        } while (DateTime.UtcNow < deadline);
        return images;
    }

    private static bool HasNonTransparentPixels(BitmapSource bmp)
    {
        var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 8) return true;
        }
        return false;
    }

    private static IEnumerable<object> EnumerateLogical(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject d)
            {
                foreach (var x in EnumerateLogical(d))
                    yield return x;
            }
            else if (child is not null)
                yield return child;
        }
        if (root is FlowDocument fd)
        {
            foreach (var block in fd.Blocks)
            foreach (var x in EnumerateLogical(block))
                yield return x;
        }
        if (root is Paragraph p)
        {
            foreach (var inline in p.Inlines)
            foreach (var x in EnumerateLogical(inline))
                yield return x;
        }
        if (root is Span span)
        {
            foreach (var inline in span.Inlines)
            foreach (var x in EnumerateLogical(inline))
                yield return x;
        }
        if (root is InlineUIContainer iuc && iuc.Child is not null)
        {
            foreach (var x in EnumerateLogical(iuc.Child))
                yield return x;
        }
        if (root is BlockUIContainer buc && buc.Child is not null)
        {
            foreach (var x in EnumerateLogical(buc.Child))
                yield return x;
        }
        if (root is RichTextBox rtb)
        {
            foreach (var x in EnumerateLogical(rtb.Document))
                yield return x;
        }
    }

    private static void Capture(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var w = Math.Max(1, (int)Math.Ceiling(element.DesiredSize.Width));
        var h = Math.Max(1, (int)Math.Ceiling(element.DesiredSize.Height));
        if (w < 2) w = 640;
        if (h < 2) h = 200;
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
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
