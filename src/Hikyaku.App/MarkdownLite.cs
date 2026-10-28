using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>
/// Document-scoped math render ownership (generation + dispatcher). Stale completions must not apply.
/// </summary>
public sealed class MathDocScope
{
    public MathDocScope(long documentGeneration, Func<long> getCurrentDocumentGeneration, Dispatcher dispatcher)
    {
        DocumentGeneration = documentGeneration;
        GetCurrentDocumentGeneration = getCurrentDocumentGeneration;
        Dispatcher = dispatcher;
        GlobalGeneration = MathTypesetter.Generation;
    }

    public long DocumentGeneration { get; }
    public long GlobalGeneration { get; }
    public Func<long> GetCurrentDocumentGeneration { get; }
    public Dispatcher Dispatcher { get; }

    public bool IsCurrent =>
        DocumentGeneration == GetCurrentDocumentGeneration()
        && GlobalGeneration == MathTypesetter.Generation;
}

/// <summary>
/// Local markdown→FlowDocument (GFM-ish: fences, tables, bold/italic/inline code, links as text).
/// No network, no HTML/script. TeX math via WPF-Math (local); code/currency/streaming preserved.
/// </summary>
public static partial class MarkdownLite
{
    public const string MathPendingLabel = "[Math: pending — not typeset in this client]";

    private static MathDocScope? _activeScope;

    public static FlowDocument ToDocument(string markdown) => ToDocument(markdown, mathScope: null);

    public static FlowDocument ToDocument(string markdown, MathDocScope? mathScope)
    {
        var previous = _activeScope;
        _activeScope = mathScope;
        try
        {
            return ToDocumentCore(markdown);
        }
        finally
        {
            _activeScope = previous;
        }
    }

