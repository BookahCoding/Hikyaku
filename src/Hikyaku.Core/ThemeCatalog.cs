namespace Hikyaku.Core;

/// <summary>
/// Hokusai-inspired theme options (approximate tokens — not measured artworks).
/// Light/dark pairs share one palette family; no unrelated bright presets.
/// </summary>
public sealed record ThemeSurfaces(
    string Bg, string Surface, string SurfaceSidebar, string SurfaceRaised, string Surface3, string Well,
    string Text, string Text2, string TextMuted, string Accent, string OnAccent, string Border,
    string Danger, string Success, string Warning, string MarkVermilion, string MarkPaper);

public sealed record ThemeDefinition(string Id, string DisplayName, string InspirationNote, ThemeSurfaces Light, ThemeSurfaces Dark);

public enum AppearanceMode { Light, Dark, System }

public static class AppearanceResolver
{
    /// <summary>Returns true when the effective palette should be light.</summary>
    public static bool Resolve(AppearanceMode mode, bool osDark) => mode switch
    {
        AppearanceMode.Light => true,
        AppearanceMode.Dark => false,
        _ => !osDark
    };
}

public static class MotionPolicy
{
    public static readonly TimeSpan DefaultPanel = TimeSpan.FromMilliseconds(180);
    public static TimeSpan PanelDuration(bool reduceMotion) => reduceMotion ? TimeSpan.Zero : DefaultPanel;
}

public enum SidebarPresentation { Inline, CollapsedRail, Overlay, Hidden }

public static class SidebarLayout
{
    public const double NarrowBreakpoint = 900;

    public static SidebarPresentation ForWidth(double width, bool collapsed)
    {
        var narrow = width < NarrowBreakpoint;
        if (narrow) return collapsed ? SidebarPresentation.Hidden : SidebarPresentation.Overlay;
        return collapsed ? SidebarPresentation.CollapsedRail : SidebarPresentation.Inline;
    }
}

