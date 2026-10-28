using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 36: the one Simple / Advanced table. Simple is for someone with little computer experience; Advanced is for managing Hermes.</summary>
public class Checkpoint36ExperienceTests
{
    [Fact]
    public void Simple_shows_exactly_chats_the_inbox_needs_you_and_the_three_basic_settings_sections()
    {
        Assert.Equal(
            new[]
            {
                ExperienceFeature.Chats, ExperienceFeature.Inbox, ExperienceFeature.NeedsYou,
                ExperienceFeature.ProfileSwitcher,                     // Stage 41: whom to talk to (only with more than one profile ticked)
                ExperienceFeature.Artifacts,                           // Stage 41: the agent's pictures and files (read-only)
                ExperienceFeature.SettingsAccount, ExperienceFeature.SettingsAppearance, ExperienceFeature.SettingsExperience
            }.Order(),
            Experience.SimpleSet.Order());
    }

    [Fact]
    public void Advanced_shows_every_feature()
    {
        Assert.All(Enum.GetValues<ExperienceFeature>(), f => Assert.True(Experience.IsVisible(f, advanced: true)));
    }

    [Fact]
    public void Everything_that_manages_hermes_is_advanced_only()
    {
        ExperienceFeature[] admin =
        [
            ExperienceFeature.Bots, ExperienceFeature.Rooms, ExperienceFeature.Capabilities, ExperienceFeature.Journey, ExperienceFeature.Insights,
            ExperienceFeature.Projects, ExperienceFeature.InboxSetup, ExperienceFeature.ConfigEditor, ExperienceFeature.McpEdit, ExperienceFeature.Scheduled, ExperienceFeature.Terminal,   // Artifacts: both views since Stage 41
            ExperienceFeature.ModelChip, ExperienceFeature.BranchChat, ExperienceFeature.ReasoningChip, ExperienceFeature.FastChip,
            ExperienceFeature.ToolDetails, ExperienceFeature.SessionFilters, ExperienceFeature.ReasoningDetails, ExperienceFeature.AgentPlan, ExperienceFeature.AgentNotices,
            ExperienceFeature.SettingsConnection, ExperienceFeature.SettingsAccess, ExperienceFeature.SettingsUpdates,
            ExperienceFeature.SettingsProfiles, ExperienceFeature.SettingsApprovals, ExperienceFeature.SettingsNotifications
        ];
        Assert.All(admin, f => Assert.False(Experience.IsVisible(f, advanced: false), f.ToString()));
        Assert.Equal(admin.Order(), Experience.AdvancedOnly.Order());       // nothing is hidden that is not listed, and nothing listed is shown
    }

    [Fact]
    public void An_approval_the_agent_is_waiting_on_and_the_way_back_to_advanced_are_never_hidden_in_simple()
    {
        Assert.True(Experience.IsVisible(ExperienceFeature.NeedsYou, advanced: false));
        Assert.True(Experience.IsVisible(ExperienceFeature.SettingsExperience, advanced: false));
        Assert.True(Experience.IsVisible(ExperienceFeature.SettingsAccount, advanced: false));    // sign in / out
    }

    // ---- the Settings window plan (the real window is checked against this in the UI acceptance run) ----

    [Fact]
    public void Simple_settings_after_setup_show_sign_in_and_hide_every_admin_section()
    {
        var p = SettingsSections.For(advanced: false, setupNeeded: false);
        Assert.True(p.SignInButtons && p.Credentials && p.AccountHeading);           // signing in again must stay possible
        Assert.False(p.FullSignInLabel);
        Assert.False(p.Connection || p.ConnectionHint || p.Access || p.Updates || p.Profiles || p.Approvals || p.Notifications);
    }

    [Fact]
    public void A_new_installation_in_simple_can_still_enter_the_address_and_choose_a_profile_but_nothing_else()
    {
        var p = SettingsSections.For(advanced: false, setupNeeded: true);
        Assert.True(p.Connection && p.ConnectionHint && p.Profiles && p.FullSignInLabel);   // otherwise "Open Settings to add a profile" is a dead end
        Assert.False(p.Access || p.Updates || p.Approvals || p.Notifications);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Advanced_settings_show_every_section_whether_or_not_setup_is_needed(bool setup)
    {
        var p = SettingsSections.For(advanced: true, setupNeeded: setup);
        Assert.True(p.Connection && p.ConnectionHint && p.Credentials && p.SignInButtons && p.Access && p.Updates && p.Profiles && p.Approvals && p.Notifications && p.FullSignInLabel);
        Assert.False(p.AccountHeading);
    }

    [Fact]
    public void Sign_in_is_never_hidden_in_any_mode()
    {
        foreach (var adv in new[] { true, false })
            foreach (var setup in new[] { true, false })
            {
                var p = SettingsSections.For(adv, setup);
                Assert.True(p.SignInButtons && p.Credentials, $"advanced={adv} setup={setup}");
            }
    }
}
