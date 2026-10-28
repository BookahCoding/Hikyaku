using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

public sealed class PaletteEntryVM(PaletteItem item) : Observable
{
    private bool _selected;
    public string Id { get; } = item.Id;
    public string Title { get; } = item.Title;
    public string Group { get; } = item.Group;
    public string Hint { get; } = item.Hint;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>
/// Command palette (Ctrl+K): type to find a place to go or a view action. It only offers navigation and reversible view
/// actions: nothing that sends a message, deletes, grants access, publishes or changes a permission is ever listed, so it
/// can be opened and used without consequences. Commands that cannot run right now are not listed.
/// </summary>
public sealed partial class MainViewModel
{
    private sealed record PaletteAction(PaletteItem Item, Action Run);

    private readonly List<PaletteAction> _paletteActions = new();
    private bool _paletteOpen;
    private string _paletteQuery = "";
    private int _paletteIndex;
    private string _paletteStatus = "";

    /// <summary>Window-level actions the view model cannot perform itself.</summary>
    public event Action? PaletteOpenSettingsRequested;
    public event Action? PaletteToggleSidebarRequested;
    public event Action? PaletteSearchSessionsRequested;
    public event Action? PaletteShowShortcutsRequested;

    public ObservableCollection<PaletteEntryVM> PaletteResults { get; } = new();

    public ICommand? OpenPaletteCommand { get; private set; }
    public ICommand? ClosePaletteCommand { get; private set; }
    public ICommand? RunPaletteSelectedCommand { get; private set; }
    public ICommand? RunPaletteEntryCommand { get; private set; }
    public ICommand? MovePaletteSelectionCommand { get; private set; }

    public bool IsPaletteOpen => _paletteOpen;
    public Visibility PaletteVisibility => _paletteOpen ? Visibility.Visible : Visibility.Collapsed;
    public string PaletteStatus => _paletteStatus;
    public int PaletteSelectedIndex => _paletteIndex;

    public string PaletteQuery
    {
        get => _paletteQuery;
        set
        {
            if (_paletteQuery == value) return;
            _paletteQuery = value;
            Raise();
            RefreshPaletteResults();
        }
    }

    private void InitPalette()
    {
        OpenPaletteCommand = new RelayCommand(_ => OpenPalette());
        ClosePaletteCommand = new RelayCommand(_ => ClosePalette());
        RunPaletteSelectedCommand = new RelayCommand(_ => RunPaletteSelected(), _ => PaletteResults.Count > 0);
        RunPaletteEntryCommand = new RelayCommand(p => { if (p is PaletteEntryVM e) RunPaletteEntry(e); });
        MovePaletteSelectionCommand = new RelayCommand(p => MovePaletteSelection(p is int d ? d : p is string s && int.TryParse(s, out var n) ? n : 0));
    }

    private void OpenPalette()
    {
        BuildPaletteActions();
        _paletteQuery = "";
        Raise(nameof(PaletteQuery));
        _paletteOpen = true;
        RefreshPaletteResults();
        Raise(nameof(IsPaletteOpen));
        Raise(nameof(PaletteVisibility));
    }

    private void ClosePalette()
    {
        if (!_paletteOpen) return;
        _paletteOpen = false;
        PaletteResults.Clear();
        _paletteActions.Clear();
        Raise(nameof(IsPaletteOpen));
        Raise(nameof(PaletteVisibility));
    }

    private void RefreshPaletteResults()
    {
        if (!_paletteOpen) return;
        var found = PaletteSearch.Filter(_paletteActions.Select(a => a.Item), _paletteQuery);
        PaletteResults.Clear();
        foreach (var item in found) PaletteResults.Add(new PaletteEntryVM(item));
        SelectPalette(0);
        _paletteStatus = PaletteResults.Count == 0 ? "No matching commands" : PaletteResults.Count == 1 ? "1 result" : $"{PaletteResults.Count} results";
        Raise(nameof(PaletteStatus));
        (RunPaletteSelectedCommand as RelayCommand)?.Refresh();
    }

