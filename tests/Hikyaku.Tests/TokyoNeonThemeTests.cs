using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

public class TokyoNeonThemeTests
{
    [Theory]
    [InlineData("shibuya-night", "#FF7CC0", "#22E3FF")]
    [InlineData("akihabara", "#2BE37A", "#A080FF")]
    public void Neon_themes_have_two_distinct_accents_that_read_on_every_surface(string id, string accent, string secondary)
    {
        var theme = ThemeCatalog.Get(id);
        Assert.Equal(id, theme.Id);
        Assert.Equal(accent, theme.Dark.Accent);
        Assert.Equal(secondary, theme.Dark.EffectiveSecondary);
        foreach (var p in new[] { theme.Light, theme.Dark })
        {
            Assert.NotNull(p.Secondary);
            Assert.True(Distance(p.Accent, p.EffectiveSecondary) > 100);
            foreach (var ground in new[] { p.Bg, p.Surface, p.SurfaceSidebar, p.SurfaceRaised, p.Surface3, p.Well })
            {
                foreach (var ink in new[] { p.Text, p.Text2, p.TextMuted, p.Accent, p.EffectiveSecondary, p.Danger, p.Success, p.Warning })
                    Assert.True(Contrast(ink, ground) >= 4.5, id + ": " + ink + " on " + ground);
            }
            Assert.True(Contrast(p.OnAccent, p.Accent) >= 4.5);
            Assert.True(Contrast(p.EffectiveOnSecondary, p.EffectiveSecondary) >= 4.5);
        }
    }

    [Fact]
    public void Existing_themes_keep_their_original_accent_as_the_secondary_fallback()
    {
        foreach (var theme in ThemeCatalog.All.Where(t => t.Id is not "shibuya-night" and not "akihabara"))
            foreach (var p in new[] { theme.Light, theme.Dark })
            {
                Assert.Null(p.Secondary);
                Assert.Equal(p.Accent, p.EffectiveSecondary);
                Assert.Equal(p.OnAccent, p.EffectiveOnSecondary);
            }
    }

    [Fact]
    public void Neon_families_have_different_accent_pairs()
    {
        var shibuya = ThemeCatalog.Get("shibuya-night");
        var akihabara = ThemeCatalog.Get("akihabara");
        Assert.True(Distance(shibuya.Dark.Accent, akihabara.Dark.Accent) > 200);
        Assert.True(Distance(shibuya.Dark.EffectiveSecondary, akihabara.Dark.EffectiveSecondary) > 120);
    }

    private static int[] Rgb(string hex) => new[] { 1, 3, 5 }.Select(i => Convert.ToInt32(hex.Substring(i, 2), 16)).ToArray();
    private static double Distance(string a, string b) => Math.Sqrt(Rgb(a).Zip(Rgb(b), (x, y) => (x - y) * (x - y)).Sum());
    private static double Contrast(string a, string b)
    {
        static double Luminance(string hex)
        {
            var c = Rgb(hex).Select(v => v / 255.0).Select(v => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4)).ToArray();
            return c[0] * 0.2126 + c[1] * 0.7152 + c[2] * 0.0722;
        }
        var x = Luminance(a); var y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}
