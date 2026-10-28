using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>
/// One result row in the Inbox. 0.7.0: it shows what the run found (the agent's answer or the script's output, fetched by
/// <see cref="JobRunDetails"/>), a status, an unread mark, and expands in place; an agent run can be asked about in a chat.
/// </summary>
public sealed class JobResultVM : Observable
{
    private bool _expanded, _unread, _detailLoaded;
    private RunText? _detail;
    private string _loadProblem = "";

    public JobResultVM(JobResult result, string profileLabel, bool showProfile, ICommand open, ICommand hide, ICommand? ask = null, bool unread = false)
    {
        Result = result;
        ProfileLabel = profileLabel;
        ProfileVisibility = showProfile ? Visibility.Visible : Visibility.Collapsed;
        OpenCommand = open;
        HideCommand = hide;
        AskCommand = ask ?? open;
        _unread = unread;
    }

    /// <summary>What is known before the result is fetched: a script run's preview without its header; an agent run's preview is only its prompt.</summary>
    private RunText Shown => _detail ?? (Result.IsScriptOutput ? CronRunText.Parse(Result.Preview) : new RunText(RunStatus.Ok, "", null));

    /// <summary>The first lines of what the run found.</summary>
    public string Excerpt
    {
        get
        {
            if (_detail is null && !Result.IsScriptOutput) return !_detailLoaded ? "Loading…" : _loadProblem.Length > 0 ? "" : "No answer yet";
            var shown = Shown;
            if (shown.Status == RunStatus.Silent) return "Nothing new";
            var excerpt = CronRunText.Excerpt(shown.Text);
            return excerpt.Length > 0 ? excerpt : shown.Reason ?? "No output";
        }
    }

    /// <summary>The whole result, as Markdown (a failed run leads with Hermes' reason).</summary>
    public string FullText => Shown is { Status: RunStatus.Failed, Reason: { } reason } failed ? $"**{reason}**\n\n{failed.Text}" : Shown.Text;

    public string StatusLabel => Result.Failed || Shown.Status == RunStatus.Failed ? "Failed" : "";
    public Visibility StatusVisibility => StatusLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>"Ask about this" shows on an opened agent result (a script run has no chat to ask in).</summary>
    public Visibility AskVisibility => !Result.IsScriptOutput && _expanded ? Visibility.Visible : Visibility.Collapsed;
    public ICommand AskCommand { get; }

    public bool IsUnread { get => _unread; set { Set(ref _unread, value); Raise(nameof(UnreadVisibility)); Raise(nameof(TitleWeight)); } }
    public Visibility UnreadVisibility => _unread ? Visibility.Visible : Visibility.Collapsed;
    public FontWeight TitleWeight => _unread ? FontWeights.Bold : FontWeights.SemiBold;

    public string LoadProblem => _loadProblem;
    public Visibility LoadProblemVisibility => _loadProblem.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool HasDetail => _detailLoaded;

    /// <summary>The fetched result (null when the run has none yet), or why it could not be fetched.</summary>
    public void SetDetail(RunText? detail, string problem = "")
    {
        _detail = detail;
        _detailLoaded = true;
        _loadProblem = problem;
        foreach (var n in new[] { nameof(Excerpt), nameof(FullText), nameof(StatusLabel), nameof(StatusVisibility), nameof(LoadProblem),
                                  nameof(LoadProblemVisibility), nameof(FullTextVisibility), nameof(FailedVisibility) })
            Raise(n);
    }

    public JobResult Result { get; }
    public string Title => Result.Title;
    public string ProfileLabel { get; }
    public Visibility ProfileVisibility { get; }
    public string Preview => Result.Preview.Length > 0 ? Result.Preview : "No preview";
    public string When => JobInboxText.Ago(Result.At, DateTimeOffset.UtcNow);
    public Visibility FailedVisibility => StatusLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>A script-only run is an output document, not a chat: clicking it shows its whole preview instead of opening anything.</summary>
    public Visibility ScriptVisibility => Result.IsScriptOutput ? Visibility.Visible : Visibility.Collapsed;
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            Set(ref _expanded, value);
            Raise(nameof(PreviewWrapping)); Raise(nameof(PreviewTrimming)); Raise(nameof(ExcerptVisibility)); Raise(nameof(FullTextVisibility)); Raise(nameof(AskVisibility));
        }
    }
    public Visibility ExcerptVisibility => _expanded && FullText.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FullTextVisibility => _expanded && FullText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public TextWrapping PreviewWrapping => _expanded ? TextWrapping.Wrap : TextWrapping.NoWrap;
    public TextTrimming PreviewTrimming => _expanded ? TextTrimming.None : TextTrimming.CharacterEllipsis;
    public ICommand OpenCommand { get; }
    public ICommand HideCommand { get; }
}

