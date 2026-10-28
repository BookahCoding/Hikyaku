using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>One result row in the Inbox.</summary>
public sealed class JobResultVM : Observable
{
    private bool _expanded;

    public JobResultVM(JobResult result, string profileLabel, bool showProfile, ICommand open, ICommand hide)
    {
        Result = result;
        ProfileLabel = profileLabel;
        ProfileVisibility = showProfile ? Visibility.Visible : Visibility.Collapsed;
        OpenCommand = open;
        HideCommand = hide;
    }

    public JobResult Result { get; }
    public string Title => Result.Title;
    public string ProfileLabel { get; }
    public Visibility ProfileVisibility { get; }
    public string Preview => Result.Preview.Length > 0 ? Result.Preview : "No preview";
    public string When => JobInboxText.Ago(Result.At, DateTimeOffset.UtcNow);
    public Visibility FailedVisibility => Result.Failed ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>A script-only run is an output document, not a chat: clicking it shows its whole preview instead of opening anything.</summary>
    public Visibility ScriptVisibility => Result.IsScriptOutput ? Visibility.Visible : Visibility.Collapsed;
    public bool IsExpanded { get => _expanded; set { Set(ref _expanded, value); Raise(nameof(PreviewWrapping)); Raise(nameof(PreviewTrimming)); } }
    public TextWrapping PreviewWrapping => _expanded ? TextWrapping.Wrap : TextWrapping.NoWrap;
    public TextTrimming PreviewTrimming => _expanded ? TextTrimming.None : TextTrimming.CharacterEllipsis;
    public ICommand OpenCommand { get; }
    public ICommand HideCommand { get; }
}

/// <summary>One folder of the Inbox's Folders layout (failed runs, a group, or a job).</summary>
public sealed class InboxFolderVM(InboxFolder folder, ICommand open, ICommand? moveToGroup)
{
    public InboxFolder Folder { get; } = folder;
    public string Name => Folder.Name;
    public string Count => Folder.Count == 1 ? "1 result" : $"{Folder.Count} results";
    public string When => JobInboxText.Ago(Folder.NewestAt, DateTimeOffset.UtcNow);
    public string Preview => Folder.NewestPreview;
    public string Glyph => Folder.Kind switch { InboxFolderKind.Failed => "", InboxFolderKind.Group => "", _ => "" };
    public ICommand OpenCommand { get; } = open;
    public ICommand? MoveToGroupCommand { get; } = moveToGroup;
    public Visibility MoveVisibility => MoveToGroupCommand is null ? Visibility.Collapsed : Visibility.Visible;
}

public static class JobInboxText
{
    /// <summary>"just now", "5 min ago", "3 h ago", "yesterday", "4 days ago", or the date.</summary>
    public static string Ago(long unixSeconds, DateTimeOffset now)
    {
        if (unixSeconds <= 0) return "";
        var at = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var d = now - at;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
        if (d < TimeSpan.FromHours(24)) return $"{(int)d.TotalHours} h ago";
        if (d < TimeSpan.FromHours(48)) return "yesterday";
        if (d < TimeSpan.FromDays(7)) return $"{(int)d.TotalDays} days ago";
        return at.ToLocalTime().ToString("d MMM yyyy");
    }
}

/// <summary>
/// The Inbox page (Stage 42, option B): results of the scheduled jobs of the profiles ticked here, read from the dashboard's cron
/// routes. It follows the profile switcher, or shows every ticked profile with "All my profiles". Stage 43: it opens on folders
/// like the Android app (Failed, the groups you make, then each job), a job can be put in a group, and the plain list is one
/// click away. An agent run opens as its chat; a script-only run is an output document and only expands in place.
/// "Hide this job" removes a job's results from this PC's Inbox only.
/// </summary>
public sealed partial class MainViewModel
{
    private JobInboxPrefs? _jobInboxPrefs;
    private bool _jobInboxAll;
    private bool _jobInboxLoading;
    private int _jobInboxGeneration;
    private string _jobInboxStatus = "";
    private int _hiddenJobs;
    private IReadOnlyList<JobResult> _jobResultsAll = [];
    private bool _jobInboxShowAll;               // false: the Folders layout (the default, as in the Android app)
    private string? _openInboxFolder;
    private InboxGroups _jobInboxGroups = InboxGroups.Empty;
    private bool _jobInboxGroupsLoaded;

    public ObservableCollection<JobResultVM> JobResults { get; } = new();
    public ObservableCollection<InboxFolderVM> JobInboxFolders { get; } = new();

    public void AttachJobInbox(JobInboxPrefs prefs) => _jobInboxPrefs = prefs;

