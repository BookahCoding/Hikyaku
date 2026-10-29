using System.Windows;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>
/// Simple / Advanced (<see cref="Experience"/> is the one table): a visibility choice only. Simple is for someone with little computer
/// experience (chats, the Inbox, Needs you); Advanced is for managing Hermes. It never changes what is allowed (approvals, YOLO policy,
/// folder grants, terminal and network permissions are unaffected) and it is not a lock. A risk already in effect is never hidden: a
/// chat with YOLO on, a folder shared with Hermes, or a live terminal each show in Simple too, with the way to switch it off.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _advanced = true;   // until settings are applied (tests and first paint); new installs start Simple

    /// <summary>Raised when the user flips the mode, so the choice can be saved.</summary>
    public event Action<bool>? ExperienceChanged;

    public bool IsAdvanced
    {
        get => _advanced;
        set
        {
            if (_advanced == value) return;
            _advanced = value;
            RaiseExperienceSurfaces();
            ExperienceChanged?.Invoke(value);
        }
    }

    /// <summary>Applies the saved mode without announcing it as a user change.</summary>
    public void ApplyExperience(bool advanced)
    {
        if (_advanced == advanced) return;
        _advanced = advanced;
        RaiseExperienceSurfaces();
    }

    public bool Shows(ExperienceFeature feature) => Experience.IsVisible(feature, _advanced);
    private Visibility Vis(ExperienceFeature feature) => Shows(feature) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Which feature a sidebar page belongs to.</summary>
    public static ExperienceFeature FeatureOf(MainNav nav) => nav switch
    {
        MainNav.Chats => ExperienceFeature.Chats,
        MainNav.Bots => ExperienceFeature.Bots,
        MainNav.Inbox => ExperienceFeature.Inbox,
        MainNav.NeedsYou => ExperienceFeature.NeedsYou,
        MainNav.Projects => ExperienceFeature.Projects,
        MainNav.Scheduled => ExperienceFeature.Scheduled,
        MainNav.Capabilities => ExperienceFeature.Capabilities,
        MainNav.Insights => ExperienceFeature.Insights,
        MainNav.Rooms => ExperienceFeature.Rooms,
        MainNav.Artifacts => ExperienceFeature.Artifacts,
        MainNav.Journey => ExperienceFeature.Journey,
        _ => throw new ArgumentOutOfRangeException(nameof(nav), nav, "This page has no Simple / Advanced classification.")
    };

    // Stage 45: a newer version was found at start-up; the button opens Settings → Updates (where updates live, Advanced)
    private string _updateNotice = "";
    public string UpdateNotice { get => _updateNotice; set { Set(ref _updateNotice, value); Raise(nameof(UpdateNoticeVisibility)); } }
    public Visibility UpdateNoticeVisibility => _updateNotice.Length > 0 && Shows(ExperienceFeature.SettingsUpdates) ? Visibility.Visible : Visibility.Collapsed;

    // Stage 44: the agent's work while it answers
    public Visibility ReasoningDetailsVisibility => Vis(ExperienceFeature.ReasoningDetails);
    public Visibility PlanDetailsVisibility => Vis(ExperienceFeature.AgentPlan);
    public Visibility NoticeDetailsVisibility => Vis(ExperienceFeature.AgentNotices);
    /// <summary>The detailed activity line (tool names, the gateway's words) in Advanced; the plain one in Simple.</summary>
    public Visibility AdvancedActivityVisibility => Vis(ExperienceFeature.ToolDetails);
    public Visibility SimpleActivityVisibility => Shows(ExperienceFeature.ToolDetails) ? Visibility.Collapsed : Visibility.Visible;

    // sidebar rows
    public Visibility BotsNavVisibility => Vis(ExperienceFeature.Bots);
    public Visibility RoomsNavVisibility => Vis(ExperienceFeature.Rooms);
    public Visibility CapabilitiesNavVisibility => Vis(ExperienceFeature.Capabilities);
    public Visibility JourneyNavVisibility => Vis(ExperienceFeature.Journey);
    public Visibility InsightsNavVisibility => Vis(ExperienceFeature.Insights);
    public Visibility ProjectsNavVisibility => Vis(ExperienceFeature.Projects);
    public Visibility ScheduledNavVisibility => Vis(ExperienceFeature.Scheduled);
    public Visibility ArtifactsNavVisibility => Vis(ExperienceFeature.Artifacts);

    // chat chrome
    public Visibility ModelChipVisibility => Vis(ExperienceFeature.ModelChip);
    public Visibility BranchVisibility => Vis(ExperienceFeature.BranchChat);
    public Visibility ReasoningChipVisibility => Vis(ExperienceFeature.ReasoningChip);
    public Visibility FastChipVisibility => Vis(ExperienceFeature.FastChip);
    public Visibility SessionFiltersVisibility => Vis(ExperienceFeature.SessionFilters);
    /// <summary>Raw tool-call lines and tool disclosures.</summary>
    public Visibility AdvancedToolsVisibility => Vis(ExperienceFeature.ToolDetails);

    /// <summary>The YOLO control is technical, but YOLO being ON is a risk state and stays visible in Simple.</summary>
    public Visibility YoloVisibility => _advanced || SelectedConversation?.YoloEnabled == true ? Visibility.Visible : Visibility.Collapsed;

    public string ExperienceToggleLabel => _advanced ? "Use simple view" : "Show advanced tools";

    // settings sections (the window binds to these)
    public Visibility SettingsConnectionVisibility => Vis(ExperienceFeature.SettingsConnection);

    // Inbox: Simple shows the results; following jobs, choosing which to notify about, and the job / group filters are set-up work
    public Visibility InboxSetupVisibility => Vis(ExperienceFeature.InboxSetup);
    public Visibility InboxSimpleIntroVisibility => Shows(ExperienceFeature.InboxSetup) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// What is already in effect on this PC that Simple must not hide: a folder shared with Hermes, a live terminal. (YOLO on has its own
    /// chip.) Shown only in Simple: Advanced shows both on their own pages. Empty when there is nothing.
    /// </summary>
    public string RiskNotice
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(CurrentProjectPathHint))
                parts.Add("A folder on this computer is shared with Hermes (" + System.IO.Path.GetFileName(CurrentProjectPathHint.TrimEnd('\\', '/')) + ").");
            if (BridgeShares.Count > 0)
                parts.Add("A folder on this computer is shared with Hermes.");
            if (_terminal?.ActiveSession is not null) parts.Add("A terminal is open on this computer.");
            if (_approvalsOff) parts.Add("Hermes is set to run commands without asking you first.");
            if (_riskActionError.Length > 0) parts.Add(_riskActionError);
            return string.Join(" ", parts);
        }
    }

    public Visibility RiskNoticeVisibility => !_advanced && RiskNotice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StopSharingVisibility =>
        !string.IsNullOrWhiteSpace(CurrentProjectPathHint) || BridgeShares.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CloseTerminalNoticeVisibility => _terminal?.ActiveSession is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AskFirstVisibility => _approvalsOff ? Visibility.Visible : Visibility.Collapsed;

    internal void RaiseRiskNotice()
    {
        Raise(nameof(RiskNotice)); Raise(nameof(RiskNoticeVisibility));
        Raise(nameof(StopSharingVisibility)); Raise(nameof(CloseTerminalNoticeVisibility)); Raise(nameof(AskFirstVisibility));
    }

    /// <summary>Everything that depends on the mode, in one place; leaving Advanced while on (or inside) a hidden page returns to Chats.</summary>
    private void RaiseExperienceSurfaces()
    {
        Raise(nameof(IsAdvanced));
        RaiseIdentity();
        foreach (var n in new[]
        {
            nameof(AdvancedToolsVisibility), nameof(YoloVisibility), nameof(ModelMenuVisibility), nameof(ExperienceToggleLabel),
            nameof(BotsNavVisibility), nameof(RoomsNavVisibility), nameof(CapabilitiesNavVisibility), nameof(JourneyNavVisibility),
            nameof(InsightsNavVisibility), nameof(ProjectsNavVisibility), nameof(ScheduledNavVisibility), nameof(ArtifactsNavVisibility),
            nameof(ModelChipVisibility), nameof(BranchVisibility), nameof(ReasoningChipVisibility), nameof(FastChipVisibility),
            nameof(SessionFiltersVisibility), nameof(SettingsConnectionVisibility), nameof(VoiceStatusVisibility),
            nameof(InboxSetupVisibility), nameof(InboxSimpleIntroVisibility), nameof(ConfigEditorVisibility), nameof(McpEditVisibility),
            nameof(ReasoningDetailsVisibility), nameof(PlanDetailsVisibility), nameof(NoticeDetailsVisibility), nameof(UpdateNoticeVisibility), nameof(VoiceChatButtonVisibility), nameof(JobInboxPluginHintVisibility), nameof(AdvancedActivityVisibility), nameof(SimpleActivityVisibility),
            nameof(FolderKeyFilesVisibility), nameof(FolderBridgePluginHint), nameof(FolderBridgePluginHintVisibility),
            nameof(NeedsYouNavLabel), nameof(NeedsYouNavToolTip), nameof(NeedsYouFilterLabel), nameof(ArtifactsNavLabel), nameof(InboxNavToolTip)
        }) Raise(n);
        RaiseRiskNotice();
        RaiseStatus();
        LeaveHiddenPage();
        if (!_advanced) _ = RefreshApprovalsRiskAsync();      // Simple must show "approvals are Off" if that is the case on this profile
    }

    private void LeaveHiddenPage()
    {
        if (!Shows(FeatureOf(_nav))) Nav = MainNav.Chats;
        if (_advanced) return;
        // A filter whose chip Simple hides must not stay switched on, or the chat list would be stuck filtered with no way to undo it.
        if (RunningFilterOn) ToggleRunningFilterCommand?.Execute(null);
        if (ArchivedView) ToggleArchivedViewCommand?.Execute(null);
        // Anything opened inside an Advanced page is closed so nothing hidden is left half open.
        CloseJourneyNode();
        CloseHubDetail();
        ClearArtifactPreviewIfAny();
        CloseTerminalDrawerIfOpenKeepingSession();
    }
}
