using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 36 follow-up (advisor review): the Settings window for Simple view (first run and signing in again must work), and state that a
/// hidden control could leave stranded (filters, the Archive verb). Selection is chosen at construction.
/// </summary>
[Collection("StaMath")]
public class Checkpoint36SimpleSettingsWpfTests
{
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }

    // ---- state a hidden control could strand ----

    private sealed class Stub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"sessions\":[],\"total\":0,\"limit\":50,\"offset\":0}", Encoding.UTF8, "application/json") });
    }

    private static MainViewModel MakeWithSessions(bool advanced)
    {
        var selection = new ProfileSelectionService(new MemPrefs36b());
        selection.SetSelectedAsync([new ProfileKey("gw", "default")]).GetAwaiter().GetResult();
        var gate = new NotificationGate(selection);
        var auth = new DashboardAuth(new Uri("https://gw.example/"), new Stub());
        var catalog = new SessionCatalog(gate, () => new SessionsApi(auth));
        catalog.SetActiveProfile(new ProfileKey("gw", "default"));
        var vm = new MainViewModel(selection, gate, () => new DemoTransport(), () => "t",
            () => null, () => null, () => catalog, () => null, () => null, () => null, () => null, () => null);
        vm.AttachCron(() => null, "gw");
        vm.ApplyExperience(advanced);
        vm.NewChat();
        return vm;
    }

    [Fact]
    public void Entering_simple_turns_off_the_running_filter_and_the_archived_view_whose_chips_are_hidden()
    {
        RunSta(() =>
        {
            var vm = MakeWithSessions(advanced: true);
            vm.ToggleRunningFilterCommand!.Execute(null);
            Assert.True(vm.RunningFilterOn);
            vm.ApplyExperience(false);
            Assert.False(vm.RunningFilterOn);                                             // not stuck filtered with the chip gone

            var vm2 = MakeWithSessions(advanced: true);
            vm2.ToggleArchivedViewCommand!.Execute(null);
            Assert.True(vm2.ArchivedView);
            vm2.ApplyExperience(false);
            Assert.False(vm2.ArchivedView);                                               // not stuck on archived chats with no way back
        });
    }

    [Fact]
    public void Simple_does_not_offer_to_archive_a_chat_but_can_still_restore_an_archived_one()
    {
        RunSta(() =>
        {
            var simple = MakeWithSessions(advanced: false);
            var advanced = MakeWithSessions(advanced: true);
            var row = new SessionRowVM(new SessionSummary("s1", "Chat"));
            var archived = new SessionRowVM(new SessionSummary("s2", "Old"), archivedView: true);
            Assert.DoesNotContain(simple.SessionMenuFor(row), m => m.Verb == "archive");
            Assert.Equal(["rename", "pin", "unread", "delete"], simple.SessionMenuFor(row).Select(m => m.Verb));
            Assert.Contains(advanced.SessionMenuFor(row), m => m.Verb == "archive");
            Assert.Contains(simple.SessionMenuFor(archived), m => m.Verb == "archive" && m.Label == "Unarchive");
        });
    }

    [Fact]
    public void A_brand_new_chat_can_be_sent_in_simple_with_no_model_chip_to_pick_from()
    {
        RunSta(() =>
        {
            var vm = MakeWithSessions(advanced: false);
            Assert.Equal(Visibility.Collapsed, vm.ModelChipVisibility);
            vm.InputText = "Hello";
            Assert.True(vm.SendCommand.CanExecute(null));
        });
    }

    [Fact]
    public void The_shortcut_list_in_simple_does_not_advertise_the_terminal_key_and_advanced_does()
    {
        Assert.DoesNotContain("Ctrl+`", Shortcuts.HelpText(advanced: false));
        Assert.Contains("Ctrl+`", Shortcuts.HelpText(advanced: true));
        Assert.Contains("Ctrl+N", Shortcuts.HelpText(advanced: false));                  // the everyday keys stay
    }

    [Fact]
    public void The_inbox_hides_following_and_filtering_set_up_in_simple_and_says_what_it_is_in_plain_words()
    {
        RunSta(() =>
        {
            var simple = MakeWithSessions(advanced: false);
            Assert.Equal(Visibility.Collapsed, simple.InboxSetupVisibility);
            Assert.Equal(Visibility.Visible, simple.InboxSimpleIntroVisibility);
            var adv = MakeWithSessions(advanced: true);
            Assert.Equal(Visibility.Visible, adv.InboxSetupVisibility);
            Assert.Equal(Visibility.Collapsed, adv.InboxSimpleIntroVisibility);
        });
    }
}

file sealed class MemPrefs36b : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
