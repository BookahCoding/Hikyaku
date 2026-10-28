using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>What the editor window returns for a new or changed job.</summary>
public sealed record CronEditorResult(string Name, string Prompt, string Schedule, string Deliver, string? Profile);

/// <summary>What the editor window is opened with. <see cref="Existing"/> is null for a new job.</summary>
public sealed record CronEditorRequest(CronJob? Existing, IReadOnlyList<string> Profiles, string? DefaultProfile,
    Func<string?, Task<IReadOnlyList<DeliveryTarget>>> LoadTargets);

/// <summary>One row of the Scheduled jobs list.</summary>
public sealed class CronJobVM : Observable
{
    private CronJob _job;
    private bool _busy;
    private readonly bool _showProfile;

    public CronJobVM(CronJob job, bool showProfile, Action<CronJobVM, string> act)
    {
        _job = job;
        _showProfile = showProfile;
        PauseResumeCommand = new RelayCommand(_ => act(this, Job.Paused ? "resume" : "pause"), _ => !_busy);
        RunNowCommand = new RelayCommand(_ => act(this, "run"), _ => !_busy);
        EditCommand = new RelayCommand(_ => act(this, "edit"), _ => !_busy);
        DeleteCommand = new RelayCommand(_ => act(this, "delete"), _ => !_busy);
    }

    public CronJob Job
    {
        get => _job;
        set
        {
            _job = value;
            foreach (var n in new[] { nameof(Job), nameof(Title), nameof(Schedule), nameof(StateLabel), nameof(NextRun), nameof(LastRun), nameof(Notes), nameof(NotesVisibility), nameof(PauseResumeLabel), nameof(Meta) })
                Raise(n);
        }
    }

    public bool Busy { get => _busy; set { Set(ref _busy, value); RefreshCommands(); } }

    public string Title => Job.DisplayName;
    public string Schedule => string.IsNullOrWhiteSpace(Job.ScheduleDisplay) ? "(no schedule shown)" : Job.ScheduleDisplay;
    public string StateLabel => Job.State switch
    {
        "paused" => "Paused",
        "completed" => "Finished",
        "error" => "Error",
        _ => "Scheduled"
    };
    public string NextRun => Job.Paused ? "Paused" : Job.NextRunAt is { } t ? "Next " + Describe(t) : "No run planned";
    public string LastRun => Job.LastRunAt is { } t
        ? "Last run " + Describe(t) + (string.IsNullOrWhiteSpace(Job.LastStatus) ? "" : " (" + Job.LastStatus + ")")
        : "Never run";
    public string PauseResumeLabel => Job.Paused ? "Resume" : "Pause";

    /// <summary>Profile and routine tag, so jobs of different profiles are never confused when All profiles is on.</summary>
    public string Meta
    {
        get
        {
            var parts = new List<string>();
            if (Job.IsRoutine) parts.Add("Routine · " + Job.RoutineBot);
            if (_showProfile && !string.IsNullOrWhiteSpace(Job.Profile)) parts.Add("Profile " + Job.Profile);
            if (!string.IsNullOrWhiteSpace(Job.Deliver) && Job.Deliver != "local") parts.Add("Delivers to " + Job.Deliver);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Things worth a second look, in plain words: failures, undelivered results, missed fires, why it paused.</summary>
    public string Notes
    {
        get
        {
            var n = new List<string>();
            if (Job.Paused && !string.IsNullOrWhiteSpace(Job.PausedReason)) n.Add("Paused: " + Job.PausedReason);
            if (Job.DeliveryFailed) n.Add("The last run worked but its result was not delivered" + (string.IsNullOrWhiteSpace(Job.LastDeliveryError) ? "." : ": " + Job.LastDeliveryError));
            else if (Job.LastRunFailed) n.Add("The last run failed" + (string.IsNullOrWhiteSpace(Job.LastError) ? "." : ": " + Job.LastError));
            if (Job.MissedFire) n.Add("The scheduler could not start the last run: " + Job.LastFireError);
            return string.Join("\n", n);
        }
    }
    public Visibility NotesVisibility => Notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public ICommand PauseResumeCommand { get; }
    public ICommand RunNowCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }

    private void RefreshCommands()
    {
        foreach (var c in new[] { PauseResumeCommand, RunNowCommand, EditCommand, DeleteCommand }) ((RelayCommand)c).Refresh();
    }

    private static string Describe(DateTimeOffset t) =>
        t.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture);
}

