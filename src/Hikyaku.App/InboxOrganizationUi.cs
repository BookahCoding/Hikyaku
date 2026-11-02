using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

public sealed partial class MainViewModel
{
    private readonly SemaphoreSlim _inboxGroupMutation = new(1, 1);
    private string _jobInboxFilter = "all", _jobInboxSearch = "";
    private ICommand? _setJobInboxFilter, _createInboxFolder, _clearJobInboxFilters;

    public string JobInboxSearch
    {
        get => _jobInboxSearch;
        set { value ??= ""; if (_jobInboxSearch == value) return; Set(ref _jobInboxSearch, value); RebuildJobInbox(reuseRows: true); }
    }

    public string JobInboxFilter
    {
        get => _jobInboxFilter;
        set
        {
            if (value is not ("all" or "unread" or "failed" or "script")) return;
            if (_jobInboxFilter == value) return;
            Set(ref _jobInboxFilter, value); RebuildJobInbox(reuseRows: true);
        }
    }

    public ICommand SetJobInboxFilterCommand => _setJobInboxFilter ??= new RelayCommand(p => JobInboxFilter = p as string ?? "all");
    public ICommand ClearJobInboxFiltersCommand => _clearJobInboxFilters ??= new RelayCommand(_ =>
    {
        _jobInboxFilter = "all"; _jobInboxSearch = "";
        Raise(nameof(JobInboxFilter)); Raise(nameof(JobInboxSearch)); RebuildJobInbox(reuseRows: true);
    });
    public ICommand CreateInboxFolderCommand => _createInboxFolder ??= new RelayCommand(_ => _ = CreateInboxFolderAsync());
    public string JobInboxAllLabel => $"All ({_jobResultsAll.Count})";
    public string JobInboxUnreadLabel => $"Unread ({_jobResultsAll.Count(r => !IsJobResultRead(r))})";
    public string JobInboxFailedLabel => $"Failed ({_jobResultsAll.Count(IsInboxFailed)})";
    public string JobInboxScriptLabel => $"Script ({_jobResultsAll.Count(r => r.IsScriptOutput)})";
    public string JobInboxSummary => InFolderList
        ? $"{_jobResultsAll.Count} results in {JobInboxFolders.Count} folders"
        : $"{JobResults.Count} of {_jobResultsAll.Count} results";
    public string JobInboxEmptyText => !string.IsNullOrWhiteSpace(_jobInboxSearch) || _jobInboxFilter != "all"
        ? "No results match. Try another filter or clear your search."
        : _openInboxFolder is not null ? "This folder is empty. Use Move to folder on a job or result to add it here."
        : "No scheduled job results yet.";
    public Visibility JobInboxEmptyVisibility => (InFolderList ? JobInboxFolders.Count == 0 : JobResults.Count == 0)
        ? Visibility.Visible : Visibility.Collapsed;

    private bool IsInboxFailed(JobResult result) => result.Failed ||
        (_runDetailCache.TryGetValue(result.Key, out var detail) && detail?.Result.Status == RunStatus.Failed);

    private bool MatchesInboxOrganization(JobResult result)
    {
        if (_jobInboxFilter == "unread" && IsJobResultRead(result)) return false;
        if (_jobInboxFilter == "failed" && !IsInboxFailed(result)) return false;
        if (_jobInboxFilter == "script" && !result.IsScriptOutput) return false;
        var query = _jobInboxSearch.Trim();
        if (query.Length == 0) return true;
        var text = result.IsScriptOutput ? CronRunText.Parse(result.Preview).Text : "";
        if (_runDetailCache.TryGetValue(result.Key, out var detail) && detail is not null)
            text += " " + detail.Result.Text + " " + detail.Result.Reason;
        return result.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || result.Job.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || text.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void RaiseInboxOrganization()
    {
        foreach (var name in new[] { nameof(JobInboxAllLabel), nameof(JobInboxUnreadLabel), nameof(JobInboxFailedLabel),
            nameof(JobInboxScriptLabel), nameof(JobInboxSummary), nameof(JobInboxEmptyText), nameof(JobInboxEmptyVisibility) }) Raise(name);
    }

    private async Task CreateInboxFolderAsync()
    {
        if (PromptText is null) return;
        var gateway = _gatewayId;
        var profile = ActiveProfileKey();
        var name = PromptText("New folder name (kept on this PC)", "")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!await MutateInboxGroupsAsync(gateway, profile, groups => new InboxGroups(
            groups.Names.Append(name).Distinct(StringComparer.Ordinal).ToList(), groups.JobGroup)).ConfigureAwait(true)) return;
        _openInboxFolder = null;
        _jobInboxShowAll = false;
        _jobInboxFilter = "all";
        _jobInboxSearch = "";
        Raise(nameof(JobInboxShowAllResults)); Raise(nameof(JobInboxFilter)); Raise(nameof(JobInboxSearch));
        RebuildJobInbox(reuseRows: true);
        JobInboxStatus = $"Folder '{name}' is ready. Use Move to folder to add jobs.";
    }
    // Serialize the complete read/modify/save operation, so queued actions always use the latest groups.
    private async Task<bool> MutateInboxGroupsAsync(string gateway, ProfileKey? profile, Func<InboxGroups, InboxGroups> change)
    {
        await _inboxGroupMutation.WaitAsync().ConfigureAwait(true);
        try
        {
            if (gateway != _gatewayId || profile != ActiveProfileKey()) return false;
            await LoadInboxGroupsAsync().ConfigureAwait(true);
            if (gateway != _gatewayId || profile != ActiveProfileKey()) return false;
            var groups = change(_jobInboxGroups);
            if (_groupsStore() is { } store) await store.SaveAsync(gateway, groups).ConfigureAwait(true);
            if (gateway != _gatewayId || profile != ActiveProfileKey())
            {
                _jobInboxGroupsLoaded = false; // next action must reload the saved state for its own scope
                return false;
            }
            _jobInboxGroups = groups;
            return true;
        }
        catch (Exception ex)
        {
            if (gateway == _gatewayId) JobInboxStatus = "Couldn't remember the folder: " + ex.Message;
            return false;
        }
        finally { _inboxGroupMutation.Release(); }
    }
}
