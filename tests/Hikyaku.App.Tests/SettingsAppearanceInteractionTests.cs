using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

[Collection("StaMath")]
public class SettingsAppearanceInteractionTests
{
    [Fact]
    public void Wheel_over_gallery_and_blank_content_scrolls_page_without_changing_selected_theme()
    {
        WithSettings(window =>
        {
            window.ShowPage("appearance");
            window.UpdateLayout();
            var scroller = (ScrollViewer)window.FindName("PageScroller");
            var picker = (ListBox)window.FindName("ThemePicker");
            Assert.True(scroller.ScrollableHeight > 0);
            var selected = picker.SelectedItem;
            var tile = (ListBoxItem)picker.ItemContainerGenerator.ContainerFromIndex(0);
            Wheel(tile, -120);
            Drain(window);
            Assert.True(scroller.VerticalOffset > 0, "The page must scroll while the pointer is over a theme tile.");
            Assert.Same(selected, picker.SelectedItem);
            Wheel((UIElement)window.Content, 120);
            Drain(window);
            Assert.Equal(0, scroller.VerticalOffset);
            window.ShowPage("general");
            Drain(window);
            Assert.Equal(0, scroller.VerticalOffset);
        });
    }

    [Fact]
    public void Appearance_modes_are_in_the_initial_viewport_before_the_gallery_at_minimum_window_size()
    {
        WithSettings(window =>
        {
            window.ShowPage("appearance");
            window.UpdateLayout();
            var scroller = (ScrollViewer)window.FindName("PageScroller");
            var picker = (ListBox)window.FindName("ThemePicker");
            foreach (var name in new[] { "AppearLight", "AppearDark", "AppearSystem" })
            {
                var mode = (RadioButton)window.FindName(name);
                var position = mode.TranslatePoint(new Point(), scroller);
                Assert.InRange(position.Y, 0, scroller.ViewportHeight - mode.ActualHeight);
                Assert.True(position.Y < picker.TranslatePoint(new Point(), scroller).Y);
            }
        });
    }

    [Fact]
    public void Scrollbar_track_can_page_and_thumb_and_secondary_buttons_follow_live_accents()
    {
        WithSettings(window =>
        {
            window.ShowPage("appearance");
            window.UpdateLayout();
            var scroller = (ScrollViewer)window.FindName("PageScroller");
            var bar = (ScrollBar)scroller.Template.FindName("PART_VerticalScrollBar", scroller);
            Assert.True(bar.ActualWidth >= 12);
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            Assert.True(track.IncreaseRepeatButton.ActualHeight > 0);
            Assert.True(track.IncreaseRepeatButton.IsHitTestVisible);
            ScrollBar.PageDownCommand.Execute(null, bar);
            Drain(window);
            Assert.True(scroller.VerticalOffset > 0);
            window.Resources["Brush.Accent"] = Brushes.OrangeRed;
            track.Thumb.ApplyTemplate();
            var thumbFill = Descendants<Border>(track.Thumb).First();
            Assert.Equal(Colors.OrangeRed, ((SolidColorBrush)thumbFill.Background).Color);
            var secondary = new Button { Content = "Secondary", Style = (Style)window.FindResource("SecondaryButton") };
            ((Panel)((Border)window.FindName("ThemePreview")).Parent).Children.Add(secondary);
            window.Resources["Brush.Secondary"] = Brushes.Cyan;
            window.Resources["Brush.OnSecondary"] = Brushes.Black;
            Assert.Equal(Colors.Cyan, ((SolidColorBrush)secondary.Background).Color);
            Assert.Equal(Colors.Black, ((SolidColorBrush)secondary.Foreground).Color);
        });
    }

