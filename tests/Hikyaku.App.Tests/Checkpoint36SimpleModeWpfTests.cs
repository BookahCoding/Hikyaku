using System.Windows;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 36 — Simple view through the real MainViewModel. Simple is for someone with little computer experience (chats, the Inbox, Needs you);
/// Advanced is for managing Hermes. Visibility only: nothing here disables an approval, a grant or a stop. Selection is chosen at construction.
/// </summary>
[Collection("StaMath")]
public class Checkpoint36SimpleModeWpfTests
{
    private static MainViewModel Make(bool advanced)
    {
        var selection = new ProfileSelectionService(new MemPrefs36());
        selection.SetSelectedAsync([new ProfileKey("demo", "default")]).GetAwaiter().GetResult();
        var vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
            () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
        vm.ApplyExperience(advanced);
        vm.NewChat();
        return vm;
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }

    private static readonly ExperienceFeature[] SimpleFeatures = [ExperienceFeature.Chats, ExperienceFeature.Inbox, ExperienceFeature.NeedsYou, ExperienceFeature.Artifacts];

    [Fact]
    public void Every_page_is_classified_and_in_simple_only_chats_inbox_and_needs_you_can_ever_be_the_current_page()
    {
        RunSta(() =>
        {
            foreach (var nav in Enum.GetValues<MainNav>())
            {
                var feature = MainViewModel.FeatureOf(nav);                              // throws for a page nobody classified
                var simple = Make(advanced: false);
                simple.Nav = nav;
                if (SimpleFeatures.Contains(feature)) Assert.Equal(nav, simple.Nav);
                else Assert.Equal(MainNav.Chats, simple.Nav);                            // a hidden page can never become current, whoever asks

                var advanced = Make(advanced: true);
                advanced.Nav = nav;
                Assert.Equal(nav, advanced.Nav);
            }
        });
    }

    [Fact]
    public void Switching_to_simple_on_any_hidden_page_goes_back_to_chats()
    {
        RunSta(() =>
        {
            foreach (var nav in Enum.GetValues<MainNav>().Where(n => !SimpleFeatures.Contains(MainViewModel.FeatureOf(n))))
            {
                var vm = Make(advanced: true);
                vm.Nav = nav;
                Assert.Equal(nav, vm.Nav);
                vm.ApplyExperience(false);
                Assert.Equal(MainNav.Chats, vm.Nav);
                Assert.Equal(Visibility.Visible, vm.ChatsVisibility);
            }
        });
    }

    [Fact]
    public void Switching_to_simple_closes_what_was_open_inside_an_advanced_page()
    {
        RunSta(() =>
        {
            var vm = Make(advanced: true);
            var node = new JourneyNode("s1", "skill", "skill", null, null, 1, null, false, null);
            vm.AcceptanceSetJourney(new JourneyGraph([node]), "default");
            vm.Nav = MainNav.Journey;
            vm.OpenJourneyNodeAsync(vm.JourneyMonths.Single().Nodes.Single()).GetAwaiter().GetResult();
            Assert.Equal(Visibility.Visible, vm.JourneyDetailVisibility);
            vm.ApplyExperience(false);
            Assert.Equal(Visibility.Collapsed, vm.JourneyDetailVisibility);
        });
    }

    [Fact]
    public void The_sidebar_shows_only_the_beginner_rows_in_simple_and_all_of_them_in_advanced()
    {
        RunSta(() =>
        {
            Visibility[] Rows(MainViewModel v) =>
            [
                v.BotsNavVisibility, v.RoomsNavVisibility, v.CapabilitiesNavVisibility, v.JourneyNavVisibility,
                v.InsightsNavVisibility, v.ProjectsNavVisibility, v.ScheduledNavVisibility
            ];
            Assert.All(Rows(Make(advanced: true)), v => Assert.Equal(Visibility.Visible, v));
            Assert.All(Rows(Make(advanced: false)), v => Assert.Equal(Visibility.Collapsed, v));
            // Stage 41 (the maintainer): Artifacts, the agent's pictures and files, is in both views.
            Assert.Equal(Visibility.Visible, Make(advanced: false).ArtifactsNavVisibility);
        });
    }