public static class ThemeCatalog
{
    public static IReadOnlyList<ThemeDefinition> All { get; } =
    [
        new("washi-ink", "Washi & Ink",
            "Hokusai-inspired warm paper, sumi ink, and indigo night.",
            Light: new ThemeSurfaces(
                Bg: "#F4EDD9", Surface: "#FAF5E3", SurfaceSidebar: "#F0E8D3", SurfaceRaised: "#EFE6CF", Surface3: "#E6DCC2", Well: "#F0E8D3",
                Text: "#2B2822", Text2: "#403B31", TextMuted: "#6A5F50", Accent: "#A63F2D", OnAccent: "#FFFFFF", Border: "#140A0A1E",
                Danger: "#8A1C45", Success: "#3F6A4B", Warning: "#855608", MarkVermilion: "#A63F2D", MarkPaper: "#F4EDD9"),
            Dark: new ThemeSurfaces(
                Bg: "#0E1424", Surface: "#151C30", SurfaceSidebar: "#111829", SurfaceRaised: "#1B2440", Surface3: "#232D4D", Well: "#111829",
                Text: "#F4EDD9", Text2: "#DCD3BC", TextMuted: "#A8A08C", Accent: "#E5927E", OnAccent: "#0A0A0C", Border: "#33F4EDD9",
                Danger: "#F4739B", Success: "#7DBE8A", Warning: "#E3B458", MarkVermilion: "#A63F2D", MarkPaper: "#F4EDD9")),

        new("great-wave", "Great Wave",
            "Hokusai-inspired Prussian blue, pale sky, and cream foam.",
            Light: new ThemeSurfaces(
                Bg: "#E8F0F4", Surface: "#F5FAFC", SurfaceSidebar: "#DDE8EF", SurfaceRaised: "#D0E0EA", Surface3: "#C2D4E0", Well: "#E2ECF2",
                Text: "#0F2438", Text2: "#243B52", TextMuted: "#4A657A", Accent: "#1B4F72", OnAccent: "#FFFFFF", Border: "#1A0F2438",
                Danger: "#8B1E3F", Success: "#2F6B4F", Warning: "#8A6A12", MarkVermilion: "#C45C3A", MarkPaper: "#F5FAFC"),
            Dark: new ThemeSurfaces(
                Bg: "#07131F", Surface: "#0C1C2C", SurfaceSidebar: "#091624", SurfaceRaised: "#12263A", Surface3: "#1A3248", Well: "#0A1828",
                Text: "#E8F0F4", Text2: "#C5D6E4", TextMuted: "#7F9BB0", Accent: "#5BA4D9", OnAccent: "#07131F", Border: "#33E8F0F4",
                Danger: "#F080A0", Success: "#6FBF95", Warning: "#E0C05A", MarkVermilion: "#E08A6A", MarkPaper: "#E8F0F4")),

        new("red-fuji", "Red Fuji",
            "Hokusai-inspired rust peak, cream mist, and deep blue sky.",
            Light: new ThemeSurfaces(
                Bg: "#F7EFE6", Surface: "#FFF8F0", SurfaceSidebar: "#F0E4D6", SurfaceRaised: "#E8D8C8", Surface3: "#DCC8B4", Well: "#F3E8DC",
                Text: "#2A1810", Text2: "#4A3024", TextMuted: "#7A5A48", Accent: "#A84828", OnAccent: "#FFFFFF", Border: "#182A1810",
                Danger: "#8A2030", Success: "#3E6848", Warning: "#8C6410", MarkVermilion: "#A84828", MarkPaper: "#FFF8F0"),
            Dark: new ThemeSurfaces(
                Bg: "#120E1C", Surface: "#1A1528", SurfaceSidebar: "#14101F", SurfaceRaised: "#241C34", Surface3: "#2E2444", Well: "#161222",
                Text: "#F7EFE6", Text2: "#E0D0C0", TextMuted: "#A89080", Accent: "#E09070", OnAccent: "#120E1C", Border: "#33F7EFE6",
                Danger: "#F07088", Success: "#78B888", Warning: "#E0B858", MarkVermilion: "#C85838", MarkPaper: "#F7EFE6")),

        new("pine-mist", "Pine & Mist",
            "Hokusai-inspired moss green, slate mist, and paper.",
            Light: new ThemeSurfaces(
                Bg: "#EEF2EC", Surface: "#F7FAF5", SurfaceSidebar: "#E4EBE0", SurfaceRaised: "#D8E2D4", Surface3: "#C8D4C4", Well: "#E8EEE4",
                Text: "#1A2420", Text2: "#2E3C36", TextMuted: "#5A6A62", Accent: "#3F6B4E", OnAccent: "#FFFFFF", Border: "#181A2420",
                Danger: "#8A2A3A", Success: "#2F6848", Warning: "#7A6810", MarkVermilion: "#8B4A32", MarkPaper: "#F7FAF5"),
            Dark: new ThemeSurfaces(
                Bg: "#0C1410", Surface: "#121A16", SurfaceSidebar: "#0E1612", SurfaceRaised: "#1A2420", Surface3: "#24302A", Well: "#101814",
                Text: "#EEF2EC", Text2: "#D0D8D0", TextMuted: "#889488", Accent: "#7AAA88", OnAccent: "#0C1410", Border: "#33EEF2EC",
                Danger: "#E08090", Success: "#80C098", Warning: "#D0C060", MarkVermilion: "#C07050", MarkPaper: "#EEF2EC")),

        // From Hokusai's "Large Flowers" series (c. 1833): "Chrysanthemums and Horsefly", golden blooms on a pale ground.
        new("chrysanthemum", "Chrysanthemums",
            "Hokusai-inspired golden chrysanthemums, leaf green, and pale paper (Large Flowers series).",
            Light: new ThemeSurfaces(
                Bg: "#F6F1E4", Surface: "#FCF8EE", SurfaceSidebar: "#F0E9D6", SurfaceRaised: "#ECE3CB", Surface3: "#E2D6B8", Well: "#F2EBDA",
                Text: "#26201A", Text2: "#3E3528", TextMuted: "#6B5C42", Accent: "#8C5A0B", OnAccent: "#FFFFFF", Border: "#1426201A",
                Danger: "#8E2A1E", Success: "#3D6A3A", Warning: "#7E5A06", MarkVermilion: "#B4462A", MarkPaper: "#FCF8EE"),
            Dark: new ThemeSurfaces(
                Bg: "#14130E", Surface: "#1C1A13", SurfaceSidebar: "#17150F", SurfaceRaised: "#26231A", Surface3: "#312D21", Well: "#18160F",
                Text: "#F4ECD8", Text2: "#DCD0B4", TextMuted: "#A9997A", Accent: "#E0B04A", OnAccent: "#14130E", Border: "#33F4ECD8",
                Danger: "#F08A6E", Success: "#8CC08A", Warning: "#E8C060", MarkVermilion: "#D46A44", MarkPaper: "#F4ECD8")),

        // From the same series: "Peonies and Butterfly", rose-pink peonies and deep leaves bent by the wind.
        new("peony", "Peonies",
            "Hokusai-inspired rose peonies, deep leaves, and soft blush paper (Large Flowers series).",
            Light: new ThemeSurfaces(
                Bg: "#F7F0EF", Surface: "#FDF8F7", SurfaceSidebar: "#F1E6E5", SurfaceRaised: "#EBDCDB", Surface3: "#E0CDCC", Well: "#F3EAE9",
                Text: "#2A1D22", Text2: "#43303A", TextMuted: "#765A64", Accent: "#9C2E58", OnAccent: "#FFFFFF", Border: "#142A1D22",
                Danger: "#8E1F2E", Success: "#2F6A4A", Warning: "#84600E", MarkVermilion: "#B23A48", MarkPaper: "#FDF8F7"),
            Dark: new ThemeSurfaces(
                Bg: "#160F14", Surface: "#1F161C", SurfaceSidebar: "#191117", SurfaceRaised: "#2A1E26", Surface3: "#362631", Well: "#1A1218",
                Text: "#F7EEF1", Text2: "#E2D0D7", TextMuted: "#B097A2", Accent: "#E58AAE", OnAccent: "#160F14", Border: "#33F7EEF1",
                Danger: "#F4848F", Success: "#7FC09A", Warning: "#E2BC62", MarkVermilion: "#E07082", MarkPaper: "#F7EEF1")),
    ];

    public static ThemeDefinition Get(string id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? All[0];
}