    public string JobInboxStatus { get => _jobInboxStatus; private set { Set(ref _jobInboxStatus, value); Raise(nameof(JobInboxStatusVisibility)); } }
    public Visibility JobInboxStatusVisibility => _jobInboxStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>"All my profiles": every ticked profile at once (offered only when more than one is ticked).</summary>
    public bool JobInboxAllProfiles
    {
        get => _jobInboxAll;
        set { if (_jobInboxAll == value) return; _jobInboxAll = value; Raise(); _ = RefreshJobInboxAsync(); }
    }
    public Visibility JobInboxAllToggleVisibility => ProfileChoices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The plain list of every result instead of folders.</summary>
    public bool JobInboxShowAllResults
    {
        get => _jobInboxShowAll;
        set { if (_jobInboxShowAll == value) return; _jobInboxShowAll = value; _openInboxFolder = null; Raise(); RebuildJobInbox(); }
    }

    public bool InFolderList => !_jobInboxShowAll && _openInboxFolder is null;
    public Visibility JobInboxFoldersVisibility => InFolderList ? Visibility.Visible : Visibility.Collapsed;
    public Visibility JobInboxResultsVisibility => InFolderList ? Visibility.Collapsed : Visibility.Visible;
    public Visibility JobInboxBackVisibility => _openInboxFolder is null ? Visibility.Collapsed : Visibility.Visible;
    public string JobInboxTitle => _openInboxFolder is { } key
        ? JobInboxFolders.FirstOrDefault(f => f.Folder.Key == key)?.Name ?? InboxFolders.Of(_jobResultsAll, _jobInboxGroups).FirstOrDefault(f => f.Key == key)?.Name ?? "Inbox"
        : "Inbox";

    public string HiddenJobsNote => _hiddenJobs == 1 ? "1 job is hidden." : $"{_hiddenJobs} jobs are hidden.";
    public Visibility HiddenJobsVisibility => _hiddenJobs > 0 ? Visibility.Visible : Visibility.Collapsed;

    private ICommand? _refreshJobInbox, _showHiddenJobs, _backToFolders;
    public ICommand RefreshJobInboxCommand => _refreshJobInbox ??= new RelayCommand(_ => _ = RefreshJobInboxAsync(), _ => !_jobInboxLoading);
    public ICommand ShowHiddenJobsCommand => _showHiddenJobs ??= new RelayCommand(_ => _ = ShowHiddenJobsAsync());
    public ICommand BackToInboxFoldersCommand => _backToFolders ??= new RelayCommand(_ => { _openInboxFolder = null; RebuildJobInbox(); });

    public async Task RefreshJobInboxAsync()
    {
        if (_cronApi() is not { } api) { _jobResultsAll = []; RebuildJobInbox(); JobInboxStatus = "Connect to a gateway to see your scheduled job results."; return; }
        if (ActiveProfileKey() is not { } active) { _jobResultsAll = []; RebuildJobInbox(); JobInboxStatus = "Choose a profile in Settings first."; return; }
        var all = _jobInboxAll && ProfileChoices.Count > 1;
        var generation = ++_jobInboxGeneration;
        _jobInboxLoading = true;
        (_refreshJobInbox as RelayCommand)?.Refresh();
        JobInboxStatus = "Loading…";
        try
        {
            await LoadInboxGroupsAsync().ConfigureAwait(true);
            var gateway = _gatewayId;
            var load = await JobInbox.LoadAsync(
                ct => api.JobsAsync(active.ProfileId, all, ct),
                (job, ct) => api.RunsAsync(job, JobInbox.RunsPerJob, ct),
                active.ProfileId, all, p => _gate.Allow(Surface.Inbox, new ProfileKey(gateway, p)),
                _jobInboxPrefs?.Hidden ?? new HashSet<string>(), default).ConfigureAwait(true);
            if (generation != _jobInboxGeneration) return;                       // a newer refresh or a profile switch won
            _jobResultsAll = load.Results;
            _hiddenJobs = load.HiddenJobs;
            RebuildJobInbox();
            JobInboxStatus = load.Results.Count == 0
                ? (load.Problems.Count > 0 ? "Couldn't load the results: " + load.Problems[0] : "No scheduled job results yet.")
                : load.Problems.Count > 0 ? $"Some jobs could not be read ({load.Problems.Count})." : "";
        }
        catch (Exception ex)
        {
            if (generation != _jobInboxGeneration) return;
            JobInboxStatus = ex is CronException { Failure: CronFailure.Unsupported }
                ? "This gateway doesn't offer scheduled jobs to the app."
                : "Couldn't load the results: " + ex.Message;
        }
        finally
        {
            if (generation == _jobInboxGeneration) { _jobInboxLoading = false; (_refreshJobInbox as RelayCommand)?.Refresh(); }
        }
    }

    private async Task LoadInboxGroupsAsync()
    {
        if (_jobInboxGroupsLoaded || _groupsStore() is not { } store) return;
        try { _jobInboxGroups = await store.LoadAsync(_gatewayId).ConfigureAwait(true); }
        catch (Exception) { _jobInboxGroups = InboxGroups.Empty; }                  // unreadable groups: everything shows as its own job
        _jobInboxGroupsLoaded = true;
    }