/// <summary>One folder of the Inbox's Folders layout (failed runs, a group, or a job), with how many of its results are unread.</summary>
public sealed class InboxFolderVM(InboxFolder folder, ICommand open, ICommand? moveToGroup, Func<int>? unread = null, Func<string>? preview = null)
    : Observable
{
    public InboxFolder Folder { get; } = folder;
    public string Name => Folder.Name;
    public string Count => (Folder.Count == 1 ? "1 result" : $"{Folder.Count} results") + (unread?.Invoke() is > 0 and var n ? $" · {n} new" : "");
    public string When => JobInboxText.Ago(Folder.NewestAt, DateTimeOffset.UtcNow);
    public string Preview => preview?.Invoke() ?? Folder.NewestPreview;

    /// <summary>The unread count or the newest result's text changed.</summary>
    public void Refresh() { Raise(nameof(Count)); Raise(nameof(Preview)); }
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

    // ---- 0.7.0: what each run found, read marks, the hikyaku-cron plugin hint ----
    public const string PluginInstallCommand = "hermes plugins install BookahCoding/Hikyaku/hermes-plugin/hikyaku-cron --enable";
    private const int DetailRowsOnShow = 20;
    private JobRunDetails? _runDetails;
    private InboxReadStore? _inboxReads;
    private bool _inboxReadsLoaded;
    private readonly Dictionary<string, RunDetail?> _runDetailCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _runDetailLoading = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _runDetailSlots = new(3, 3);
    private ICommand? _markAllJobResultsRead;

    public void AttachJobRunDetails(JobRunDetails details, InboxReadStore reads)
    {
        // One fetcher per connection. A new one drops the previous gateway's remembered results.
        if (!ReferenceEquals(_runDetails, details))
        {
            _runDetailCache.Clear();
            _runDetailLoading.Clear();
        }
        _runDetails = details;
        _inboxReads = reads;
    }

    public ICommand MarkAllJobResultsReadCommand => _markAllJobResultsRead ??= new RelayCommand(_ => _ = MarkAllJobResultsReadAsync());

    public string JobInboxPluginHint =>
        "Script-only jobs show only a short preview. To see their whole output, install the hikyaku-cron plugin on the gateway: " + PluginInstallCommand;
    public Visibility JobInboxPluginHintVisibility =>
        Shows(ExperienceFeature.InboxSetup) && _runDetails?.PluginPresent == false ? Visibility.Visible : Visibility.Collapsed;

    private bool IsJobResultRead(JobResult r) => _inboxReads is null || _inboxReads.IsRead(_gatewayId, r.Key);

    private int UnreadIn(string folderKey) =>
        _inboxReads is null ? 0 : InboxFolders.In(_jobResultsAll, folderKey, _jobInboxGroups).Count(r => !IsJobResultRead(r));

    /// <summary>A folder shows its newest result's text: fetched when known, a script run's cleaned preview, never an agent run's prompt.</summary>
    private string FolderPreview(string folderKey)
    {
        if (InboxFolders.In(_jobResultsAll, folderKey, _jobInboxGroups).FirstOrDefault() is not { } newest) return "";
        if (_runDetailCache.TryGetValue(newest.Key, out var detail) && detail is not null) return CronRunText.Excerpt(detail.Result.Text, 1);
        return newest.IsScriptOutput ? CronRunText.Excerpt(CronRunText.Parse(newest.Preview).Text, 1) : "";
    }

    /// <summary>Fetches the results of the rows on show, at most three at a time; a finished run's result is fetched once.</summary>
    private async Task LoadRunDetailsAsync(IReadOnlyList<JobResultVM> rows)
    {
        if (_runDetails is null) return;
        await Task.WhenAll(rows.Select(LoadRunDetailAsync)).ConfigureAwait(true);
    }

    private async Task LoadRunDetailAsync(JobResultVM row)
    {
        if (_runDetails is not { } details) return;
        var key = row.Result.Key;
        if (_runDetailCache.TryGetValue(key, out var cached)) { if (!row.HasDetail) row.SetDetail(cached?.Result); return; }
        if (!_runDetailLoading.Add(key)) return;
        await _runDetailSlots.WaitAsync().ConfigureAwait(true);
        try
        {
            var detail = await details.LoadAsync(row.Result, default).ConfigureAwait(true);
            // A run with no answer yet, or a script preview taken before the gateway could be asked, is not final.
            if (IsSettled(row.Result, detail)) _runDetailCache[key] = detail;
            foreach (var r in JobResults.Where(x => x.Result.Key == key)) r.SetDetail(detail?.Result);
        }
        catch (Exception ex)
        {
            var problem = Shows(ExperienceFeature.InboxSetup) ? "Could not load the result: " + ex.Message : "Could not load the result.";
            foreach (var r in JobResults.Where(x => x.Result.Key == key)) r.SetDetail(null, problem);
        }
        finally
        {
            _runDetailSlots.Release();
            _runDetailLoading.Remove(key);
            Raise(nameof(JobInboxPluginHintVisibility));
            foreach (var f in JobInboxFolders) f.Refresh();
        }
    }

    private async Task MarkJobResultsReadAsync(IReadOnlyCollection<JobResult> results)
    {
        foreach (var row in JobResults.Where(r => r.IsUnread && results.Any(x => x.Key == r.Result.Key))) row.IsUnread = false;
        foreach (var f in JobInboxFolders) f.Refresh();
        if (_inboxReads is null) return;
        try { await _inboxReads.MarkReadAsync(_gatewayId, results.Select(r => r.Key)).ConfigureAwait(true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { JobInboxStatus = "Couldn't remember what you read: " + ex.Message; }
        foreach (var f in JobInboxFolders) f.Refresh();
    }

    private Task MarkAllJobResultsReadAsync() =>
        MarkJobResultsReadAsync(InFolderList ? _jobResultsAll.ToList() : JobResults.Select(r => r.Result).ToList());

    /// <summary>A row was realized (scrolled into view). Rows past the first screen are fetched then, not left on "Loading…".</summary>
    public void NoteJobResultRealized(JobResultVM row)
    {
        if (_runDetailCache.ContainsKey(row.Result.Key)) return;
        _ = LoadRunDetailAsync(row);
    }

    private bool IsSettled(JobResult result, RunDetail? detail)
    {
        if (detail is null) return false;
        return !result.IsScriptOutput || detail.FromPlugin || _runDetails?.PluginPresent is not null;
    }

    /// <summary>A click on a row: show the whole result in place (or close it) and count it as read.</summary>
    private void ToggleJobResult(JobResultVM row)
    {
        row.IsExpanded = !row.IsExpanded;
        if (!row.IsExpanded) return;
        if (row.IsUnread) _ = MarkJobResultsReadAsync([row.Result]);
        if (!row.HasDetail) _ = LoadRunDetailAsync(row);
    }

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
            if (_inboxReads is not null && !_inboxReadsLoaded)
            {
                try { await _inboxReads.LoadAsync().ConfigureAwait(true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* nothing marked read */ }
                _inboxReadsLoaded = true;
            }
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
            JobInboxFolders.Add(new InboxFolderVM(f, new RelayCommand(_ => { _openInboxFolder = key; RebuildJobInbox(); }), move,
                () => UnreadIn(key), () => FolderPreview(key)));
        }
        if (_openInboxFolder is { } open && JobInboxFolders.All(f => f.Folder.Key != open)) _openInboxFolder = null;   // the folder emptied
        JobResults.Clear();
        if (!InFolderList)
        {
            var shown = _openInboxFolder is { } k ? InboxFolders.In(_jobResultsAll, k, _jobInboxGroups) : _jobResultsAll;
            foreach (var r in shown) JobResults.Add(ToRow(r, showProfile));
            _ = LoadRunDetailsAsync(JobResults.Take(DetailRowsOnShow).ToList());
        }
        else _ = LoadRunDetailsAsync(InboxFolders.Of(_jobResultsAll, _jobInboxGroups)
            .Select(f => InboxFolders.In(_jobResultsAll, f.Key, _jobInboxGroups).FirstOrDefault())
            .OfType<JobResult>().Distinct().Select(r => ToRow(r, showProfile)).ToList());          // each folder's newest, for its preview
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
            new RelayCommand(_ => ToggleJobResult(row!)),
            new RelayCommand(_ => _ = HideJobAsync(r)),
            new RelayCommand(_ => { if (row!.IsUnread) _ = MarkJobResultsReadAsync([r]); _ = OpenJobResultAsync(r); }),
            unread: !IsJobResultRead(r));
        if (_runDetailCache.TryGetValue(r.Key, out var cached)) row.SetDetail(cached?.Result);
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
