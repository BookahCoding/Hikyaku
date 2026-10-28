using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 23 — keyboard shortcuts copied from Hermes Desktop's documented set.</summary>
public class Checkpoint23ShortcutTests
{
    [Fact]
    public void No_two_entries_share_a_key_combination()
    {
        var combos = Shortcuts.All.Select(s => (s.Key, s.Shift)).ToList();
        Assert.Equal(combos.Count, combos.Distinct().Count());
    }

    [Theory]
    [InlineData("N", false, ShortcutAction.NewChat)]
    [InlineData("K", false, ShortcutAction.Palette)]
    [InlineData("P", false, ShortcutAction.Palette)]
    [InlineData("OemComma", false, ShortcutAction.Settings)]
    [InlineData("F", true, ShortcutAction.SearchSessions)]
    [InlineData("F", false, ShortcutAction.FindInChat)]
    [InlineData("B", false, ShortcutAction.ToggleSidebar)]
    [InlineData("Oem3", false, ShortcutAction.ToggleTerminal)]
    [InlineData("Oem2", false, ShortcutAction.ShowShortcuts)]
    public void Desktops_keys_map_to_their_actions(string key, bool shift, ShortcutAction expected)
        => Assert.Equal(expected, Shortcuts.Match(key, ctrl: true, shift, alt: false));

    [Theory]
    [InlineData("N", false, false, false)]   // plain typing is never a shortcut
    [InlineData("N", true, false, true)]     // Alt is not part of any shortcut
    [InlineData("B", true, true, false)]     // Ctrl+Shift+B is not Ctrl+B
    [InlineData("N", true, true, false)]
    [InlineData("Z", true, false, false)]
    public void Anything_else_is_ignored(string key, bool ctrl, bool shift, bool alt)
        => Assert.Null(Shortcuts.Match(key, ctrl, shift, alt));

    [Fact]
    public void Every_action_has_a_label_and_the_palette_uses_the_first_key_listed()
    {
        foreach (var a in Enum.GetValues<ShortcutAction>())
            Assert.False(string.IsNullOrWhiteSpace(Shortcuts.DisplayFor(a)));
        Assert.Equal("Ctrl+K", Shortcuts.DisplayFor(ShortcutAction.Palette));
        Assert.Contains("Ctrl+Shift+F", Shortcuts.HelpText());
    }
}
