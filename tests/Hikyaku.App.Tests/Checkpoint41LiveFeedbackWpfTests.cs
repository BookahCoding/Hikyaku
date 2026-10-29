using System.Windows;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 41 — fixes from the first live test against a real gateway (the maintainer, 2026-10-27), through the real view model.
/// The ticked profiles are chosen at construction (changing them later hangs the test host).
/// </summary>
[Collection("StaMath")]
public class Checkpoint41LiveFeedbackWpfTests
{
    private static MainViewModel Make(string[] profileIds, bool advanced = true)
    {
        var selection = new ProfileSelectionService(new MemPrefs41());
        selection.SetSelectedAsync(profileIds.Select(id => new ProfileKey("home", id))).GetAwaiter().GetResult();
        var vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
            () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
        vm.ApplyExperience(advanced);
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

    [Fact]
    public void New_chat_goes_to_the_main_agent_not_to_the_bot_whose_id_sorts_first()
    {
        RunSta(() =>
        {
            var vm = Make(["bookahmail", "default", "zeta"]);
            vm.NewChat();
            Assert.Equal("default", vm.SelectedConversation!.Profile.ProfileId);
            Assert.Equal(Visibility.Visible, vm.ProfileSwitcherVisibility);
            Assert.Equal(3, vm.ProfileChoices.Count);
            Assert.Equal("default", vm.ProfileChoices[0].Key.ProfileId);                 // the main agent is listed first
        });
    }

    [Fact]
    public void Picking_a_profile_in_the_switcher_moves_new_chats_to_it_and_is_remembered()
    {
        RunSta(() =>
        {
            var vm = Make(["bookahmail", "default", "zeta"]);
            string? remembered = null;
            vm.ActiveProfileChosen += id => remembered = id;

            vm.SelectedProfileChoice = vm.ProfileChoices.Single(c => c.Key.ProfileId == "zeta");
            Assert.Equal("zeta", remembered);
            vm.NewChat();
            Assert.Equal("zeta", vm.SelectedConversation!.Profile.ProfileId);
            Assert.Equal("zeta", vm.SelectedProfileChoice!.Key.ProfileId);
        });
    }

    [Fact]
    public void A_saved_choice_is_used_at_start_and_display_names_label_the_switcher()
    {
        RunSta(() =>
        {
            var vm = Make(["bookahmail", "default"]);
            vm.SetProfileNames(new Dictionary<string, string> { ["default"] = "Shisha", ["bookahmail"] = "BookahMail" });
            vm.InitActiveProfile("bookahmail");
            Assert.Equal("BookahMail", vm.CurrentProfileLabel);
            Assert.Contains(vm.ProfileChoices, c => c.Label == "Shisha");
            vm.InitActiveProfile("not-ticked");                                         // a saved profile that was unticked since
            Assert.Equal("Shisha", vm.CurrentProfileLabel);
        });
    }

    [Fact]
    public void Slash_commands_run_here_and_never_reach_the_agent()
    {
        RunSta(() =>
        {
            var vm = Make(["default"]);
            vm.NewChat();
            var chat = vm.SelectedConversation!;
            vm.InputText = "/reasoning high";
            vm.SendCommand.Execute(null);
            Assert.Equal("high", chat.PendingReasoning);
            Assert.Equal("", vm.InputText);
            Assert.Empty(chat.History);                                                // nothing was sent as a message

            var before = vm.Conversations.Count;
            vm.InputText = "/new";
            vm.SendCommand.Execute(null);
            Assert.Equal(before + 1, vm.Conversations.Count);
            Assert.NotSame(chat, vm.SelectedConversation);
        });
    }

    [Fact]
    public void Simple_view_refuses_advanced_commands_but_can_always_turn_yolo_off()
    {
        RunSta(() =>
        {
            var vm = Make(["default"], advanced: false);
            vm.NewChat();
            var chat = vm.SelectedConversation!;
            vm.InputText = "/model some-model";
            vm.SendCommand.Execute(null);
            Assert.Equal("/model some-model", vm.InputText);                           // left in the box, nothing sent
            Assert.Contains("Advanced", vm.RewindNotice);
            Assert.Empty(chat.History);

            chat.YoloEnabled = true;                                                   // a risk already in effect
            vm.InputText = "/yolo off";
            vm.SendCommand.Execute(null);
            Assert.False(chat.YoloEnabled);
        });
    }

    [Fact]
    public void Typing_a_slash_suggests_commands_for_the_current_view()
    {
        RunSta(() =>
        {
            var vm = Make(["default"]);
            vm.NewChat();
            vm.InputText = "/re";
            Assert.Equal(["reasoning"], vm.SlashSuggestions.Select(c => c.Name));
            Assert.Equal(Visibility.Visible, vm.SlashSuggestionsVisibility);
            vm.CompleteSlashCommand(vm.SlashSuggestions[0]);
            Assert.Equal("/reasoning ", vm.InputText);
            Assert.Empty(vm.SlashSuggestions);                                         // an argument has started

            vm.ApplyExperience(false);
            vm.InputText = "/";
            Assert.DoesNotContain(vm.SlashSuggestions, c => c.AdvancedOnly);
        });
    }

    [Fact]
    public void The_chat_box_shows_send_when_idle_and_no_row_of_model_chips()
    {
        RunSta(() =>
        {
            var vm = Make(["default"]);
            vm.NewChat();
            Assert.Equal(Visibility.Visible, vm.SendButtonVisibility);
            Assert.Equal(Visibility.Collapsed, vm.StopButtonVisibility);
            Assert.Equal(Visibility.Visible, vm.ComposerPlaceholderVisibility);
            Assert.Equal(Visibility.Collapsed, vm.YoloPillVisibility);                 // shown only while YOLO is on
            vm.SelectedConversation!.YoloEnabled = true;
            vm.InputText = "x";                                                        // any composer change re-reads the state
            Assert.Equal(Visibility.Visible, vm.YoloPillVisibility);
            Assert.Equal(Visibility.Collapsed, vm.ComposerPlaceholderVisibility);
        });
    }

    [Fact]
    public void Simple_view_asks_you_to_write_a_message_instead_of_naming_Hermes()
    {
        RunSta(() =>
        {
            var simple = Make(["default"], advanced: false);
            simple.NewChat();
            Assert.Equal("Write a message…", simple.ComposerPlaceholder);
            var advanced = Make(["default"]);
            advanced.NewChat();
            Assert.Equal("Message Hermes…", advanced.ComposerPlaceholder);

            var named = Make(["bookahmail", "default"], advanced: false);
            named.SetProfileNames(new Dictionary<string, string> { ["default"] = "Shisha" });
            named.NewChat();
            Assert.Equal("Message Shisha…", named.ComposerPlaceholder);

            simple.ApplyExperience(true);
            Assert.Equal("Message Hermes…", simple.ComposerPlaceholder);
        });
    }

    [Fact]
    public void An_empty_chat_greets_by_the_chosen_agent_and_the_greeting_leaves_once_the_chat_starts()
    {
        RunSta(() =>
        {
            var vm = Make(["bookahmail", "default"]);
            vm.SetProfileNames(new Dictionary<string, string> { ["default"] = "Shisha" });
            vm.NewChat();
            Assert.True(vm.IsChatEmpty);
            Assert.Equal(Visibility.Visible, vm.EmptyChatVisibility);
            Assert.Equal("How can Shisha help today?", vm.EmptyChatGreeting);
            vm.Messages.Add(new MessageVM("You", "hi"));
            Assert.False(vm.IsChatEmpty);
            Assert.Equal(Visibility.Collapsed, vm.EmptyChatVisibility);
            vm.Nav = MainNav.Inbox;
            vm.Messages.Clear();
            Assert.Equal(Visibility.Collapsed, vm.EmptyChatVisibility);                  // only on the chat page
            vm.SetProfileNames(new Dictionary<string, string>());                        // no display name: never "How can default help"
            Assert.Equal("How can I help you today?", vm.EmptyChatGreeting);
        });
    }

    [Fact]
    public void With_one_profile_ticked_there_is_no_switcher()
    {
        RunSta(() =>
        {
            var vm = Make(["family-member"], advanced: false);
            Assert.Equal(Visibility.Collapsed, vm.ProfileSwitcherVisibility);
            vm.NewChat();
            Assert.Equal("family-member", vm.SelectedConversation!.Profile.ProfileId);
        });
    }
}

file sealed class MemPrefs41 : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
