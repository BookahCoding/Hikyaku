using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Threading;
using System.Xml.Linq;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

[Collection("StaMath")]
public class WorkspacePresentationTests
{
    [Fact]
    public void Browsing_before_selection_has_compact_files_and_no_blank_preview_or_inactive_revokes()
    {
        WithPage("ProjectsVisibility", new BrowsingFixture(), window =>
        {
            var share = Descendants<Button>(window).Single(b => System.Windows.Data.BindingOperations.GetBinding(b, Button.CommandProperty)?.Path.Path == "ShareProjectFileCommand");
            Assert.False(share.IsVisible, "Preview actions must wait until a file is selected.");
            var files = Descendants<ListBox>(window).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Project files");
            Assert.InRange(files.ActualHeight, 90, 180);
            foreach (var command in new[] { "RevokeFolderCommand", "RevokeTerminalOptInCommand", "RevokeNetworkPublishCommand" })
            {
                var button = Descendants<Button>(window).Single(b => System.Windows.Data.BindingOperations.GetBinding(b, Button.CommandProperty)?.Path.Path == command);
                Assert.False(button.IsVisible, "Inactive risk actions should not clutter folder browsing.");
            }
        });
    }

    [Fact]
    public void Granted_folder_display_has_path_and_readable_access_without_machine_identifier()
    {
        WithPage("ProjectsVisibility", new { }, _ =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "hikyaku-project-display-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var selection = new ProfileSelectionService(new PresentationPreferences());
                var key = new ProfileKey("demo", "default");
                selection.SetSelectedAsync([key]).GetAwaiter().GetResult();
                var vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
                    () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
                const string machine = "synthetic-machine-identifier";
                var grants = new LocalFolderGrantStore(new PresentationPreferences(), () => machine);
                grants.GrantAsync(key, directory, FolderGrantMode.Read).GetAwaiter().GetResult();
                vm.AttachFolderGrants(grants);
                vm.RefreshProjectCommand!.Execute(null);
                Assert.Contains(directory, vm.ProjectRootLabel);
                Assert.DoesNotContain(machine, vm.ProjectRootLabel);
                Assert.Contains("Read only", vm.ProjectRootLabel);
            }
            finally { Directory.Delete(directory, true); }
        });
    }

    [Fact]
    public void Bot_can_be_opened_through_accessibility_without_a_mouse_row_click()
    {
        var row = new { Name = "Demo assistant", Handle = "@demo", Preview = "Ready", Meta = "Demo profile" };
        object? opened = null;
        WithPage("BotsVisibility", new { BotRows = new[] { row }, OpenBotCommand = new RelayCommand(value => opened = value) }, window =>
        {
            var open = Descendants<Button>(window).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Open bot");
            Assert.True(open.Focusable);
            var peer = new ButtonAutomationPeer(open);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Same(row, opened);
        });
    }

    [Fact]
    public void Closing_workflow_disclosures_does_not_hide_risk_reduction_controls()
    {
        WithPage("ProjectsVisibility", new { ProjectChangesTabVisibility = Visibility.Visible, ProjectFilesTabVisibility = Visibility.Collapsed, ProjectPreviewTabVisibility = Visibility.Collapsed, StopSharingVisibility = Visibility.Visible, TerminalOptedIn = true, NetworkPublishEnabled = true }, window =>
        {
            foreach (var expander in Descendants<Expander>(window)) expander.IsExpanded = false;
            window.UpdateLayout();
            foreach (var command in new[] { "RevokeFolderCommand", "RevokeTerminalOptInCommand", "RevokeNetworkPublishCommand" })
            {
                var control = Descendants<Button>(window).Single(b => System.Windows.Data.BindingOperations.GetBinding(b, Button.CommandProperty)?.Path.Path == command);
                Assert.True(control.IsVisible, command + " must remain available while disclosures are closed.");
            }
        });
    }

    [Fact]
    public void Expanded_project_workflows_scroll_to_the_last_action_at_minimum_width()
    {
        WithPage("ProjectsVisibility", new { ProjectChangesTabVisibility = Visibility.Visible, ProjectFilesTabVisibility = Visibility.Collapsed, ProjectPreviewTabVisibility = Visibility.Collapsed }, window =>
        {
            foreach (var expander in Descendants<Expander>(window)) expander.IsExpanded = true;
            window.UpdateLayout();
            var scroller = Descendants<ScrollViewer>(window).FirstOrDefault(s => s.Name == "ProjectsScroller");
            Assert.NotNull(scroller);
            Assert.True(scroller.ScrollableHeight > 0);
            var action = Descendants<Button>(window).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Create separate working copy");
            action.BringIntoView();
            window.UpdateLayout();
            var point = action.TranslatePoint(new Point(), scroller);
            Assert.InRange(point.Y, 0, scroller.ViewportHeight - action.ActualHeight + 1);
            Assert.True(scroller.ExtentWidth <= scroller.ViewportWidth + 1, "Projects must not overflow horizontally.");
        });
    }

    [Fact]
    public void Scheduled_cards_stretch_and_primary_actions_fit_a_narrow_page()
    {
        WithPage("ScheduledVisibility", new { CronRows = new[] { new { Title = "Daily workspace summary", StateLabel = "Enabled", Schedule = "Every morning", NextRun = "Tomorrow", LastRun = "Today", Meta = "Profile: demo" } } }, window =>
        {
            var list = Descendants<ListBox>(window).Single();
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.True(item.ActualWidth >= list.ActualWidth - 30);
            var run = Descendants<Button>(item).Single(b => Equals(b.Content, "Run now"));
            var point = run.TranslatePoint(new Point(), window);
            Assert.InRange(point.X, 0, window.ActualWidth - run.ActualWidth);
            var border = Descendants<Border>(item).First(b => b.Child is StackPanel);
            Assert.True(border.ActualWidth >= item.ActualWidth - 30, "The card body must fill the row.");
        });
    }

    [Fact]
    public void Expanded_terminal_help_keeps_output_and_input_usable()
    {
        WithPage("TerminalDrawerVisibility", new
        {
            TerminalHostLabel = "Local terminal",
            TerminalCwdLabel = "A long synthetic working directory with several nested folders",
            TerminalHonesty = string.Join("\n", Enumerable.Repeat("Commands run on this PC with this Windows account. Review before running.", 14)),
            TerminalOutput = "Synthetic output",
            ShellChoices = Array.Empty<string>()
        }, window =>
        {
            foreach (var expander in Descendants<Expander>(window)) expander.IsExpanded = true;
            window.UpdateLayout();
            var output = Descendants<TextBox>(window).Single(t => t.Name == "TerminalOutputBox");
            Assert.True(output.ActualHeight >= 90, "Expanded permissions must leave useful terminal output space.");
            var input = Descendants<TextBox>(window).Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Terminal input");
            var position = input.TranslatePoint(new Point(), window);
            Assert.InRange(position.Y + input.ActualHeight, 0, window.ActualHeight);
        });
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) yield return result;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed class PresentationPreferences : IPreferenceStore
    {
        private string? _content;
        public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
        public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
    }

    private sealed class BrowsingFixture
    {
        public Visibility ProjectFilesTabVisibility => Visibility.Visible;
        public Visibility ProjectPreviewTabVisibility => Visibility.Visible;
        public Visibility ProjectChangesTabVisibility => Visibility.Collapsed;
        public object? SelectedProjectFile { get; set; }
        public object[] ProjectFiles { get; } = [new { Name = "notes.txt", Meta = "file" }];
        public Visibility StopSharingVisibility => Visibility.Collapsed;
        public bool TerminalOptedIn => false;
        public bool NetworkPublishEnabled => false;
    }

    private static void WithPage(string visibility, object model, Action<Window> check)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Clear();
                foreach (var resource in new[] { "Tokens", "Styles" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/Hikyaku;component/Theme/{resource}.xaml", UriKind.Relative) });
                app.Resources["BoolToVis"] = new BooleanToVisibilityConverter();
                var root = new DirectoryInfo(AppContext.BaseDirectory);
                while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/Hikyaku.App/MainWindow.xaml"))) root = root.Parent;
                var document = XDocument.Load(Path.Combine(root!.FullName, "src/Hikyaku.App/MainWindow.xaml"));
                XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var page = new XElement(document.Descendants().Single(e => (string?)e.Attribute("Visibility") == "{Binding " + visibility + "}"));
                page.SetAttributeValue("Visibility", "Visible");
                foreach (var attribute in page.DescendantsAndSelf().Attributes().Where(a => new[] { "Click", "MouseLeftButtonUp", "SelectionChanged", "SizeChanged", "PreviewKeyDown", "TextChanged", "Checked", "Unchecked" }.Contains(a.Name.LocalName)).ToList()) attribute.Remove();
                var shell = new XElement(ns + "Window", new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"), new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:Hikyaku.App;assembly=Hikyaku"));
                if (document.Root!.Element(ns + "Window.Resources") is { } resources) shell.Add(new XElement(resources));
                shell.Add(page);
                window = (Window)XamlReader.Parse(shell.ToString());
                window.Width = 672; window.Height = 430; window.DataContext = model;
                window.Show(); window.UpdateLayout();
                check(window);
            }
            catch (Exception exception) { error = exception; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