/// <summary>
/// Scheduled jobs (Hermes Desktop's Cron page): every job on the gateway for the profiles chosen here, pause / resume, run now,
/// edit, delete, and a structured schedule picker. Every job action goes to the profile that owns the job, never the active one.
/// Run now and Delete are confirmed in words; nothing is retried or re-sent, and a failed call leaves the list as it was.
/// </summary>
public sealed partial class MainViewModel
{
    private Func<CronApi?> _cronApi = () => null;
    private string _gatewayId = "";
    private bool _cronAll;
    private string _cronStatus = "";
    private bool _cronLoading;
    private int _cronRefreshId;

    public ObservableCollection<CronJobVM> CronRows { get; } = new();

    public ICommand? RefreshCronCommand { get; private set; }
    public ICommand? NewCronJobCommand { get; private set; }

    /// <summary>Asks before something with side effects (title, message) → true only on an explicit yes. Unset means no.</summary>
    public Func<string, string, bool>? ConfirmCron { get; set; }

    /// <summary>Opens the editor; null means cancelled.</summary>
    public Func<CronEditorRequest, Task<CronEditorResult?>>? EditCron { get; set; }

    public void AttachCron(Func<CronApi?> api, string gatewayId)
    {
        _cronApi = api;
        _gatewayId = gatewayId;
    }

    public bool CronAllProfiles
    {
        get => _cronAll;
        set
        {
            if (_cronAll == value) return;
            _cronAll = value;
            Raise();
            Raise(nameof(CronScopeLabel));
            if (Nav == MainNav.Scheduled) _ = RefreshCronAsync();
        }
    }

    public string CronScopeLabel => _cronAll ? "Showing every profile you have selected" : "Showing the active profile";
    public string CronStatus { get => _cronStatus; private set { Set(ref _cronStatus, value); Raise(nameof(CronStatusVisibility)); } }
    public Visibility CronStatusVisibility => _cronStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool CronLoading { get => _cronLoading; private set { Set(ref _cronLoading, value); (RefreshCronCommand as RelayCommand)?.Refresh(); (NewCronJobCommand as RelayCommand)?.Refresh(); } }

    public Visibility ScheduledVisibility =>
        ChatShellVisibility == Visibility.Visible && Nav == MainNav.Scheduled ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The sidebar row. Hidden in Simple mode (Hermes Desktop hides the Scheduled jobs row there); the palette entry follows it.</summary>

    public Visibility CronEmptyVisibility => CronRows.Count == 0 && !_cronLoading && _cronStatus.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void InitCron()
    {
        RefreshCronCommand = new RelayCommand(_ => _ = RefreshCronAsync(), _ => !_cronLoading);
        NewCronJobCommand = new RelayCommand(_ => _ = NewCronJobAsync(), _ => !_cronLoading);
        ShowScheduledCommand = new RelayCommand(_ => ShowScheduled());
        CronRows.CollectionChanged += (_, _) => Raise(nameof(CronEmptyVisibility));
    }

    public ICommand? ShowScheduledCommand { get; private set; }

    private void ShowScheduled()
    {
        Nav = MainNav.Scheduled;
        _ = RefreshCronAsync();
    }

    private IReadOnlyList<string> CronProfileIds() =>
        _selection.Selected.Where(k => k.GatewayId == _gatewayId).Select(k => k.ProfileId).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();

    private string? ActiveProfileId()
    {
        var active = _sessions()?.ActiveProfile;
        if (active is { } a && a.GatewayId == _gatewayId && _gate.Allow(Surface.Cron, a)) return a.ProfileId;
        return CronProfileIds().FirstOrDefault();
    }

    private bool CronAllowed(string? profile) =>
        profile is { Length: > 0 } && _gate.Allow(Surface.Cron, new ProfileKey(_gatewayId, profile));

