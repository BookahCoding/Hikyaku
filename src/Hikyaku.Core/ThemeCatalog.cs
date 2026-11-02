namespace Hikyaku.Core;

/// <summary>
/// Hokusai-inspired and Tokyo-neon theme options (approximate tokens — not measured artworks).
/// Light/dark pairs use neutral paper/black surfaces with two accents from each named inspiration.
/// </summary>
public sealed record ThemeSurfaces(
    string Bg, string Surface, string SurfaceSidebar, string SurfaceRaised, string Surface3, string Well,
    string Text, string Text2, string TextMuted, string Accent, string OnAccent, string Border,
    string Danger, string Success, string Warning, string MarkVermilion, string MarkPaper,
    string? Secondary = null, string? OnSecondary = null)
{
    public string EffectiveSecondary => Secondary ?? Accent;
    public string EffectiveOnSecondary => OnSecondary ?? OnAccent;
}

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
                Bg: "#F7F7F5", Surface: "#FAFAF8", SurfaceSidebar: "#F0F0EE", SurfaceRaised: "#F3F3F1", Surface3: "#EDEDEB", Well: "#F0F0EE",
                Text: "#2B2822", Text2: "#403B31", TextMuted: "#6A5F50", Accent: "#A63F2D", OnAccent: "#FFFFFF", Border: "#140A0A1E",
                Danger: "#8A1C45", Success: "#3F6A4B", Warning: "#855608", MarkVermilion: "#A63F2D", MarkPaper: "#F4EDD9",
                Secondary: "#245A86", OnSecondary: "#FFFFFF"),
            Dark: new ThemeSurfaces(
                Bg: "#000000", Surface: "#000000", SurfaceSidebar: "#080808", SurfaceRaised: "#171717", Surface3: "#202020", Well: "#0C0C0C",
                Text: "#F4EDD9", Text2: "#DCD3BC", TextMuted: "#A8A08C", Accent: "#E5927E", OnAccent: "#0A0A0C", Border: "#33F4EDD9",
                Danger: "#F4739B", Success: "#7DBE8A", Warning: "#E3B458", MarkVermilion: "#A63F2D", MarkPaper: "#F4EDD9",
                Secondary: "#73B6E3", OnSecondary: "#080808")),

        new("great-wave", "Great Wave",
            "Hokusai-inspired Prussian blue, pale sky, and cream foam.",
            Light: new ThemeSurfaces(
                Bg: "#F7F7F5", Surface: "#FAFAF8", SurfaceSidebar: "#F0F0EE", SurfaceRaised: "#F3F3F1", Surface3: "#EDEDEB", Well: "#F0F0EE",
                Text: "#0F2438", Text2: "#243B52", TextMuted: "#4A657A", Accent: "#1B4F72", OnAccent: "#FFFFFF", Border: "#1A0F2438",
                Danger: "#8B1E3F", Success: "#2F6B4F", Warning: "#80600C", MarkVermilion: "#C45C3A", MarkPaper: "#F5FAFC",
                Secondary: "#9B4D25", OnSecondary: "#FFFFFF"),
            Dark: new ThemeSurfaces(
                Bg: "#000000", Surface: "#000000", SurfaceSidebar: "#080808", SurfaceRaised: "#171717", Surface3: "#202020", Well: "#0C0C0C",
                Text: "#E8F0F4", Text2: "#C5D6E4", TextMuted: "#7F9BB0", Accent: "#5BA4D9", OnAccent: "#07131F", Border: "#33E8F0F4",
                Danger: "#F080A0", Success: "#6FBF95", Warning: "#E0C05A", MarkVermilion: "#E08A6A", MarkPaper: "#E8F0F4",
                Secondary: "#E9AA72", OnSecondary: "#080808")),

        new("red-fuji", "Red Fuji",
            "Hokusai-inspired rust peak, cream mist, and deep blue sky.",
            Light: new ThemeSurfaces(
                Bg: "#F7F7F5", Surface: "#FAFAF8", SurfaceSidebar: "#F0F0EE", SurfaceRaised: "#F3F3F1", Surface3: "#EDEDEB", Well: "#F0F0EE",
                Text: "#2A1810", Text2: "#4A3024", TextMuted: "#7A5A48", Accent: "#A84828", OnAccent: "#FFFFFF", Border: "#182A1810",
                Danger: "#8A2030", Success: "#3E6848", Warning: "#8C6410", MarkVermilion: "#A84828", MarkPaper: "#FFF8F0",
                Secondary: "#285A83", OnSecondary: "#FFFFFF"),
            Dark: new ThemeSurfaces(
                Bg: "#000000", Surface: "#000000", SurfaceSidebar: "#080808", SurfaceRaised: "#171717", Surface3: "#202020", Well: "#0C0C0C",
                Text: "#F7EFE6", Text2: "#E0D0C0", TextMuted: "#A89080", Accent: "#E09070", OnAccent: "#120E1C", Border: "#33F7EFE6",
                Danger: "#F07088", Success: "#78B888", Warning: "#E0B858", MarkVermilion: "#C85838", MarkPaper: "#F7EFE6",
                Secondary: "#83B8E6", OnSecondary: "#080808")),

        new("pine-mist", "Pine & Mist",
            "Hokusai-inspired moss green, slate mist, and paper.",
            Light: new ThemeSurfaces(
                Bg: "#F7F7F5", Surface: "#FAFAF8", SurfaceSidebar: "#F0F0EE", SurfaceRaised: "#F3F3F1", Surface3: "#EDEDEB", Well: "#F0F0EE",
                Text: "#1A2420", Text2: "#2E3C36", TextMuted: "#5A6A62", Accent: "#3F6B4E", OnAccent: "#FFFFFF", Border: "#181A2420",
                Danger: "#8A2A3A", Success: "#2F6848", Warning: "#7A6810", MarkVermilion: "#8B4A32", MarkPaper: "#F7FAF5",
                Secondary: "#79447F", OnSecondary: "#FFFFFF"),
            Dark: new ThemeSurfaces(
                Bg: "#000000", Surface: "#000000", SurfaceSidebar: "#080808", SurfaceRaised: "#171717", Surface3: "#202020", Well: "#0C0C0C",
                Text: "#EEF2EC", Text2: "#D0D8D0", TextMuted: "#889488", Accent: "#7AAA88", OnAccent: "#0C1410", Border: "#33EEF2EC",
                Danger: "#E08090", Success: "#80C098", Warning: "#D0C060", MarkVermilion: "#C07050", MarkPaper: "#EEF2EC",
                Secondary: "#CB9FDF", OnSecondary: "#080808")),

        // From Hokusai's "Large Flowers" series (c. 1833): "Chrysanthemums and Horsefly", golden blooms on a pale ground.
        new("chrysanthemum", "Chrysanthemums",
            "Hokusai-inspired golden chrysanthemums, leaf green, and pale paper (Large Flowers series).",
            Light: new ThemeSurfaces(
                Bg: "#F7F7F5", Surface: "#FAFAF8", SurfaceSidebar: "#F0F0EE", SurfaceRaised: "#F3F3F1", Surface3: "#EDEDEB", Well: "#F0F0EE",
                Text: "#26201A", Text2: "#3E3528", TextMuted: "#6B5C42", Accent: "#8C5A0B", OnAccent: "#FFFFFF", Border: "#1426201A",
                Danger: "#8E2A1E", Success: "#3D6A3A", Warning: "#7E5A06", MarkVermilion: "#B4462A", MarkPaper: "#FCF8EE",
                Secondary: "#326341", OnSecondary: "#FFFFFF"),
            Dark: new ThemeSurfaces(
                Bg: "#000000", Surface: "#000000", SurfaceSidebar: "#080808", SurfaceRaised: "#171717", Surface3: "#202020", Well: "#0C0C0C",
                Text: "#F4ECD8", Text2: "#DCD0B4", TextMuted: "#A9997A", Accent: "#E0B04A", OnAccent: "#14130E", Border: "#33F4ECD8",
                Danger: "#F08A6E", Success: "#8CC08A", Warning: "#E8C060", MarkVermilion: "#D46A44", MarkPaper: "#F4ECD8",
                Secondary: "#8EC7A4", OnSecondary: "#080808")),

        // From the same series: "Peonies and Butterfly", rose-pink peonies and deep leaves bent by the wind.
        new("peony", "Peonies",
            "Hokusai-inspired rose peonies, deep leaves, and soft blush paper (Large Flowers series).",
            Light: new ThemeSurfaces(
                Bg: "#F7F7F5", Surface: "#FAFAF8", SurfaceSidebar: "#F0F0EE", SurfaceRaised: "#F3F3F1", Surface3: "#EDEDEB", Well: "#F0F0EE",
                Text: "#2A1D22", Text2: "#43303A", TextMuted: "#765A64", Accent: "#9C2E58", OnAccent: "#FFFFFF", Border: "#142A1D22",
                Danger: "#8E1F2E", Success: "#2F6A4A", Warning: "#84600E", MarkVermilion: "#B23A48", MarkPaper: "#FDF8F7",
                Secondary: "#246A59", OnSecondary: "#FFFFFF"),
            Dark: new ThemeSurfaces(
                Bg: "#000000", Surface: "#000000", SurfaceSidebar: "#080808", SurfaceRaised: "#171717", Surface3: "#202020", Well: "#0C0C0C",
                Text: "#F7EEF1", Text2: "#E2D0D7", TextMuted: "#B097A2", Accent: "#E58AAE", OnAccent: "#160F14", Border: "#33F7EEF1",
                Danger: "#F4848F", Success: "#7FC09A", Warning: "#E2BC62", MarkVermilion: "#E07082", MarkPaper: "#F7EEF1",
                Secondary: "#7BD0BB", OnSecondary: "#080808")),

        // "Kirifuri Waterfall at Kurokami Mountain in Shimotsuke" (A Tour of the Waterfalls, c. 1832):
        // a white cascade on saturated teal water, not the pale sky of Great Wave or the sage paper of Pine & Mist.
        new("kirifuri", "Kirifuri Falls",
            "Hokusai-inspired foam white, deep waterfall teal, and moss (A Tour of the Waterfalls: Kirifuri Waterfall at Kurokami Mountain).",
            Light: new ThemeSurfaces(
                Bg: "#F7F7F5", Surface: "#FAFAF8", SurfaceSidebar: "#F0F0EE", SurfaceRaised: "#F3F3F1", Surface3: "#EDEDEB", Well: "#F0F0EE",
                Text: "#06211E", Text2: "#143632", TextMuted: "#2A4E48", Accent: "#084F48", OnAccent: "#FFFFFF", Border: "#1A06211E",
                Danger: "#7A1C2C", Success: "#0E5A34", Warning: "#6A4A06", MarkVermilion: "#B8432A", MarkPaper: "#E7F6F2",
                Secondary: "#8B412C", OnSecondary: "#FFFFFF"),
            Dark: new ThemeSurfaces(
                Bg: "#000000", Surface: "#000000", SurfaceSidebar: "#080808", SurfaceRaised: "#171717", Surface3: "#202020", Well: "#0C0C0C",
                Text: "#F3FFFC", Text2: "#D4F3EC", TextMuted: "#A8DDD2", Accent: "#D8FFF6", OnAccent: "#0C3C38", Border: "#33F3FFFC",
                Danger: "#FFC8D0", Success: "#9EE8C0", Warning: "#FFE08A", MarkVermilion: "#F09A78", MarkPaper: "#F3FFFC",
                Secondary: "#F4AA7D", OnSecondary: "#080808")),

        // "Thunderstorm Beneath the Summit" (Thirty-six Views of Mount Fuji): clear blue sky above a black storm, lightning in pale gold.
        // The sky is a real blue, not Great Wave's pale wash, and the bolt is not Chrysanthemums' flower gold.
        new("thunderstorm", "Thunderstorm",
            "Hokusai-inspired clear sky, storm indigo, and lightning (Thirty-six Views: Thunderstorm Beneath the Summit).",
            Light: new ThemeSurfaces(
                Bg: "#F7F7F5", Surface: "#FAFAF8", SurfaceSidebar: "#F0F0EE", SurfaceRaised: "#F3F3F1", Surface3: "#EDEDEB", Well: "#F0F0EE",
                Text: "#10182C", Text2: "#1E3050", TextMuted: "#2C4068", Accent: "#1C2748", OnAccent: "#FFFFFF", Border: "#1A10182C",
                Danger: "#7A1428", Success: "#0C5834", Warning: "#5E4406", MarkVermilion: "#8E3A28", MarkPaper: "#E6EEFC",
                Secondary: "#76530A", OnSecondary: "#FFFFFF"),
            Dark: new ThemeSurfaces(
                Bg: "#000000", Surface: "#000000", SurfaceSidebar: "#080808", SurfaceRaised: "#171717", Surface3: "#202020", Well: "#0C0C0C",
                Text: "#F7F9FF", Text2: "#D5DBF0", TextMuted: "#B4BCDC", Accent: "#FFF3C4", OnAccent: "#070A14", Border: "#33F7F9FF",
                Danger: "#FFB4BE", Success: "#A4E8C4", Warning: "#FFE08A", MarkVermilion: "#E89878", MarkPaper: "#F7F9FF",
                Secondary: "#83BAFF", OnSecondary: "#080808")),

        // "Irises and grasshopper" (c. 1833–34), Fitzwilliam Museum P.191-1946.
        // An interpretive violet/lavender family: original UI tokens, not sampled colours or a reproduced image.
        new("irises", "Irises",
            "Hokusai-inspired iris violet, lavender paper, and aubergine ink (Irises and grasshopper).",
            Light: new ThemeSurfaces(
                Bg: "#F7F7F5", Surface: "#FAFAF8", SurfaceSidebar: "#F0F0EE", SurfaceRaised: "#F3F3F1", Surface3: "#EDEDEB", Well: "#F0F0EE",
                Text: "#24152F", Text2: "#382547", TextMuted: "#51405E", Accent: "#5D347F", OnAccent: "#FFFFFF", Border: "#2024152F",
                Danger: "#7C203C", Success: "#245133", Warning: "#624506", MarkVermilion: "#A64632", MarkPaper: "#F5EFFA",
                Secondary: "#38613E", OnSecondary: "#FFFFFF"),
            Dark: new ThemeSurfaces(
                Bg: "#000000", Surface: "#000000", SurfaceSidebar: "#080808", SurfaceRaised: "#171717", Surface3: "#202020", Well: "#0C0C0C",
                Text: "#F6EFFB", Text2: "#E5D6EF", TextMuted: "#C4ACD4", Accent: "#C0A0F0", OnAccent: "#21142C", Border: "#40F6EFFB",
                Danger: "#FFAFC3", Success: "#A5D9B0", Warning: "#EFD58D", MarkVermilion: "#EA9A7F", MarkPaper: "#F6EFFB",
                Secondary: "#99D2A8", OnSecondary: "#080808")),
        // Ported from Herald Themes.kt @ 5227112f (MIT): crossing signs on violet night, magenta + cyan.
        new("shibuya-night", "Shibuya Night",
            "Tokyo crossing after dark: magenta signs, cyan light, and near-black violet (Herald Shibuya Night).",
            Light: new ThemeSurfaces(
                Bg: "#F7F7F5", Surface: "#FAFAF8", SurfaceSidebar: "#F0F0EE", SurfaceRaised: "#F3F3F1", Surface3: "#EDEDEB", Well: "#F0F0EE",
                Text: "#1B1233", Text2: "#2C2250", TextMuted: "#5A4E7C", Accent: "#A9136A", OnAccent: "#FFFFFF", Border: "#201B1233",
                Danger: "#B3202B", Success: "#1F6B55", Warning: "#855608", MarkVermilion: "#A9136A", MarkPaper: "#FCF9FF",
                Secondary: "#00677B", OnSecondary: "#FFFFFF"),
            Dark: new ThemeSurfaces(
                Bg: "#000000", Surface: "#000000", SurfaceSidebar: "#080808", SurfaceRaised: "#171717", Surface3: "#202020", Well: "#0C0C0C",
                Text: "#F3EEFF", Text2: "#DAD2F0", TextMuted: "#B1A6D1", Accent: "#FF7CC0", OnAccent: "#0A0A0C", Border: "#33F3EEFF",
                Danger: "#FF6B6B", Success: "#3EE0B0", Warning: "#FFC857", MarkVermilion: "#FF7CC0", MarkPaper: "#F3EEFF",
                Secondary: "#22E3FF", OnSecondary: "#0A0A0C")),

        // Herald's electric-town pair: sign green + violet against black-blue.
        new("akihabara", "Akihabara",
            "Tokyo electric town: green neon, violet signs, and black-blue streets (Herald Akihabara).",
            Light: new ThemeSurfaces(
                Bg: "#F7F7F5", Surface: "#FAFAF8", SurfaceSidebar: "#F0F0EE", SurfaceRaised: "#F3F3F1", Surface3: "#EDEDEB", Well: "#F0F0EE",
                Text: "#0E1A24", Text2: "#1E2D3A", TextMuted: "#48596A", Accent: "#06703A", OnAccent: "#FFFFFF", Border: "#200E1A24",
                Danger: "#B3202B", Success: "#0B6B3A", Warning: "#855608", MarkVermilion: "#06703A", MarkPaper: "#F8FAFC",
                Secondary: "#5A2DD0", OnSecondary: "#FFFFFF"),
            Dark: new ThemeSurfaces(
                Bg: "#000000", Surface: "#000000", SurfaceSidebar: "#080808", SurfaceRaised: "#171717", Surface3: "#202020", Well: "#0C0C0C",
                Text: "#E9F4F1", Text2: "#CFE0DC", TextMuted: "#9FB4B0", Accent: "#2BE37A", OnAccent: "#0A0A0C", Border: "#33E9F4F1",
                Danger: "#FF6B7A", Success: "#2BE37A", Warning: "#FFC857", MarkVermilion: "#2BE37A", MarkPaper: "#E9F4F1",
                Secondary: "#A080FF", OnSecondary: "#0A0A0C")),
    ];

    public static ThemeDefinition Get(string id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? All[0];
}