    private void MovePaletteSelection(int delta)
    {
        if (PaletteResults.Count == 0) return;
        SelectPalette((_paletteIndex + delta + PaletteResults.Count) % PaletteResults.Count);   // wraps
    }

    private void SelectPalette(int index)
    {
        _paletteIndex = PaletteResults.Count == 0 ? 0 : Math.Clamp(index, 0, PaletteResults.Count - 1);
        for (var i = 0; i < PaletteResults.Count; i++) PaletteResults[i].IsSelected = i == _paletteIndex;
        Raise(nameof(PaletteSelectedIndex));
    }

    private void RunPaletteSelected()
    {
        if (_paletteIndex >= 0 && _paletteIndex < PaletteResults.Count) RunPaletteEntry(PaletteResults[_paletteIndex]);
    }

    private void RunPaletteEntry(PaletteEntryVM entry)
    {
        var action = _paletteActions.FirstOrDefault(a => a.Item.Id == entry.Id);
        ClosePalette();                                  // close first so focus and any dialog belong to the real destination
        if (action is null) return;
        try { action.Run(); }
        catch (Exception ex) { SetRewindNotice("That command failed: " + ex.Message); }
    }

    private void BuildPaletteActions()
    {
        _paletteActions.Clear();
        void Add(string id, string title, string group, Action run, bool enabled = true, string hint = "", params string[] keywords)
        {
            if (enabled) _paletteActions.Add(new PaletteAction(new PaletteItem(id, title, group, hint, keywords), run));
        }
        static bool Can(ICommand? c) => c is not null && c.CanExecute(null);

        Add("chat.new", "New chat", "Chats", NewChat, hint: Shortcuts.DisplayFor(ShortcutAction.NewChat), keywords: ["start", "compose", "conversation"]);
        Add("nav.chats", "Open Chats", "Go to", () => ShowChatsCommand.Execute(null));
        Add("nav.bots", "Open Bots", "Go to", () => ShowBotsCommand.Execute(null), enabled: Shows(ExperienceFeature.Bots), keywords: ["rooms", "roster"]);
        Add("nav.inbox", "Open Inbox", "Go to", () => ShowInboxCommand.Execute(null), keywords: ["scheduled", "cron", "jobs"]);
        Add("nav.scheduled", "Open Scheduled jobs", "Go to", () => ShowScheduledCommand!.Execute(null), enabled: Shows(ExperienceFeature.Scheduled), hint: "Cron: pause, run, edit", keywords: ["cron", "routines", "schedule", "jobs", "automations"]);
        Add("nav.journey", "Open Journey", "Go to", () => ShowJourneyCommand!.Execute(null), enabled: Shows(ExperienceFeature.Journey), hint: "Memory graph: what the agent learned", keywords: ["memory", "memory graph", "learning", "skills", "learned", "star map"]);
        Add("nav.artifacts", "Open Artifacts", "Go to", () => ShowArtifactsCommand!.Execute(null), enabled: Shows(ExperienceFeature.Artifacts), hint: "Browse generated outputs", keywords: ["files", "images", "pictures", "links", "generated", "outputs", "downloads"]);
        Add("nav.rooms", "Open Rooms", "Go to", () => ShowRoomsCommand!.Execute(null), enabled: Shows(ExperienceFeature.Rooms), keywords: ["group", "groups", "bots", "mention"]);
        Add("nav.capabilities", "Open Capabilities", "Go to", () => ShowCapabilitiesCommand!.Execute(null), enabled: Shows(ExperienceFeature.Capabilities), keywords: ["skills", "tools", "toolsets", "mcp", "servers"]);
        Add("nav.insights", "Open Insights", "Go to", () => ShowInsightsCommand!.Execute(null), enabled: Shows(ExperienceFeature.Insights), keywords: ["usage", "tokens", "cost", "analytics", "stats"]);
        Add("nav.needs-you", "Open Needs you", "Go to", () => ShowNeedsYouCommand.Execute(null), keywords: ["approvals", "questions", "waiting"]);
        Add("nav.projects", "Open Projects", "Go to", () => Nav = MainNav.Projects, enabled: Shows(ExperienceFeature.Projects), keywords: ["folders", "files", "git"]);
        Add("app.settings", "Open Settings", "App", () => PaletteOpenSettingsRequested?.Invoke(), hint: Shortcuts.DisplayFor(ShortcutAction.Settings), keywords: ["preferences", "options", "connection", "updates", "notifications"]);
        Add("app.sidebar", "Show or hide the sidebar", "App", () => PaletteToggleSidebarRequested?.Invoke(), hint: Shortcuts.DisplayFor(ShortcutAction.ToggleSidebar), keywords: ["collapse", "expand"]);
        Add("chats.search", "Search sessions", "Chats", () => PaletteSearchSessionsRequested?.Invoke(), hint: Shortcuts.DisplayFor(ShortcutAction.SearchSessions), keywords: ["find", "history"]);
        Add("app.shortcuts", "Keyboard shortcuts", "App", () => PaletteShowShortcutsRequested?.Invoke(), hint: Shortcuts.DisplayFor(ShortcutAction.ShowShortcuts), keywords: ["keys", "hotkeys", "bindings"]);
        Add("app.experience", IsAdvanced ? "Use simple view" : "Show advanced tools", "App", () => IsAdvanced = !IsAdvanced,
            hint: "Changes what is shown; never what is allowed", keywords: ["simple", "advanced", "experience", "tools"]);
        Add("chat.find", "Find in chat", "Chat", () => OpenFindCommand!.Execute(null), Can(OpenFindCommand), Shortcuts.DisplayFor(ShortcutAction.FindInChat), ["search"]);
        Add("chat.stop", "Stop the reply", "Chat", () => CancelCommand.Execute(null), Can(CancelCommand), keywords: ["cancel", "interrupt"]);
        Add("chat.branch", "Branch this chat", "Chat", () => BranchChatCommand!.Execute(null), Can(BranchChatCommand) && Shows(ExperienceFeature.BranchChat), "Copies it into a new chat", ["copy", "fork"]);
        Add("chat.earlier", "Load earlier messages", "Chat", () => LoadEarlierCommand!.Execute(null), Can(LoadEarlierCommand), keywords: ["history", "older"]);
        Add("inbox.refresh", "Refresh Inbox", "Inbox", () => RefreshInboxCommand.Execute(null), Can(RefreshInboxCommand));
        Add("sessions.refresh", "Refresh gateway sessions", "Chats", () => RefreshSessionsCommand.Execute(null), Can(RefreshSessionsCommand));
        Add("sessions.needs-you", _attention == AttentionFilter.NeedsYou ? "Show all chats" : "Show only chats that need you", "Chats", () => SetAttentionFilterCommand!.Execute("NeedsYou"),
            keywords: ["attention", "waiting", "unread", "filter"]);
        Add("sessions.archived", ArchivedView ? "Show recent chats" : "Show archived chats", "Chats", () => ToggleArchivedViewCommand!.Execute(null), enabled: Shows(ExperienceFeature.SessionFilters), keywords: ["archive", "history"]);
        Add("sessions.running", RunningFilterOn ? "Show all chats" : "Show only running chats", "Chats", () => ToggleRunningFilterCommand!.Execute(null),
            enabled: Shows(ExperienceFeature.SessionFilters), keywords: ["running", "filter", "active"]);

        // Places, by name: chats open on this device and the listed gateway sessions (already limited to the selected profile).
        var n = 0;
        foreach (var c in Conversations.ToList())
        {
            var convo = c;
            Add("chat:" + n++, "Go to chat: " + convo.Title, "Chats", () => { Nav = MainNav.Chats; SelectedConversation = convo; }, true, convo.Profile.ProfileId);
        }
        foreach (var row in SessionRows.ToList())
            Add("session:" + row.Summary.Id, "Open session: " + row.Title, "Sessions", () => _ = OpenSessionAsync(row), true, "Gateway session");
    }
}