    public async Task RefreshCronAsync()
    {
        if (_cronApi() is not { } api) { CronStatus = "Connect to a gateway to see its scheduled jobs."; return; }
        var active = ActiveProfileId();
        if (active is null) { CronStatus = "Choose a profile first."; return; }
        var id = ++_cronRefreshId;
        CronLoading = true;
        CronStatus = "";
        try
        {
            var jobs = await api.JobsAsync(active, _cronAll, CancellationToken.None).ConfigureAwait(true);
            if (id != _cronRefreshId) return;          // a newer refresh owns the list
            var shown = new List<CronJobVM>();
            foreach (var raw in jobs)
            {
                var job = raw.Profile is { Length: > 0 } ? raw : raw with { Profile = _cronAll ? null : active };
                if (!CronAllowed(job.Profile)) continue;   // another profile's job, or one with no owner, is never shown
                shown.Add(new CronJobVM(job, _cronAll, OnCronAction));
            }
            CronRows.Clear();
            foreach (var r in shown) CronRows.Add(r);
            CronStatus = shown.Count == 0 && jobs.Count > 0 ? "Jobs exist on this gateway, but none belong to the profiles you chose." : "";
        }
        catch (CronException ex) { if (id == _cronRefreshId) CronStatus = CronMessage(ex); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        {
            if (id == _cronRefreshId) CronStatus = "Couldn't reach the gateway: " + ex.Message;
        }
        finally { if (id == _cronRefreshId) CronLoading = false; Raise(nameof(CronEmptyVisibility)); }
    }

    private static string CronMessage(CronException ex) => ex.Failure switch
    {
        CronFailure.Auth => "Sign in again to manage scheduled jobs.",
        CronFailure.Unsupported => "This gateway does not offer scheduled jobs through the dashboard.",
        CronFailure.Conflict => ex.Message.Length > 0 ? ex.Message : "That job is already running.",
        CronFailure.NotFound => "That job no longer exists. The list was refreshed.",
        _ => ex.Message
    };

    private void OnCronAction(CronJobVM row, string verb) => _ = RunCronActionAsync(row, verb);

    public async Task RunCronActionAsync(CronJobVM row, string verb)
    {
        if (!Shows(ExperienceFeature.Scheduled)) return;                                  // Simple view never reaches it, whoever asks
        if (_cronApi() is not { } api) { CronStatus = "Connect to a gateway first."; return; }
        var job = row.Job;
        if (!CronAllowed(job.Profile)) { CronStatus = "That job belongs to a profile you haven't chosen."; return; }
        var where = string.IsNullOrWhiteSpace(job.Profile) ? "" : $" (profile {job.Profile})";
        try
        {
            switch (verb)
            {
                case "run":
                    if (ConfirmCron?.Invoke("Run now",
                            $"Run “{job.DisplayName}”{where} now?\n\nThis starts an agent run on the gateway outside its schedule. It can use tools" +
                            (string.IsNullOrWhiteSpace(job.Deliver) || job.Deliver == "local" ? " and keeps its result on the gateway." : $" and sends its result to {job.Deliver}.")) != true)
                        return;
                    await Mutate(row, () => api.TriggerAsync(job, CancellationToken.None), "Started. It runs in the background on the gateway; open Inbox or Chats for the result.");
                    break;
                case "delete":
                    if (ConfirmCron?.Invoke("Delete scheduled job",
                            $"Delete “{job.DisplayName}”{where}?\n\nThe job and its schedule are removed from the gateway. This can't be undone.") != true)
                        return;
                    row.Busy = true;
                    try
                    {
                        await api.DeleteAsync(job, CancellationToken.None).ConfigureAwait(true);
                        CronRows.Remove(row);
                        CronStatus = $"Deleted “{job.DisplayName}”.";
                    }
                    finally { row.Busy = false; }
                    break;
                case "pause":
                    await Mutate(row, () => api.PauseAsync(job, CancellationToken.None), $"Paused “{job.DisplayName}”.");
                    break;
                case "resume":
                    await Mutate(row, () => api.ResumeAsync(job, CancellationToken.None), $"Resumed “{job.DisplayName}”.");
                    break;
                case "edit":
                    await EditCronAsync(row, api);
                    break;
            }
        }
        catch (CronException ex)
        {
            CronStatus = CronMessage(ex);
            if (ex.Failure == CronFailure.NotFound) { CronRows.Remove(row); _ = RefreshCronAsync(); }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        {
            CronStatus = "Couldn't reach the gateway: " + ex.Message;
        }
    }

    private async Task Mutate(CronJobVM row, Func<Task<CronJob>> call, string done)
    {
        row.Busy = true;
        try
        {
            var updated = await call().ConfigureAwait(true);
            // The reply may omit the owner; the row keeps the one it was listed under.
            row.Job = updated.Profile is { Length: > 0 } ? updated : updated with { Profile = row.Job.Profile };
            CronStatus = done;
        }
        finally { row.Busy = false; }
    }

    private async Task EditCronAsync(CronJobVM row, CronApi api)
    {
        if (EditCron is not { } edit) return;
        var job = row.Job;
        var result = await edit(new CronEditorRequest(job, [job.Profile ?? ""], job.Profile, p => LoadTargetsAsync(api, p))).ConfigureAwait(true);
        if (result is null) return;
        var changes = new Dictionary<string, string>();
        if (result.Name != job.Name) changes["name"] = result.Name;
        if (result.Prompt != job.Prompt) changes["prompt"] = result.Prompt;
        if (result.Schedule != job.EditableSchedule && !string.IsNullOrWhiteSpace(result.Schedule)) changes["schedule"] = result.Schedule;
        if (!string.Equals(result.Deliver, job.Deliver ?? "local", StringComparison.Ordinal)) changes["deliver"] = result.Deliver;
        if (changes.Count == 0) { CronStatus = "Nothing changed."; return; }
        await Mutate(row, () => api.UpdateAsync(job, changes, CancellationToken.None), $"Saved “{(result.Name.Length > 0 ? result.Name : job.DisplayName)}”.");
    }

    public async Task NewCronJobAsync()
    {
        if (!Shows(ExperienceFeature.Scheduled)) return;                                  // Simple view never reaches it, whoever asks
        if (_cronApi() is not { } api) { CronStatus = "Connect to a gateway first."; return; }
        if (EditCron is not { } edit) return;
        var profiles = CronProfileIds();
        if (profiles.Count == 0) { CronStatus = "Choose a profile first."; return; }
        try
        {
            var active = ActiveProfileId();
            var result = await edit(new CronEditorRequest(null, profiles, active, p => LoadTargetsAsync(api, p))).ConfigureAwait(true);
            if (result is null) return;
            if (!CronAllowed(result.Profile)) { CronStatus = "Choose one of your selected profiles."; return; }
            var draft = new CronJobDraft(result.Name.Trim(), result.Prompt, result.Schedule.Trim(), result.Deliver);
            if (draft.Problem() is { } problem) { CronStatus = problem; return; }
            CronLoading = true;
            try
            {
                var made = await api.CreateAsync(draft, result.Profile, CancellationToken.None).ConfigureAwait(true);
                var owned = made.Profile is { Length: > 0 } ? made : made with { Profile = result.Profile };
                if (_cronAll || result.Profile == active) CronRows.Insert(0, new CronJobVM(owned, _cronAll, OnCronAction));
                CronStatus = $"Created “{owned.DisplayName}”" + (_cronAll || result.Profile == active ? "." : $" in profile {result.Profile}.");
            }
            finally { CronLoading = false; }
        }
        catch (CronException ex) { CronStatus = CronMessage(ex); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        {
            CronStatus = "Couldn't reach the gateway: " + ex.Message;
        }
    }

    private async Task<IReadOnlyList<DeliveryTarget>> LoadTargetsAsync(CronApi api, string? profile)
    {
        try { return await api.DeliveryTargetsAsync(profile, CancellationToken.None).ConfigureAwait(true); }
        catch (Exception ex) when (ex is CronException or HttpRequestException or TaskCanceledException or AuthFailedException)
        {
            return [new DeliveryTarget("local", "Local (kept on the gateway)")];   // never guess other targets
        }
    }

}
