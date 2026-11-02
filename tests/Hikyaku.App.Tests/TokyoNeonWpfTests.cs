using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

[Collection("StaMath")]
public class TokyoNeonWpfTests
{
    [Fact]
    public void Applying_a_palette_and_previewing_it_keeps_both_accents()
    {
        RunSta(() =>
        {
            var resources = new ResourceDictionary();
            foreach (var id in new[] { "shibuya-night", "akihabara", "washi-ink" })
            {
                var theme = ThemeCatalog.Get(id);
                foreach (var p in new[] { theme.Light, theme.Dark })
                {
                    ThemeService.ApplyPalette(resources, p, false, false, TimeSpan.Zero);
                    Assert.Equal(ThemeService.ParseColor(p.Accent), ((SolidColorBrush)resources["Brush.Accent"]).Color);
                    Assert.Equal(ThemeService.ParseColor(p.EffectiveSecondary), ((SolidColorBrush)resources["Brush.Secondary"]).Color);
                    Assert.Equal(ThemeService.ParseColor(p.EffectiveOnSecondary), ((SolidColorBrush)resources["Brush.OnSecondary"]).Color);
                }
                var swatch = new ThemeSwatch(theme);
                Assert.Equal(ThemeService.ParseColor(theme.Light.EffectiveSecondary), ((SolidColorBrush)swatch.LightSecondary).Color);
                Assert.Equal(ThemeService.ParseColor(theme.Dark.EffectiveSecondary), ((SolidColorBrush)swatch.DarkSecondary).Color);
                Assert.Equal(Visibility.Visible, swatch.SecondaryVisibility);
            }
        });
    }

    [Fact]
    public void Link_text_uses_the_secondary_and_follows_live_resource_changes_without_becoming_clickable()
    {
        RunSta(() =>
        {
            var doc = MarkdownLite.ToDocument("[Example](https://example.invalid/)");
            var paragraph = Assert.IsType<Paragraph>(Assert.Single(doc.Blocks.Cast<Block>()));
            var span = Assert.IsType<Span>(Assert.Single(paragraph.Inlines.Cast<Inline>()));
            var run = Assert.IsType<Run>(Assert.Single(span.Inlines.Cast<Inline>()));
            Assert.Equal("Example (https://example.invalid/)", run.Text);
            doc.Resources["Brush.Secondary"] = new SolidColorBrush(Colors.Cyan);
            Assert.Equal(Colors.Cyan, ((SolidColorBrush)run.Foreground).Color);
            doc.Resources["Brush.Secondary"] = new SolidColorBrush(Colors.Violet);
            Assert.Equal(Colors.Violet, ((SolidColorBrush)run.Foreground).Color);
        });
    }

    [Fact]
    public void Startup_resources_use_black_neutral_surfaces_and_the_washi_secondary_before_preview()
    {
        RunSta(() =>
        {
            _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var resources = new ResourceDictionary { Source = new Uri("/Hikyaku;component/Theme/Tokens.xaml", UriKind.Relative) };
            Assert.Equal(Colors.Black, ((SolidColorBrush)resources["Brush.Bg"]).Color);
            Assert.Equal(Color.FromRgb(0x00, 0x00, 0x00), ((SolidColorBrush)resources["Brush.Surface"]).Color);
            Assert.Equal(Color.FromRgb(0x73, 0xB6, 0xE3), ((SolidColorBrush)resources["Brush.Secondary"]).Color);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error is not null) throw error;
    }
}
