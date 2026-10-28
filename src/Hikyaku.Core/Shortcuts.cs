namespace Hikyaku.Core;

public enum ShortcutAction
{
    NewChat,
    Palette,
    Settings,
    SearchSessions,
    ToggleSidebar,
    FindInChat,
    ToggleTerminal,
    ShowShortcuts
}

/// <summary>One key combination. <see cref="Key"/> is the WPF <c>Key</c> name (e.g. "N", "OemComma", "Oem2"); Alt is never part of one.</summary>
public sealed record Shortcut(ShortcutAction Action, string Key, bool Shift, string Display, string Label);

/// <summary>
/// The app's keyboard shortcuts, following Hermes Desktop's documented set (Ctrl+N new session, Ctrl+K / Ctrl+P command palette,
/// Ctrl+, Settings, Ctrl+Shift+F search sessions, Ctrl+B sidebar, Ctrl+F find in chat, Ctrl+` terminal, Ctrl+/ shortcut list).
/// All use Ctrl. They only navigate or change the view: none sends, deletes, grants or publishes anything. One table drives the
/// window and the palette hints, so what is listed is what works, and matching is exact (Ctrl+Shift+B is not Ctrl+B).
/// </summary>
public static class Shortcuts
{
    public static IReadOnlyList<Shortcut> All { get; } =
    [
        new(ShortcutAction.NewChat, "N", false, "Ctrl+N", "New chat"),
        new(ShortcutAction.Palette, "K", false, "Ctrl+K", "Command palette"),
        new(ShortcutAction.Palette, "P", false, "Ctrl+P", "Command palette"),
        new(ShortcutAction.Settings, "OemComma", false, "Ctrl+,", "Settings"),
        new(ShortcutAction.SearchSessions, "F", true, "Ctrl+Shift+F", "Search sessions"),
        new(ShortcutAction.ToggleSidebar, "B", false, "Ctrl+B", "Show or hide the sidebar"),
        new(ShortcutAction.FindInChat, "F", false, "Ctrl+F", "Find in chat"),
        new(ShortcutAction.ToggleTerminal, "Oem3", false, "Ctrl+`", "Terminal drawer (after opt-in)"),
        new(ShortcutAction.ShowShortcuts, "Oem2", false, "Ctrl+/", "Keyboard shortcuts")
    ];

    /// <summary>The action for a key with exactly the given modifiers, or null. Alt or no Ctrl never matches.</summary>
    public static ShortcutAction? Match(string key, bool ctrl, bool shift, bool alt)
    {
        if (!ctrl || alt) return null;
        foreach (var s in All)
            if (s.Shift == shift && string.Equals(s.Key, key, StringComparison.Ordinal)) return s.Action;
        return null;
    }

    /// <summary>The first (preferred) key label for an action, e.g. "Ctrl+K" for the palette.</summary>
    public static string DisplayFor(ShortcutAction action) => All.First(s => s.Action == action).Display;

    /// <summary>A plain-text list for the Ctrl+/ panel, one line per key.</summary>
    /// <summary>The shortcut list. Simple view leaves out the terminal key, which Simple does not offer.</summary>
    public static string HelpText(bool advanced = true) =>
        string.Join("\n", All.Where(s => advanced || s.Action != ShortcutAction.ToggleTerminal).Select(s => $"{s.Display}   {s.Label}"));
}
