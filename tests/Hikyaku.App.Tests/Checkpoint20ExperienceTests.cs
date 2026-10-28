using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 20b — Simple / Advanced is a visibility choice only: install-aware default, what each mode shows, tool lines vs status
/// lines, and the rule that a risk already in effect (YOLO on) is never hidden.
/// </summary>
[Collection("StaMath")]
public sealed class Checkpoint20ExperienceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hik-exp-" + Guid.NewGuid().ToString("N"));

    public Checkpoint20ExperienceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private string P => Path.Combine(_dir, "settings.json");

    // ---- settings ----------------------------------------------------------------------------------

    [Fact]
    public void A_brand_new_install_starts_simple()
    {
        var s = AppSettings.Load(P);                                          // no file yet
        Assert.False(s.IsAdvanced);
        Assert.Equal("simple", s.Experience);
    }

    [Fact]
    public void An_existing_install_without_the_choice_keeps_the_full_chrome()
    {
        File.WriteAllText(P, """{"GatewayUrl":"https://hermes.example","Username":"u"}""");
        var s = AppSettings.Load(P);
        Assert.True(s.IsAdvanced);                                            // nobody's controls vanish after an update
        Assert.Null(s.Experience);
        Assert.Equal("https://hermes.example", s.GatewayUrl);
    }

    [Fact]
    public void The_choice_round_trips_and_an_unreadable_file_does_not_hide_anything()
    {
        var s = AppSettings.Load(P);
        s.SetAdvanced(true);
        s.Save(P);
        Assert.True(AppSettings.Load(P).IsAdvanced);
        s.SetAdvanced(false);
        s.Save(P);
        Assert.False(AppSettings.Load(P).IsAdvanced);

        File.WriteAllText(P, "{ not json");
        Assert.True(AppSettings.Load(P).IsAdvanced);
        File.WriteAllText(P, """{"Experience":"something-else"}""");
        Assert.True(AppSettings.Load(P).IsAdvanced);                          // only an explicit "simple" simplifies
    }

    // ---- view model --------------------------------------------------------------------------------

    private sealed class Tools : IGatewayTransport
    {
        public Func<IEnumerable<ChatEvent>> Events = () => [new StreamCompleted()];
        public async IAsyncEnumerable<ChatEvent> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var e in Events()) yield return e;
        }
        public Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProfileInfo>>([]);
    }

    private static (MainViewModel Vm, Tools T) Make()
    {
        var t = new Tools();
        var selection = new ProfileSelectionService(new MemPrefs());
        selection.SetSelectedAsync(new[] { new ProfileKey("demo", "default") }).GetAwaiter().GetResult();
        var vm = new MainViewModel(selection, new NotificationGate(selection), () => t, () => "t",
            () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
        vm.NewChat();
        return (vm, t);
    }

    private static void SetYolo(MainViewModel vm, bool on) =>
        vm.SelectedConversation!.YoloEnabled = on;

    [Fact]
    public void Simple_hides_the_technical_chips_and_advanced_shows_them()
    {
        RunSta(() =>
        {
            var (vm, _) = Make();
            Assert.True(vm.IsAdvanced);
            Assert.Equal(Visibility.Visible, vm.AdvancedToolsVisibility);
            Assert.Equal(Visibility.Visible, vm.YoloVisibility);
            vm.IsAdvanced = false;
            Assert.Equal(Visibility.Collapsed, vm.AdvancedToolsVisibility);
            Assert.Equal(Visibility.Collapsed, vm.YoloVisibility);
            Assert.Equal("Show advanced tools", vm.ExperienceToggleLabel);
            vm.IsAdvanced = true;
            Assert.Equal(Visibility.Visible, vm.YoloVisibility);
            Assert.Equal("Use simple view", vm.ExperienceToggleLabel);
        });
    }

    [Fact]
    public void A_chat_with_yolo_on_keeps_its_indicator_in_simple_so_the_risk_is_never_hidden()
    {
        RunSta(() =>
        {
            var (vm, _) = Make();
            vm.IsAdvanced = false;
            SetYolo(vm, true);
            Assert.Equal(Visibility.Visible, vm.YoloVisibility);
            Assert.Equal("YOLO on", vm.SelectedYoloLabel);
            Assert.True(vm.ToggleYoloCommand.CanExecute(null));                   // and it can still be turned off
            SetYolo(vm, false);
            Assert.Equal(Visibility.Collapsed, vm.YoloVisibility);
        });
    }

    [Fact]
    public void Changing_the_mode_never_changes_what_is_allowed()
    {
        RunSta(() =>
        {
            var (vm, _) = Make();
            var before = (vm.ToggleYoloCommand.CanExecute(null), vm.SendCommand.CanExecute(null), vm.CycleModelCommand.CanExecute(null),
                vm.CycleReasoningCommand.CanExecute(null), vm.ToggleFastCommand.CanExecute(null), vm.AttachCommand.CanExecute(null));
            vm.IsAdvanced = false;
            var simple = (vm.ToggleYoloCommand.CanExecute(null), vm.SendCommand.CanExecute(null), vm.CycleModelCommand.CanExecute(null),
                vm.CycleReasoningCommand.CanExecute(null), vm.ToggleFastCommand.CanExecute(null), vm.AttachCommand.CanExecute(null));
            Assert.Equal(before, simple);                                          // hidden is not disabled, and not enabled either
        });
    }

    [Fact]
    public void The_user_flipping_the_mode_is_announced_once_but_applying_the_saved_mode_is_not()
    {
        RunSta(() =>
        {
            var (vm, _) = Make();
            var changes = new List<bool>();
            vm.ExperienceChanged += changes.Add;
            vm.ApplyExperience(false);                                             // startup / settings dialog closing
            Assert.False(vm.IsAdvanced);
            Assert.Empty(changes);
            vm.IsAdvanced = false;                                                 // no change: not announced
            Assert.Empty(changes);
            vm.IsAdvanced = true;
            Assert.Equal(new[] { true }, changes);
        });
    }

    [Fact]
    public void A_tool_line_goes_only_to_the_advanced_tool_summary_never_to_the_status_line()
    {
        RunSta(() =>
        {
            var (vm, t) = Make();
            vm.IsAdvanced = false;
            t.Events = () => [new ToolActivity("shell: ls -la /private"), new StreamCompleted()];
            vm.InputText = "go";
            vm.SendCommand.Execute(null);
            Pump(400);
            var reply = vm.Messages.Last();
            Assert.Equal("shell: ls -la /private", reply.ToolSummary);             // hidden in Simple by the view
            Assert.Equal("", reply.Activity);                                       // so nothing leaks through the always-visible line
        });
    }

    [Fact]
    public void Errors_use_the_status_line_which_stays_visible_in_simple()
    {
        RunSta(() =>
        {
            var (vm, t) = Make();
            vm.IsAdvanced = false;
            t.Events = () => [new ProtocolError("gateway said no")];
            vm.InputText = "go";
            vm.SendCommand.Execute(null);
            Pump(400);
            var reply = vm.Messages.Last();
            Assert.Contains("gateway said no", reply.Activity);
            Assert.Equal("", reply.ToolSummary);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var th = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        th.Join();
        if (error is not null) throw error;
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}

file sealed class MemPrefs : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
