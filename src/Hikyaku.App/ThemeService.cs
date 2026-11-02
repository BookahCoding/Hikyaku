using System.Windows;
using System.Windows.Media;
using Hikyaku.Core;
using Microsoft.Win32;

namespace Hikyaku.App;

/// <summary>Applies a theme + light/dark/system into live brushes; respects reduced motion / high contrast.</summary>
public static class ThemeService
{
    public static string ThemeId { get; private set; } = "washi-ink";
    public static AppearanceMode Appearance { get; private set; } = AppearanceMode.System;
    public static bool IsLight { get; private set; }
    public static bool ReduceMotion { get; private set; }
    public static bool HighContrast { get; private set; }
    public static TimeSpan PanelDuration => MotionPolicy.PanelDuration(ReduceMotion);

    public static void Apply(string themeId, AppearanceMode appearance)
    {
        ThemeId = ThemeCatalog.Get(themeId).Id;
        Appearance = appearance;
        ReduceMotion = DetectReduceMotion();
        HighContrast = SystemParameters.HighContrast;
        IsLight = AppearanceResolver.Resolve(appearance, OsPrefersDark());
        var surfaces = IsLight ? ThemeCatalog.Get(ThemeId).Light : ThemeCatalog.Get(ThemeId).Dark;
        ApplySurfaces(surfaces);
        MathTypesetter.ClearCache();
    }

    /// <summary>Legacy light toggle — maps to Light/Dark while keeping current theme family.</summary>
    public static void Apply(bool light) =>
        Apply(ThemeId, light ? AppearanceMode.Light : AppearanceMode.Dark);

    public static bool OsPrefersDark()
    {
        try
        {
            var v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return v is int i && i == 0;
        }
        catch { return false; }
    }

    public static bool DetectReduceMotion()
    {
        try
        {
            if (!SystemParameters.ClientAreaAnimation) return true;
            var v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Control Panel\Accessibility",
                "MessageDuration", null);
            // Prefer OS animation flag; registry is a secondary hint only.
            return !SystemParameters.ClientAreaAnimation;
        }
        catch { return !SystemParameters.ClientAreaAnimation; }
    }

    private static void ApplySurfaces(ThemeSurfaces s)
    {
        var app = Application.Current;
        if (app?.Resources is null) return;
        ApplyPalette(app.Resources, s, HighContrast, IsLight, PanelDuration);
    }

    internal static void ApplyPalette(ResourceDictionary resources, ThemeSurfaces s, bool highContrast, bool isLight, TimeSpan panelDuration)
    {
        void Set(string key, string hex)
        {
            var color = ParseColor(hex);
            if (highContrast && (key is "Brush.Border" or "Brush.Text" or "Brush.Accent"))
            {
                // Strengthen borders / keep text crisp under OS high contrast without inventing a fifth theme.
                if (key == "Brush.Border")
                    color = isLight ? Color.FromRgb(0x10, 0x10, 0x10) : Color.FromRgb(0xF0, 0xF0, 0xF0);
            }
            resources[key] = new SolidColorBrush(color);
        }

        Set("Brush.Bg", s.Bg);
        Set("Brush.Surface", s.Surface);
        Set("Brush.SurfaceSidebar", s.SurfaceSidebar);
        Set("Brush.SurfaceRaised", s.SurfaceRaised);
        Set("Brush.Surface3", s.Surface3);
        Set("Brush.Well", s.Well);
        Set("Brush.Text", s.Text);
        Set("Brush.Text2", s.Text2);
        Set("Brush.TextMuted", s.TextMuted);
        Set("Brush.Accent", s.Accent);
        Set("Brush.OnAccent", s.OnAccent);
        Set("Brush.Secondary", s.EffectiveSecondary);
        Set("Brush.OnSecondary", s.EffectiveOnSecondary);
        Set("Brush.Border", s.Border);
        Set("Brush.Danger", s.Danger);
        Set("Brush.Success", s.Success);
        Set("Brush.Warning", s.Warning);
        Set("Brush.MarkVermilion", s.MarkVermilion);
        Set("Brush.MarkPaper", s.MarkPaper);
        resources["Duration.Panel"] = panelDuration;
    }

    public static Color ParseColor(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6)
            return Color.FromRgb(
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16));
        if (hex.Length == 8)
            return Color.FromArgb(
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16),
                Convert.ToByte(hex[6..8], 16));
        return Colors.Gray;
    }
}