    private static FlowDocument ToDocumentCore(string markdown)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = TryResource<FontFamily>("Font.Body") ?? new FontFamily("Segoe UI"),
            FontSize = TryResource<double?>("Size.Body") ?? 14,
            Foreground = TryResource<Brush>("Brush.Text") ?? Brushes.Black
        };
        if (string.IsNullOrEmpty(markdown))
        {
            doc.Blocks.Add(new Paragraph());
            return doc;
        }

        // MathMarkup protects fences/code/currency before any placeholder mangling.
        var text = MathMarkup.Prepare(markdown);
        var parts = FenceRegex().Split(text);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 1)
            {
                var codeText = parts[i].TrimEnd('\n');
                var para = new Paragraph { Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(8) };
                para.Background = TryResource<Brush>("Brush.Well") ?? Brushes.Transparent;
                para.FontFamily = TryResource<FontFamily>("Font.Code") ?? new FontFamily("Consolas");
                para.FontSize = 12.5;
                para.Inlines.Add(new Run(codeText));
                var copy = new Hyperlink(new Run(" Copy")) { FontSize = 11 };
                var captured = codeText;
                copy.Click += (_, _) =>
                {
                    try { Clipboard.SetText(captured); } catch { /* clipboard busy */ }
                };
                para.Inlines.Add(copy);
                doc.Blocks.Add(para);
            }
            else
            {
                foreach (var block in ParseBlocks(parts[i]))
                    doc.Blocks.Add(block);
            }
        }
        return doc;
    }

    private static IEnumerable<Block> ParseBlocks(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            if (IsTableHeader(lines, i, out var headerCells, out var alignRow))
            {
                var rows = new List<string[]> { headerCells };
                i += 2;
                while (i < lines.Length && lines[i].Contains('|') && !string.IsNullOrWhiteSpace(lines[i]))
                {
                    rows.Add(SplitTableRow(lines[i]));
                    i++;
                }
                yield return BuildTable(rows, alignRow);
                continue;
            }

            var line = lines[i];
            if (TrySoleMathLine(line, out var soleFormula))
            {
                yield return BuildDisplayMathBlock(soleFormula);
                i++;
                continue;
            }

            yield return new Paragraph(ParseInline(line));
            i++;
        }
    }

    private static bool TrySoleMathLine(string line, out MathFormula formula)
    {
        formula = default;
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("![math](", StringComparison.Ordinal) || !trimmed.EndsWith(')'))
            return false;
        var link = trimmed["![math](".Length..^1];
        if (MathMarkup.TryParseFormulaLink(link) is not { } f || !f.Display)
            return false;
        formula = f;
        return true;
    }

    private static Block BuildDisplayMathBlock(MathFormula formula)
    {
        var fg = ForegroundColor();
        var em = (TryResource<double?>("Size.Body") ?? 14.0) * 1.15;
        var image = CreateMathImage(null, formula.Latex);
        var fallback = new TextBlock
        {
            Text = formula.Latex,
            FontFamily = TryResource<FontFamily>("Font.Code") ?? new FontFamily("Consolas"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap
        };
        var host = new Grid();
        host.Children.Add(fallback);
        host.Children.Add(image);
        image.Visibility = Visibility.Collapsed;

        BindMathResult(formula.Latex, em, display: true, fg, image, fallback, showImage: bmp =>
        {
            image.Source = bmp;
            image.Visibility = Visibility.Visible;
            fallback.Visibility = Visibility.Collapsed;
        }, showFallback: label =>
        {
            fallback.Text = label;
            fallback.Visibility = Visibility.Visible;
            image.Visibility = Visibility.Collapsed;
        });

        var viewer = new ScrollViewer
        {
            Content = host,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 480,
            Margin = new Thickness(0, 6, 0, 6),
            Focusable = true
        };
        return new BlockUIContainer(viewer) { Margin = new Thickness(0, 4, 0, 8) };
    }

    private static bool IsTableHeader(string[] lines, int i, out string[] header, out string[] align)
    {
        header = Array.Empty<string>();
        align = Array.Empty<string>();
        if (i + 1 >= lines.Length) return false;
        if (!lines[i].Contains('|') || !lines[i + 1].Contains('|')) return false;
        var sep = lines[i + 1].Trim();
        if (!SepRegex().IsMatch(sep)) return false;
        header = SplitTableRow(lines[i]);
        align = SplitTableRow(lines[i + 1]);
        return header.Length > 0;
    }

    private static string[] SplitTableRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return t.Split('|').Select(c => c.Trim()).ToArray();
    }

    private static Block BuildTable(List<string[]> rows, string[] _)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 8) };
        if (rows.Count == 0) return table;
        var cols = rows.Max(r => r.Length);
        for (var c = 0; c < cols; c++)
            table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (var r = 0; r < rows.Count; r++)
        {
            var row = new TableRow();
            for (var c = 0; c < cols; c++)
            {
                var cellText = c < rows[r].Length ? rows[r][c] : "";
                var cell = new TableCell(new Paragraph(ParseInline(cellText)))
                {
                    BorderBrush = TryResource<Brush>("Brush.Border") ?? Brushes.Gray,
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(6, 3, 6, 3)
                };
                if (r == 0) cell.FontWeight = FontWeights.SemiBold;
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        return table;
    }

    private static Span ParseInline(string line)
    {
        var span = new Span();
        var idx = 0;
        while (idx < line.Length)
        {
            var mathStart = line.IndexOf("![math](", idx, StringComparison.Ordinal);
            var nextInline = InlineRegex().Match(line, idx);

            if (mathStart >= 0 && ( !nextInline.Success || mathStart <= nextInline.Index))
            {
                if (mathStart > idx)
                    span.Inlines.Add(new Run(line[idx..mathStart]));
                var linkStart = mathStart + "![math](".Length;
                var linkEnd = line.IndexOf(')', linkStart);
                if (linkEnd < 0)
                {
                    span.Inlines.Add(new Run(line[mathStart..]));
                    break;
                }
                var link = line[linkStart..linkEnd];
                if (MathMarkup.TryParseFormulaLink(link) is { } formula)
                    span.Inlines.Add(CreateMathInline(formula));
                else
                    span.Inlines.Add(new Run(line[mathStart..(linkEnd + 1)]));
                idx = linkEnd + 1;
                continue;
            }

            if (!nextInline.Success)
            {
                span.Inlines.Add(new Run(line[idx..]));
                break;
            }

            if (nextInline.Index > idx)
                span.Inlines.Add(new Run(line[idx..nextInline.Index]));
            if (nextInline.Groups["code"].Success)
            {
                span.Inlines.Add(new Run(nextInline.Groups["code"].Value)
                {
                    FontFamily = TryResource<FontFamily>("Font.Code") ?? new FontFamily("Consolas"),
                    Background = TryResource<Brush>("Brush.Well") ?? Brushes.Transparent
                });
            }
            else if (nextInline.Groups["bold"].Success)
                span.Inlines.Add(new Run(nextInline.Groups["bold"].Value) { FontWeight = FontWeights.SemiBold });
            else if (nextInline.Groups["em"].Success)
                span.Inlines.Add(new Run(nextInline.Groups["em"].Value) { FontStyle = FontStyles.Italic });
            else if (nextInline.Groups["linktext"].Success)
            {
                var label = nextInline.Groups["linktext"].Value;
                var href = nextInline.Groups["href"].Value;
                if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    span.Inlines.Add(new Run($"{label} ({href})"));
                else
                    span.Inlines.Add(new Run(label));
            }
            idx = nextInline.Index + nextInline.Length;
        }
        if (span.Inlines.Count == 0) span.Inlines.Add(new Run(""));
        return span;
    }

    private static Inline CreateMathInline(MathFormula formula)
    {
        var fg = ForegroundColor();
        var em = TryResource<double?>("Size.Body") ?? 14.0;
        var scale = formula.Display ? em * 1.15 : em;
        var image = CreateMathImage(null, formula.Latex);
        image.Margin = new Thickness(2, 0, 2, 0);
        image.Visibility = Visibility.Collapsed;
        var fallback = new TextBlock
        {
            Text = formula.Latex,
            FontFamily = TryResource<FontFamily>("Font.Code") ?? new FontFamily("Consolas"),
            FontSize = 12.5
        };
        var host = new Grid();
        host.Children.Add(fallback);
        host.Children.Add(image);

        BindMathResult(formula.Latex, scale, formula.Display, fg, image, fallback, showImage: bmp =>
        {
            image.Source = bmp;
            image.Visibility = Visibility.Visible;
            fallback.Visibility = Visibility.Collapsed;
        }, showFallback: label =>
        {
            fallback.Text = label;
            fallback.Visibility = Visibility.Visible;
            image.Visibility = Visibility.Collapsed;
        });

        return new InlineUIContainer(host) { BaselineAlignment = BaselineAlignment.Center };
    }

    private static void BindMathResult(
        string latex,
        double em,
        bool display,
        Color fg,
        Image image,
        TextBlock fallback,
        Action<BitmapSource> showImage,
        Action<string> showFallback)
    {
        var scope = _activeScope;
        if (scope is null)
        {
            var result = MathTypesetter.Render(latex, em, display, fg);
            ApplyMathResult(result, showImage, showFallback);
            return;
        }

        if (MathTypesetter.TryGetCached(latex, em, display, fg, out var hit))
        {
            ApplyMathResult(hit, showImage, showFallback);
            return;
        }

        // Exact TeX visible immediately; bitmap fills in without blocking document build.
        fallback.Text = latex;
        MathTypesetter.Request(
            latex, em, display, fg,
            scope.GlobalGeneration,
            () => scope.IsCurrent,
            scope.Dispatcher,
            result => ApplyMathResult(result, showImage, showFallback));
    }

    private static void ApplyMathResult(
        MathRenderResult result,
        Action<BitmapSource> showImage,
        Action<string> showFallback)
    {
        if (result is { Success: true, Bitmap: not null })
            showImage(result.Bitmap);
        else
            showFallback(result.FallbackLabel);
    }

    private static Image CreateMathImage(BitmapSource? bmp, string latex)
    {
        var image = new Image
        {
            Source = bmp,
            Stretch = Stretch.None,
            SnapsToDevicePixels = true,
            ToolTip = latex,
            Focusable = true,
            Tag = latex
        };
        image.MouseRightButtonUp += (_, e) =>
        {
            try
            {
                Clipboard.SetText(latex);
                e.Handled = true;
            }
            catch { /* clipboard busy */ }
        };
        image.KeyDown += (_, e) =>
        {
            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                try { Clipboard.SetText(latex); e.Handled = true; } catch { /* busy */ }
            }
        };
        AutomationHelp.SetName(image, "Math: " + latex);
        return image;
    }

    private static Run CreateFallbackRun(MathRenderResult result)
    {
        var run = new Run(result.FallbackLabel)
        {
            FontFamily = TryResource<FontFamily>("Font.Code") ?? new FontFamily("Consolas"),
            FontSize = 12.5,
            ToolTip = result.SourceLatex
        };
        return run;
    }

    private static Color ForegroundColor()
    {
        var brush = TryResource<Brush>("Brush.Text");
        if (brush is SolidColorBrush scb) return scb.Color;
        return Colors.Black;
    }

    private static T? TryResource<T>(string key)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is T t) return t;
        }
        catch { /* design-time / tests */ }
        return default;
    }

    [GeneratedRegex(@"```[^\n]*\n([\s\S]*?)```", RegexOptions.Compiled)]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"(`(?<code>[^`]+)`)|(\*\*(?<bold>.+?)\*\*)|(\*(?<em>.+?)\*)|(\[(?<linktext>[^\]]+)\]\((?<href>[^)]+)\))", RegexOptions.Compiled)]
    private static partial Regex InlineRegex();

    [GeneratedRegex(@"^\|?(\s*:?-+:?\s*\|)+\s*:?-+:?\s*\|?$")]
    private static partial Regex SepRegex();
}

/// <summary>Minimal automation peer naming without taking a UIAutomation dependency beyond WPF.</summary>
file static class AutomationHelp
{
    public static void SetName(DependencyObject d, string name)
    {
        try
        {
            System.Windows.Automation.AutomationProperties.SetName(d, name);
        }
        catch { /* tests without automation */ }
    }
}

/// <summary>FlowDocument host that re-renders markdown when bound Text changes (coalesced by MessageVM).</summary>
public sealed class MarkdownBlock : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownBlock),
        new PropertyMetadata("", OnMarkdownChanged));

    private long _documentGeneration;

    public MarkdownBlock()
    {
        IsReadOnly = true;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        IsDocumentEnabled = true;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MarkdownBlock box) return;
        var gen = Interlocked.Increment(ref box._documentGeneration);
        var scope = new MathDocScope(gen, () => Interlocked.Read(ref box._documentGeneration), box.Dispatcher);
        box.Document = MarkdownLite.ToDocument(e.NewValue as string ?? "", scope);
    }
}
