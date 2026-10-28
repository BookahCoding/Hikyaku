using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>One theme in the Appearance gallery: its light and dark paper, ink and accent, side by side.</summary>
public sealed class ThemeSwatch(ThemeDefinition theme)
{
    public string Id => theme.Id;
    public string Name => theme.DisplayName;
    public Brush LightBackground { get; } = Solid(theme.Light.Bg);
    public Brush LightText { get; } = Solid(theme.Light.Text);
    public Brush LightAccent { get; } = Solid(theme.Light.Accent);
    public Brush DarkBackground { get; } = Solid(theme.Dark.Bg);
    public Brush DarkText { get; } = Solid(theme.Dark.Text);
    public Brush DarkAccent { get; } = Solid(theme.Dark.Accent);

    private static Brush Solid(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// 0.5.2 — Settings as sections on the left and one page each (Claude Desktop's layout; the maintainer's request). A section
/// whose content the view hides (<see cref="SettingsSections"/>) is hidden from the list too.
/// </summary>
public partial class SettingsWindow
{
    private (string Id, ListBoxItem Nav, FrameworkElement Page)[] Sections =>
    [
        ("general", NavGeneral, PageGeneral), ("appearance", NavAppearance, PageAppearance), ("account", NavAccount, PageAccount),
        ("profiles", NavProfiles, PageProfiles), ("safety", NavSafety, PageSafety), ("connection", NavConnection, PageConnection),
        ("updates", NavUpdates, PageUpdates)
    ];

    /// <summary>The page on show ("general", "appearance", "account", "profiles", "safety", "connection" or "updates").</summary>
    internal string CurrentPage { get; private set; } = "general";

    internal void ShowPage(string id)
    {
        if (Sections.FirstOrDefault(s => s.Id == id) is not { Nav: not null } section || section.Nav.Visibility != Visibility.Visible) return;
        CurrentPage = id;
        foreach (var s in Sections) s.Page.Visibility = s.Id == id ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(SectionNav.SelectedItem, section.Nav)) SectionNav.SelectedItem = section.Nav;
        PageScroller.ScrollToTop();
    }

    private void SectionNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionNav.SelectedItem is ListBoxItem { Tag: string id } && id != CurrentPage) ShowPage(id);
    }

    /// <summary>Hides the sections the view has nothing to show in; if the page on show went, falls back to the first one.</summary>
    private void ApplySectionList(SettingsSectionPlan plan)
    {
        Visibility V(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
        NavProfiles.Visibility = V(plan.Profiles);
        NavSafety.Visibility = V(plan.Approvals);
        NavConnection.Visibility = V(plan.Access);
        NavUpdates.Visibility = V(plan.Updates);
        if (Sections.First(s => s.Id == CurrentPage).Nav.Visibility != Visibility.Visible) ShowPage("general");
        else ShowPage(CurrentPage);
    }
}