    [Fact]
    public void Checked_indicators_and_selected_tabs_use_the_live_secondary_accent()
    {
        WithSettings(window =>
        {
            var panel = (StackPanel)window.FindName("PageAppearance");
            var check = new CheckBox { Content = "Enabled", IsChecked = true };
            var radio = new RadioButton { Content = "Chosen", IsChecked = true };
            var tab = new TabItem { Header = "Overview", Content = "Summary", IsSelected = true };
            var tabs = new TabControl();
            tabs.Items.Add(tab);
            panel.Children.Insert(0, check);
            panel.Children.Insert(1, radio);
            panel.Children.Insert(2, tabs);
            window.ShowPage("appearance");
            window.Resources["Brush.Secondary"] = Brushes.Cyan;
            window.Resources["Brush.OnSecondary"] = Brushes.Black;
            Drain(window);
            Assert.Contains(Descendants<Border>(check), b => b.Background is SolidColorBrush brush && brush.Color == Colors.Cyan);
            Assert.Contains(Descendants<System.Windows.Shapes.Ellipse>(radio), e => e.Fill is SolidColorBrush brush && brush.Color == Colors.Cyan);
            Assert.Contains(Descendants<Border>(tab), b => b.Background is SolidColorBrush brush && brush.Color == Colors.Cyan);
            Assert.Equal(Colors.Black, ((SolidColorBrush)tab.Foreground).Color);
            window.Resources["Brush.Secondary"] = Brushes.Violet;
            Assert.Contains(Descendants<Border>(check), b => b.Background is SolidColorBrush brush && brush.Color == Colors.Violet);
            Assert.Contains(Descendants<System.Windows.Shapes.Ellipse>(radio), e => e.Fill is SolidColorBrush brush && brush.Color == Colors.Violet);
            Assert.Contains(Descendants<Border>(tab), b => b.Background is SolidColorBrush brush && brush.Color == Colors.Violet);
        });
    }