    /// <summary>Redraws the folders or the results from what was last loaded (no request).</summary>
    private void RebuildJobInbox()
    {
        var showProfile = _jobInboxAll && ProfileChoices.Count > 1;
        JobInboxFolders.Clear();
        foreach (var f in InboxFolders.Of(_jobResultsAll, _jobInboxGroups))
        {
            var key = f.Key;
            var jobKey = f.JobKey;
            ICommand? move = f.Kind == InboxFolderKind.Failed ? null : new RelayCommand(_ => _ = MoveToGroupAsync(f));
            JobInboxFolders.Add(new InboxFolderVM(f, new RelayCommand(_ => { _openInboxFolder = key; RebuildJobInbox(); }), move));
        }
        if (_openInboxFolder is { } open && JobInboxFolders.All(f => f.Folder.Key != open)) _openInboxFolder = null;   // the folder emptied
        JobResults.Clear();
        if (!InFolderList)
        {
            var shown = _openInboxFolder is { } k ? InboxFolders.In(_jobResultsAll, k, _jobInboxGroups) : _jobResultsAll;
            foreach (var r in shown) JobResults.Add(ToRow(r, showProfile));
        }
        Raise(nameof(InFolderList)); Raise(nameof(JobInboxFoldersVisibility)); Raise(nameof(JobInboxResultsVisibility));
        Raise(nameof(JobInboxBackVisibility)); Raise(nameof(JobInboxTitle));
        Raise(nameof(HiddenJobsNote)); Raise(nameof(HiddenJobsVisibility));
    }

    private JobResultVM ToRow(JobResult r, bool showProfile)
    {
        var key = new ProfileKey(_gatewayId, r.Profile);
        var label = ActiveProfile.Label(key, _profileNames);
        JobResultVM? row = null;
        row = new JobResultVM(r, label, showProfile,
            new RelayCommand(_ => { if (r.IsScriptOutput) row!.IsExpanded = !row.IsExpanded; else _ = OpenJobResultAsync(r); }),
            new RelayCommand(_ => _ = HideJobAsync(r)));
        return row;
    }

    /// <summary>Puts a job (or every job of a group) into a named group of this PC's Inbox; an empty name takes it out again.</summary>
    private async Task MoveToGroupAsync(InboxFolder folder)
    {
        if (PromptText is null) return;
        var current = folder.Kind == InboxFolderKind.Group ? folder.Name : (folder.JobKey is { } jk ? _jobInboxGroups.GroupOf(jk) ?? "" : "");
        var name = PromptText("Group name (leave empty to take it out of its group)", current);
        if (name is null) return;                                                    // cancelled
        var jobs = folder.Kind == InboxFolderKind.Group
            ? _jobResultsAll.Where(r => _jobInboxGroups.GroupOf(r.JobKey) == folder.Name).Select(r => r.JobKey).Distinct().ToList()
            : folder.JobKey is { } only ? [only] : [];
        var groups = _jobInboxGroups;
        foreach (var j in jobs) groups = groups.WithJob(j, name.Trim().Length == 0 ? null : name.Trim());
        _jobInboxGroups = groups;
        try { if (_groupsStore() is { } store) await store.SaveAsync(_gatewayId, groups).ConfigureAwait(true); }
        catch (Exception ex) { JobInboxStatus = "Couldn't remember the group: " + ex.Message; }
        _openInboxFolder = null;
        RebuildJobInbox();
    }

    /// <summary>Opens the chat an agent run produced, under the job's own profile (only if that profile is ticked here).</summary>
    private async Task OpenJobResultAsync(JobResult r)
    {
        if (r.IsScriptOutput) return;                                                // no chat behind it (404 from the sessions routes)
        var key = new ProfileKey(_gatewayId, r.Profile);
        if (!_gate.Allow(Surface.Conversation, key)) return;
        // Named after the job and when it ran (the gateway's own title for a run is a raw "COMPLETED · # Cron Job: …" line).
        var when = r.At > 0 ? " · " + DateTimeOffset.FromUnixTimeSeconds(r.At).ToLocalTime().ToString("d MMM HH:mm") : "";
        await OpenStoredAsync(r.Run.Id, r.Title + when, key, select: true).ConfigureAwait(true);
    }

    private async Task HideJobAsync(JobResult r)
    {
        if (_jobInboxPrefs is null) return;
        try { await _jobInboxPrefs.HideAsync(r.JobKey).ConfigureAwait(true); }
        catch (Exception ex) { JobInboxStatus = "Couldn't remember that: " + ex.Message; return; }
        _jobResultsAll = _jobResultsAll.Where(x => x.JobKey != r.JobKey).ToList();
        _hiddenJobs++;
        RebuildJobInbox();
    }

    private async Task ShowHiddenJobsAsync()
    {
        if (_jobInboxPrefs is null) return;
        try { await _jobInboxPrefs.ShowAllAsync().ConfigureAwait(true); }
        catch (Exception ex) { JobInboxStatus = "Couldn't change that: " + ex.Message; return; }
        await RefreshJobInboxAsync().ConfigureAwait(true);
    }
}
