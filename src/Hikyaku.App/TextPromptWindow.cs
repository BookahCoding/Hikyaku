using System.Windows;
using System.Windows.Controls;

namespace Hikyaku.App;

/// <summary>A small modal "enter text" dialog (rename). Built in code; the caller gets the text, or null when cancelled.</summary>
public sealed class TextPromptWindow : Window
{
    private readonly TextBox _box;

    private TextPromptWindow(string title, string initial)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "Brush.Surface");

        _box = new TextBox { Text = initial, MaxLength = 200, Margin = new Thickness(0, 0, 0, 12) };
        System.Windows.Automation.AutomationProperties.SetName(_box, title);
        var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 72 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 72, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(_box);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(Window owner, string title, string initial)
    {
        var w = new TextPromptWindow(title, initial) { Owner = owner };
        return w.ShowDialog() == true ? w._box.Text : null;
    }
}
