namespace Hikyaku.Core;

/// <summary>Every surface that Simple or Advanced view can show or hide. A new surface must be classified in <see cref="Experience"/>.</summary>
public enum ExperienceFeature
{
    // sidebar pages
    Chats, Inbox, InboxSetup, NeedsYou, Bots, Rooms, Capabilities, Journey, Insights, Projects, Scheduled, Artifacts,
    // gateway configuration (writes config.yaml)
    ConfigEditor, McpEdit,
    // choosing which ticked profile (agent) to talk to; shown only when more than one is ticked
    ProfileSwitcher,
    // chat chrome
    ModelChip, BranchChat, ReasoningChip, FastChip, ToolDetails, SessionFilters, Terminal,
    // what the agent did while answering (Stage 44): the reasoning text, the plan checklist and the agent's notices (such as
    // "Context was compressed"); Simple keeps a plain activity line and the risk warning
    ReasoningDetails, AgentPlan, AgentNotices,
    // talking with the agent: the hands-free voice chat in the chat box (0.6.0), everyday chatting in both views
    VoiceChat,
    // settings sections
    SettingsAccount, SettingsAppearance, SettingsExperience,
    SettingsConnection, SettingsAccess, SettingsUpdates, SettingsProfiles, SettingsApprovals, SettingsNotifications,
}

/// <summary>
/// The one table behind Simple / Advanced (the maintainer, 2026-10-27): Simple is for someone with little computer experience (chats, the Inbox,
/// and the Needs-you approvals), Advanced is for managing Hermes. This is a <b>visibility</b> choice only: it never changes what is
/// allowed, never disables an approval, grant or stop control, and a risk already in effect (YOLO on, a shared folder, a running
/// terminal) is shown in Simple too. It is not a lock: anyone at the PC can switch (the maintainer chose one click, no PIN).
/// Every nav row, page, palette entry, shortcut, chip and settings section reads this table instead of keeping its own rule.
/// </summary>
public static class Experience
{
    private static readonly HashSet<ExperienceFeature> SimpleFeatures =
    [
        ExperienceFeature.Chats,
        ExperienceFeature.Inbox,
        ExperienceFeature.NeedsYou,          // an approval the agent is waiting on must never be hidden
        ExperienceFeature.ProfileSwitcher,   // whom to talk to is everyday chatting (one ticked profile hides it anyway)
        ExperienceFeature.Artifacts,         // the pictures and files the agent made: read-only on the gateway (the maintainer, 2026-10-27)
        ExperienceFeature.VoiceChat,         // talking with the agent is everyday chatting (the maintainer, 2026-10-28)
        ExperienceFeature.SettingsAccount,   // sign in / out
        ExperienceFeature.SettingsAppearance,
        ExperienceFeature.SettingsExperience // the way back to Advanced, and the mode choice, in both views
    ];

    public static bool IsVisible(ExperienceFeature feature, bool advanced) => advanced || SimpleFeatures.Contains(feature);

    public static IReadOnlyCollection<ExperienceFeature> SimpleSet => SimpleFeatures;

    /// <summary>Everything that exists only in Advanced.</summary>
    public static IEnumerable<ExperienceFeature> AdvancedOnly => Enum.GetValues<ExperienceFeature>().Where(f => !SimpleFeatures.Contains(f));
}

/// <summary>Which parts of the Settings window show (the window applies exactly this).</summary>
public sealed record SettingsSectionPlan(
    bool Connection, bool ConnectionHint, bool Credentials, bool SignInButtons, bool AccountHeading, bool FullSignInLabel,
    bool Access, bool Updates, bool Profiles, bool Approvals, bool Notifications);

public static class SettingsSections
{
    /// <summary>
    /// Advanced shows everything. Simple shows sign-in (browser, sign out, and the saved username + password so a session that expired can be
    /// renewed), Appearance and the view choice. A new installation (<paramref name="setupNeeded"/>: no usable dashboard address, or no profile
    /// chosen) also shows the address and Profiles in Simple, so the first-run screen is never a dead end; once set up they are hidden again.
    /// </summary>
    public static SettingsSectionPlan For(bool advanced, bool setupNeeded)
    {
        bool V(ExperienceFeature f) => Experience.IsVisible(f, advanced);
        bool Setup(ExperienceFeature f) => advanced || setupNeeded || V(f);
        return new SettingsSectionPlan(
            Connection: Setup(ExperienceFeature.SettingsConnection),
            ConnectionHint: Setup(ExperienceFeature.SettingsConnection),
            Credentials: true,
            SignInButtons: true,
            AccountHeading: !advanced,
            FullSignInLabel: advanced || setupNeeded,
            Access: V(ExperienceFeature.SettingsAccess),
            Updates: V(ExperienceFeature.SettingsUpdates),
            Profiles: Setup(ExperienceFeature.SettingsProfiles),
            Approvals: V(ExperienceFeature.SettingsApprovals),
            Notifications: V(ExperienceFeature.SettingsNotifications));
    }
}
