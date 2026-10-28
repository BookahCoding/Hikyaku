using System.Collections.ObjectModel;
using System.Windows;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>One entry in the sidebar's profile switcher (a ticked profile, by its gateway display name).</summary>
public sealed class ProfileChoiceVM(ProfileKey key, string label)
{
    public ProfileKey Key { get; } = key;
    public string Label { get; } = label;
    public override string ToString() => Label;
}

/// <summary>
/// The profile switcher (sidebar, under the app name). Which ticked profile the app works with: New chat, the gateway chat
/// list and every page follow it. Before this, the alphabetically first profile id was used everywhere and could not be
/// changed, so New chat went to a bot instead of the main agent. Shown only when more than one profile is ticked.
/// </summary>
public sealed partial class MainViewModel
{
    private ProfileKey? _activeProfile;
    private string? _savedActiveProfileId;
    private IReadOnlyDictionary<string, string> _profileNames = new Dictionary<string, string>();

    public ObservableCollection<ProfileChoiceVM> ProfileChoices { get; } = new();

    /// <summary>Raised with the profile id when the person picks a profile, so the window can remember it.</summary>
    public event Action<string>? ActiveProfileChosen;

    public ProfileChoiceVM? SelectedProfileChoice
    {
        get => ProfileChoices.FirstOrDefault(c => c.Key == ActiveProfileKey());
        set { if (value is not null) SwitchProfile(value.Key); }
    }

    public string CurrentProfileLabel => ActiveProfileKey() is { } p ? ActiveProfile.Label(p, _profileNames) : "";

    public Visibility ProfileSwitcherVisibility =>
        ProfileChoices.Count > 1 && Shows(ExperienceFeature.ProfileSwitcher) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The profile the app works with now: always a ticked one (re-picked if the old one was unticked).</summary>
    public ProfileKey? ActiveProfileKey()
    {
        var selected = _selection.Selected;
        if (_activeProfile is { } p && selected.Contains(p)) return p;
        return ActiveProfile.Pick(selected, _activeProfile?.ProfileId ?? _savedActiveProfileId);
    }

    /// <summary>The profile a gateway event belongs to: the open chat with that session, else the chosen profile.</summary>
    public ProfileKey? ProfileForSession(string? sessionId) =>
        (sessionId is { Length: > 0 } id ? _all.FirstOrDefault(c => c.SessionId == id || c.StoredSessionId == id)?.Profile : null)
        ?? ActiveProfileKey();

    /// <summary>Called once the saved settings are known.</summary>
    public void InitActiveProfile(string? savedProfileId)
    {
        _savedActiveProfileId = savedProfileId;
        _activeProfile = null;
        RebuildProfileChoices();
    }

    /// <summary>Display names from the gateway's profile list (profile id → name).</summary>
    public void SetProfileNames(IReadOnlyDictionary<string, string> names)
    {
        _profileNames = names;
        RebuildProfileChoices();
    }

    private void RebuildProfileChoices()
    {
        var before = _activeProfile;
        var now = ActiveProfileKey();
        _activeProfile = now;
        ProfileChoices.Clear();
        foreach (var k in _selection.Selected.OrderBy(k => k.ProfileId == ActiveProfile.GatewayDefault ? 0 : 1)
                     .ThenBy(k => ActiveProfile.Label(k, _profileNames), StringComparer.CurrentCultureIgnoreCase))
            ProfileChoices.Add(new ProfileChoiceVM(k, ActiveProfile.Label(k, _profileNames)));
        Raise(nameof(ProfileSwitcherVisibility));
        Raise(nameof(JobInboxAllToggleVisibility));
        Raise(nameof(SelectedProfileChoice));
        Raise(nameof(CurrentProfileLabel));
        if (before is not null && before != now) OnActiveProfileChanged();
    }

    /// <summary>The person picked a profile in the switcher.</summary>
    public void SwitchProfile(ProfileKey key)
    {
        if (!_selection.Selected.Contains(key) || key == ActiveProfileKey()) return;
        _activeProfile = key;
        _savedActiveProfileId = key.ProfileId;
        ActiveProfileChosen?.Invoke(key.ProfileId);
        Raise(nameof(SelectedProfileChoice));
        Raise(nameof(CurrentProfileLabel));
        OnActiveProfileChanged();
    }

    private void OnActiveProfileChanged()
    {
        if (ActiveProfileKey() is { } p) _sessions()?.SetActiveProfile(p);
        _ = RefreshSessionsAsync();
        _ = RefreshApprovalsRiskAsync();
        _ = RefreshBridgeSharesAsync();
        // The open page shows the newly chosen profile's data (each page drops the old profile's rows first).
        switch (Nav)
        {
            case MainNav.Inbox: _ = RefreshJobInboxAsync(); break;
            case MainNav.Capabilities: _ = RefreshCapabilitiesAsync(); break;
            case MainNav.Scheduled: _ = RefreshCronAsync(); break;
            case MainNav.Insights: _ = RefreshInsightsAsync(); break;
            case MainNav.Journey: _ = RefreshJourneyAsync(); break;
            case MainNav.Bots: _ = RefreshBotsAsync(); break;
            case MainNav.Rooms: _ = RefreshRoomsAsync(); break;
            case MainNav.Projects: OnProjectsProfileChanged(); break;
        }
    }
}