    [Fact]
    public void Wheel_over_value_controls_scrolls_without_changing_their_values()
    {
        WithSettings(window =>
        {
            var panel = (StackPanel)window.FindName("PageAppearance");
            var combo = new ComboBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0 };
            var slider = new Slider { Minimum = 0, Maximum = 10, Value = 5 };
            panel.Children.Insert(0, combo);
            panel.Children.Insert(1, slider);
            window.ShowPage("appearance");
            Drain(window);
            var scroller = (ScrollViewer)window.FindName("PageScroller");
            Wheel(combo, -120);
            Drain(window);
            var firstOffset = scroller.VerticalOffset;
            Assert.True(firstOffset > 0);
            Assert.Equal(0, combo.SelectedIndex);
            Wheel(slider, -120);
            Drain(window);
            Assert.True(scroller.VerticalOffset > firstOffset);
            Assert.Equal(5, slider.Value);
        });
    }

    [Fact]
    public void Nested_viewer_scrolls_itself_then_passes_wheel_at_both_boundaries_to_the_page()
    {
        WithSettings(window =>
        {
            // A separate content host exercises shared styles without the settings-only wheel handler.
            var text = new TextBlock { Text = "Nested file list", Height = 900 };
            var inner = new ScrollViewer { Content = text, Height = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var content = new StackPanel();
            content.Children.Add(new Border { Height = 200 });
            content.Children.Add(inner);
            content.Children.Add(new Border { Height = 1200 });
            var outer = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var host = new Window { Content = outer, Width = 400, Height = 300, ShowInTaskbar = false };
            try
            {
                host.Show();
                Drain(host);
                outer.ScrollToVerticalOffset(150);
                Drain(host);
                Wheel(text, -120);
                Drain(host);
                Assert.True(inner.VerticalOffset > 0);
                Assert.Equal(150, outer.VerticalOffset);
                inner.ScrollToBottom();
                Drain(host);
                Wheel(text, -120);
                Drain(host);
                Assert.True(outer.VerticalOffset > 150, "At the inner bottom the outer page must continue scrolling.");
                var afterDown = outer.VerticalOffset;
                inner.ScrollToTop();
                Drain(host);
                Wheel(text, 120);
                Drain(host);
                Assert.True(outer.VerticalOffset < afterDown, "At the inner top the outer page must scroll upward.");
            }
            finally { host.Close(); }
        });
    }

    [Fact]
    public void Default_lists_and_horizontal_viewers_keep_accented_scrollbars_and_dragging()
    {
        WithSettings(window =>
        {
            var panel = (StackPanel)window.FindName("PageAppearance");
            var list = new ListBox { Height = 80, ItemsSource = Enumerable.Range(1, 40).Select(i => "Item " + i).ToArray() };
            var horizontal = new ScrollViewer { Width = 200, Height = 60, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = new Border { Width = 1800, Height = 20 } };
            panel.Children.Insert(0, list);
            panel.Children.Insert(1, horizontal);
            window.ShowPage("appearance");
            Drain(window);
            var listViewer = Descendants<ScrollViewer>(list).First();
            var verticalBar = (ScrollBar)listViewer.Template.FindName("PART_VerticalScrollBar", listViewer);
            Assert.True(verticalBar.ActualWidth >= 12);
            var bar = (ScrollBar)horizontal.Template.FindName("PART_HorizontalScrollBar", horizontal);
            Assert.True(bar.ActualHeight >= 12);
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            Assert.True(track.IncreaseRepeatButton.ActualWidth > 0);
            ScrollBar.PageRightCommand.Execute(null, bar);
            Drain(window);
            Assert.True(horizontal.HorizontalOffset > 0);
            var beforeDrag = horizontal.HorizontalOffset;
            track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            track.Thumb.RaiseEvent(new DragDeltaEventArgs(10, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            track.Thumb.RaiseEvent(new DragCompletedEventArgs(10, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            Drain(window);
            Assert.True(horizontal.HorizontalOffset > beforeDrag);
        });
    }

    [Fact]
    public void Long_list_descriptions_wrap_within_a_narrow_vertical_list()
    {
        WithSettings(window =>
        {
            var description = new TextBlock { Text = string.Join(" ", Enumerable.Repeat("A readable long description", 20)), TextWrapping = TextWrapping.Wrap };
            var list = new ListBox { Width = 260, Height = 120 };
            list.Items.Add(description);
            ((StackPanel)window.FindName("PageAppearance")).Children.Insert(0, list);
            window.ShowPage("appearance");
            Drain(window);
            var viewer = Descendants<ScrollViewer>(list).First();
            Assert.True(viewer.ExtentWidth <= viewer.ViewportWidth + 1, "Vertical lists must not measure descriptions at unlimited horizontal width.");
            Assert.True(description.ActualHeight > 40, "A long description should wrap into multiple lines.");
            Assert.InRange(description.ActualWidth, 1, 260);
        });
    }

    [Fact]
    public void Text_selection_follows_the_live_secondary_without_changing_the_selection()
    {
        WithSettings(window =>
        {
            var editor = new TextBox { Text = "Example editable text" };
            ((StackPanel)window.FindName("PageAppearance")).Children.Insert(0, editor);
            window.ShowPage("appearance");
            editor.Select(0, 7);
            window.Resources["Brush.Secondary"] = Brushes.Cyan;
            Drain(window);
            Assert.Equal(Colors.Cyan, ((SolidColorBrush)editor.SelectionBrush).Color);
            Assert.Equal("Example", editor.SelectedText);
            window.Resources["Brush.Secondary"] = Brushes.Violet;
            Assert.Equal(Colors.Violet, ((SolidColorBrush)editor.SelectionBrush).Color);
            Assert.Equal("Example", editor.SelectedText);
        });
    }

    private static void Wheel(UIElement source, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        source.RaiseEvent(args);
        if (!args.Handled)
        {
            args.RoutedEvent = Mouse.MouseWheelEvent;
            source.RaiseEvent(args);
        }
    }

    private static void Drain(Window window)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void WithSettings(Action<SettingsWindow> action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SettingsWindow? window = null;
            try
            {
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Clear();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Hikyaku;component/Theme/Tokens.xaml", UriKind.Relative) });
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Hikyaku;component/Theme/Styles.xaml", UriKind.Relative) });
                window = new SettingsWindow(new AppSettings { Experience = "simple" }, "unused-settings.example.json",
                    new ProfileSelectionService(new Preferences()), new Secrets());
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                window.Show();
                Drain(window);
                action(window);
            }
            catch (Exception ex) { error = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class Secrets : ISecretStore
    {
        public string? Get(string name) => null;
        public void Set(string name, string value) { }
        public void Remove(string name) { }
    }
    private sealed class Preferences : IPreferenceStore
    {
        public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SaveAsync(string content, CancellationToken ct = default) => Task.CompletedTask;
    }
}