    [Fact]
    public void The_palette_in_simple_lists_no_advanced_page_or_action_but_keeps_the_basics_and_the_way_back()
    {
        RunSta(() =>
        {
            var vm = Make(advanced: false);
            string[] hidden =
            [
                "nav.bots", "nav.rooms", "nav.capabilities", "nav.journey", "nav.insights", "nav.projects", "nav.scheduled",
                "chat.branch", "sessions.archived", "sessions.running"
            ];
            vm.OpenPaletteCommand!.Execute(null);
            vm.PaletteQuery = "";
            var all = vm.PaletteResults.Select(r => r.Id).ToList();
            vm.PaletteQuery = "open";
            all.AddRange(vm.PaletteResults.Select(r => r.Id));
            vm.PaletteQuery = "show";
            all.AddRange(vm.PaletteResults.Select(r => r.Id));
            vm.PaletteQuery = "branch";
            all.AddRange(vm.PaletteResults.Select(r => r.Id));
            Assert.DoesNotContain(all, id => hidden.Contains(id));
            vm.PaletteQuery = "open";
            var open = vm.PaletteResults.Select(r => r.Id).ToList();
            Assert.Contains("nav.chats", open);
            Assert.Contains("nav.inbox", open);
            Assert.Contains("nav.needs-you", open);
            Assert.Contains("nav.artifacts", open);
            vm.PaletteQuery = "advanced";
            Assert.Contains(vm.PaletteResults, r => r.Id == "app.experience");          // Simple always offers the way back

            var adv = Make(advanced: true);
            adv.OpenPaletteCommand!.Execute(null);
            adv.PaletteQuery = "open";
            var advIds = adv.PaletteResults.Select(r => r.Id).ToList();
            Assert.All(new[] { "nav.bots", "nav.rooms", "nav.capabilities", "nav.journey", "nav.insights", "nav.projects", "nav.scheduled", "nav.artifacts" },
                id => Assert.Contains(id, advIds));
        });
    }

    [Fact]
    public void The_technical_chips_are_hidden_in_simple_and_a_yolo_that_is_on_would_still_show()
    {
        RunSta(() =>
        {
            var simple = Make(advanced: false);
            Assert.Equal(Visibility.Collapsed, simple.ModelChipVisibility);
            Assert.Equal(Visibility.Collapsed, simple.BranchVisibility);
            Assert.Equal(Visibility.Collapsed, simple.ReasoningChipVisibility);
            Assert.Equal(Visibility.Collapsed, simple.FastChipVisibility);
            Assert.Equal(Visibility.Collapsed, simple.SessionFiltersVisibility);
            Assert.Equal(Visibility.Collapsed, simple.AdvancedToolsVisibility);          // tool steps and raw tool lines
            Assert.Equal(Visibility.Collapsed, simple.YoloVisibility);                   // off, and technical: hidden

            var adv = Make(advanced: true);
            Assert.Equal(Visibility.Visible, adv.ModelChipVisibility);
            Assert.Equal(Visibility.Visible, adv.BranchVisibility);
            Assert.Equal(Visibility.Visible, adv.SessionFiltersVisibility);
            Assert.Equal(Visibility.Visible, adv.YoloVisibility);
        });
    }

    [Fact]
    public void A_folder_shared_with_hermes_is_shown_in_simple_with_the_way_to_stop_it_and_not_duplicated_in_advanced()
    {
        RunSta(() =>
        {
            var simple = Make(advanced: false);
            Assert.Equal(Visibility.Collapsed, simple.RiskNoticeVisibility);             // nothing shared: no notice
            simple.CurrentProjectPathHint = @"D:\Shared\Recipes";
            simple.RaiseRiskNotice();
            Assert.Equal(Visibility.Visible, simple.RiskNoticeVisibility);
            Assert.Contains("A folder on this computer is shared with Hermes (Recipes).", simple.RiskNotice);
            Assert.Equal(Visibility.Visible, simple.StopSharingVisibility);

            simple.ApplyExperience(true);
            Assert.Equal(Visibility.Collapsed, simple.RiskNoticeVisibility);             // Advanced shows it on its own page
            simple.ApplyExperience(false);
            Assert.Equal(Visibility.Visible, simple.RiskNoticeVisibility);

            simple.CurrentProjectPathHint = null;
            simple.RaiseRiskNotice();
            Assert.Equal(Visibility.Collapsed, simple.RiskNoticeVisibility);
        });
    }

    [Fact]
    public void Routine_voice_housekeeping_is_hidden_in_simple_and_everything_else_is_shown()
    {
        RunSta(() =>
        {
            var simple = Make(advanced: false);
            Assert.Equal(Visibility.Collapsed, simple.VoiceStatusVisibility);            // the default hint
            // Stage 41 (live test): the routine lines are hidden in Advanced too; they sat under the chat box all the time.
            Assert.Equal(Visibility.Collapsed, Make(advanced: true).VoiceStatusVisibility);
        });
    }

    [Fact]
    public void Simple_never_disables_an_approval_or_a_stop_it_only_hides_technical_surfaces()
    {
        RunSta(() =>
        {
            var simple = Make(advanced: false);
            var adv = Make(advanced: true);
            Assert.Equal(adv.CancelCommand.CanExecute(null), simple.CancelCommand.CanExecute(null));
            Assert.Equal(adv.ShowNeedsYouCommand.CanExecute(null), simple.ShowNeedsYouCommand.CanExecute(null));
            simple.ShowNeedsYouCommand.Execute(null);
            Assert.Equal(MainNav.NeedsYou, simple.Nav);                                  // an approval can always be reached
            simple.ShowInboxCommand.Execute(null);
            Assert.Equal(MainNav.Inbox, simple.Nav);
        });
    }
}

file sealed class MemPrefs36 : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
