using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>A skill the hub offers.</summary>
public sealed class HubRowVM : Observable
{
    public HubRowVM(HubSkill skill, bool installed, MainViewModel owner)
    {
        Skill = skill;
        _installed = installed || skill.Installed;
        PreviewCommand = new RelayCommand(_ => _ = owner.PreviewHubSkillAsync(this));
        CheckSafetyCommand = new RelayCommand(_ => _ = owner.CheckHubSkillAsync(this));
        InstallCommand = new RelayCommand(_ => _ = owner.InstallHubSkillAsync(this), _ => !_installed && !owner.HubActing);
        UninstallCommand = new RelayCommand(_ => _ = owner.UninstallHubSkillAsync(this), _ => _installed && !owner.HubActing);
    }

    private bool _installed;
    public HubSkill Skill { get; }
    public bool Installed
    {
        get => _installed;
        set { Set(ref _installed, value); Raise(nameof(Meta)); Raise(nameof(InstallVisibility)); Raise(nameof(UninstallVisibility)); RefreshCommands(); }
    }
    public Visibility InstallVisibility => _installed ? Visibility.Collapsed : Visibility.Visible;
    public Visibility UninstallVisibility => _installed ? Visibility.Visible : Visibility.Collapsed;
    public void RefreshCommands() { ((RelayCommand)InstallCommand).Refresh(); ((RelayCommand)UninstallCommand).Refresh(); }
    public string Name => Skill.Name;
    public string Description => Skill.Description;
    public string Meta => string.Join(" · ", new[]
    {
        Skill.IsOfficial ? "official" : Skill.Source,
        Skill.TrustLabel,
        Skill.Category,
        Installed ? "installed" : null
    }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public ICommand PreviewCommand { get; }
    public ICommand CheckSafetyCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand UninstallCommand { get; }
}

public sealed record HubFindingVM(string Header, string Description);

/// <summary>
/// The skills hub (Capabilities → Skills hub): featured skills, search across the hub's sources, a skill's own text and file list, the
/// gateway's safety scan, and (approved by the maintainer, 2026-10-27) install, uninstall and update-all. Those put third-party code on the
/// gateway, so: an install always runs a fresh safety check first, a "block" policy is never overridden (nothing is sent), and
/// every write is confirmed in words naming what it does; with no confirmation handler nothing is sent. One write at a time; the
/// gateway runs it in the background and Hikyaku polls it. Slow reads (search can take 30 s) run only on a click; a newer one replaces an older.
/// </summary>
public sealed partial class MainViewModel
{
    private Func<SkillsHubApi?> _hubApi = () => null;
    private string _hubStatus = "Choose Browse featured, or search the hub.";
    private string _hubQuery = "";
    private string _hubNote = "";
    private bool _hubLoading;
    private int _hubRefreshId, _hubDetailId;
    private string _hubProfile = "";
    private HubRowVM? _hubOpen;
    private string _hubDetailTitle = "", _hubDetailMeta = "", _hubDetailBody = "", _hubDetailFiles = "", _hubDetailNote = "";
    private string _hubScanVerdict = "";
    private string _hubMessage = "";
    private bool _hubActing;

    public ObservableCollection<HubRowVM> HubRows { get; } = new();
    public ObservableCollection<HubFindingVM> HubFindings { get; } = new();
    public ICommand? BrowseHubCommand { get; private set; }
    public ICommand? SearchHubCommand { get; private set; }
    public ICommand? CloseHubDetailCommand { get; private set; }
    public ICommand? UpdateAllHubCommand { get; private set; }

    /// <summary>Asked before any install, removal or update (title, message) → true only on an explicit yes. Unset means no.</summary>
    public Func<string, string, bool>? ConfirmHub { get; set; }

    /// <summary>The outcome of the last write, in words.</summary>
    public string HubMessage { get => _hubMessage; private set { Set(ref _hubMessage, value); Raise(nameof(HubMessageVisibility)); } }
    public Visibility HubMessageVisibility => _hubMessage.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>A write is running on the gateway: the other writes wait.</summary>
    public bool HubActing { get => _hubActing; private set { Set(ref _hubActing, value); RefreshHubWriteCommands(); } }

    public void AttachSkillsHub(Func<SkillsHubApi?> api) => _hubApi = api;

    public string HubStatus { get => _hubStatus; private set { Set(ref _hubStatus, value); Raise(nameof(HubStatusVisibility)); } }
    public Visibility HubStatusVisibility => _hubStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string HubNote { get => _hubNote; private set { Set(ref _hubNote, value); Raise(nameof(HubNoteVisibility)); } }
    public Visibility HubNoteVisibility => _hubNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool HubLoading { get => _hubLoading; private set { Set(ref _hubLoading, value); (BrowseHubCommand as RelayCommand)?.Refresh(); (SearchHubCommand as RelayCommand)?.Refresh(); } }
    public string HubQuery { get => _hubQuery; set { Set(ref _hubQuery, value ?? ""); (SearchHubCommand as RelayCommand)?.Refresh(); } }
    public Visibility HubDetailVisibility => _hubOpen is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HubListVisibility => _hubOpen is null ? Visibility.Visible : Visibility.Collapsed;
    public string HubDetailTitle { get => _hubDetailTitle; private set => Set(ref _hubDetailTitle, value); }
    public string HubDetailMeta { get => _hubDetailMeta; private set => Set(ref _hubDetailMeta, value); }
    public string HubDetailBody { get => _hubDetailBody; private set => Set(ref _hubDetailBody, value); }
    public string HubDetailFiles { get => _hubDetailFiles; private set => Set(ref _hubDetailFiles, value); }
    public string HubDetailNote { get => _hubDetailNote; private set { Set(ref _hubDetailNote, value); Raise(nameof(HubDetailNoteVisibility)); } }
    public Visibility HubDetailNoteVisibility => _hubDetailNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string HubScanVerdict { get => _hubScanVerdict; private set { Set(ref _hubScanVerdict, value); Raise(nameof(HubScanVisibility)); } }
    public Visibility HubScanVisibility => _hubScanVerdict.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Synthetic UI acceptance only: shows hub rows and a scan result without any gateway.</summary>
    internal void AcceptanceSetHub(IEnumerable<HubSkill> skills)
    {
        FillHub(skills, new Dictionary<string, HubInstalled>());
        HubStatus = "3 featured skill(s). (synthetic)";
        HubNote = "Not answering right now: GitHub.";
    }

    private void InitSkillsHub()
    {
        BrowseHubCommand = new RelayCommand(_ => _ = BrowseHubAsync(), _ => !_hubLoading);
        SearchHubCommand = new RelayCommand(_ => _ = SearchHubAsync(), _ => !_hubLoading && _hubQuery.Trim().Length > 0);
        CloseHubDetailCommand = new RelayCommand(_ => CloseHubDetail());
        UpdateAllHubCommand = new RelayCommand(_ => _ = UpdateAllHubAsync(), _ => !_hubActing);
    }

    private static string HubReason(RestException ex) => ex.Failure switch
    {
        RestFailure.Auth => "Sign in again to browse the skills hub.",
        RestFailure.Unsupported => "This gateway does not offer the skills hub through the dashboard.",
        RestFailure.NotFound => "The hub could not find that skill.",
        _ => ex.Message
    };

    private bool HubProfile(out SkillsHubApi api, out string profile, bool forAction = false)
    {
        api = null!; profile = "";
        if (forAction && !Shows(ExperienceFeature.Capabilities)) return false;          // Simple view never installs or removes skills, whoever asks
        if (_hubApi() is not { } a) { HubStatus = "Connect to a gateway to browse its skills hub."; return false; }
        var p = ActiveProfileId();
        if (p is null || !CronAllowed(p)) { HubStatus = "Choose a profile first."; return false; }
        if (_hubProfile != p)
        {
            var switched = _hubProfile.Length > 0;
            _hubProfile = p; HubRows.Clear(); CloseHubDetail(); HubMessage = "";                          // another profile's installed marks are never shown under this one
            if (forAction && switched) { HubStatus = "The profile changed, so this list was cleared. Search again, then choose the skill."; return false; }   // a row from the old profile is never acted on
        }
        api = a; profile = p;
        return true;
    }

    private void FillHub(IEnumerable<HubSkill> skills, IReadOnlyDictionary<string, HubInstalled> installed)
    {
        HubRows.Clear();
        foreach (var s in skills)
            HubRows.Add(new HubRowVM(s, installed.ContainsKey(s.Key) || installed.Values.Any(i => s.Name.Length > 0 && i.Name == s.Name), this));
    }

    public async Task BrowseHubAsync()
    {
        if (!HubProfile(out var api, out var profile)) return;
        var id = ++_hubRefreshId;
        HubLoading = true; HubStatus = "Loading the featured skills…"; HubNote = "";
        try
        {
            var s = await api.SourcesAsync(profile, CancellationToken.None).ConfigureAwait(true);
            if (id != _hubRefreshId) return;
            FillHub(s.Featured, s.Installed);
            var unavailable = s.Sources.Where(x => !x.Available || x.RateLimited).Select(x => x.Label).ToList();
            HubNote = unavailable.Count > 0 ? "Not answering right now: " + string.Join(", ", unavailable) + "." : "";
            HubStatus = s.Featured.Count == 0 ? "The hub has nothing featured right now." : $"{s.Featured.Count} featured skill(s).";
        }
        catch (RestException ex) { if (id == _hubRefreshId) HubStatus = HubReason(ex); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        {
            if (id == _hubRefreshId) HubStatus = "Couldn't reach the gateway: " + ex.Message;
        }
        finally { if (id == _hubRefreshId) HubLoading = false; }
    }

    public async Task SearchHubAsync()
    {
        var q = _hubQuery.Trim();
        if (q.Length == 0 || !HubProfile(out var api, out var profile)) return;
        var id = ++_hubRefreshId;
        HubLoading = true; HubStatus = "Searching the hub (this can take up to 30 seconds)…"; HubNote = "";
        try
        {
            var r = await api.SearchAsync(profile, q, 30, CancellationToken.None).ConfigureAwait(true);
            if (id != _hubRefreshId) return;
            FillHub(r.Results, r.Installed);
            HubNote = r.TimedOut.Count > 0 ? "Slow sources that did not answer in time: " + string.Join(", ", r.TimedOut) + "." : "";
            HubStatus = r.Results.Count == 0 ? $"Nothing found for “{q}”." : $"{r.Results.Count} result(s) for “{q}”.";
        }
        catch (RestException ex) { if (id == _hubRefreshId) HubStatus = HubReason(ex); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        {
            if (id == _hubRefreshId) HubStatus = "Couldn't reach the gateway: " + ex.Message;
        }
        finally { if (id == _hubRefreshId) HubLoading = false; }
    }

    private void OpenHubDetail(HubRowVM row)
    {
        _hubOpen = row;
        HubDetailTitle = row.Name; HubDetailMeta = row.Meta; HubDetailBody = ""; HubDetailFiles = ""; HubDetailNote = "Loading…";
        HubScanVerdict = ""; HubFindings.Clear();
        Raise(nameof(HubDetailVisibility)); Raise(nameof(HubListVisibility));
    }

    /// <summary>Reads a skill's own text and file list: nothing is installed or changed.</summary>
    public async Task PreviewHubSkillAsync(HubRowVM row)
    {
        if (!HubProfile(out var api, out var profile)) return;
        OpenHubDetail(row);
        var id = ++_hubDetailId;
        try
        {
            var p = await api.PreviewAsync(profile, row.Skill.Key, CancellationToken.None).ConfigureAwait(true);
            if (id != _hubDetailId) return;
            HubDetailBody = p.Body;
            HubDetailFiles = p.Files.Count == 0 ? "" : "Files: " + string.Join(", ", p.Files);
            HubDetailMeta = string.Join(" · ", new[] { row.Meta, p.Tags.Count > 0 ? string.Join(", ", p.Tags) : null }.Where(s => !string.IsNullOrWhiteSpace(s)));
            HubDetailNote = string.IsNullOrWhiteSpace(p.Body) ? "This skill has no text to show." : "";
        }
        catch (RestException ex) { if (id == _hubDetailId) HubDetailNote = HubReason(ex); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        {
            if (id == _hubDetailId) HubDetailNote = "Couldn't reach the gateway: " + ex.Message;
        }
    }

    /// <summary>Asks the gateway to scan a skill and shows its verdict, what its policy would do, and each finding. Nothing is installed.</summary>
    public async Task CheckHubSkillAsync(HubRowVM row)
    {
        if (!HubProfile(out var api, out var profile)) return;
        OpenHubDetail(row);
        var id = ++_hubDetailId;
        try
        {
            var s = await api.ScanAsync(profile, row.Skill.Key, CancellationToken.None).ConfigureAwait(true);
            if (id != _hubDetailId) return;
            ShowHubScan(s);
        }
        catch (RestException ex) { if (id == _hubDetailId) HubDetailNote = HubReason(ex); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        {
            if (id == _hubDetailId) HubDetailNote = "Couldn't reach the gateway: " + ex.Message;
        }
    }

    private void ShowHubScan(HubScan s)
    {
        HubDetailNote = "";
        HubScanVerdict = $"Safety check: {(s.Verdict.Length > 0 ? s.Verdict : "no verdict")}"
            + (s.Policy.Length > 0 ? $" · the gateway would {PolicyWords(s.Policy)}" : "")
            + (s.PolicyReason.Length > 0 ? $" ({s.PolicyReason})" : "")
            + (s.Summary.Length > 0 ? "\n" + s.Summary : "");
        HubFindings.Clear();
        foreach (var f in s.Findings)
            HubFindings.Add(new HubFindingVM(
                string.Join(" · ", new[] { f.Severity, f.Category, f.File.Length > 0 ? f.File + (f.Line > 0 ? ":" + f.Line : "") : null }.Where(x => !string.IsNullOrWhiteSpace(x))),
                f.Description));
        if (s.Verdict.Length == 0 && s.Findings.Count == 0) HubScanVerdict = "Safety check: the gateway gave no verdict for this skill.";
    }

    private static string PolicyWords(string policy) => policy switch
    {
        "allow" => "install it",
        "ask" => "ask before installing it",
        "block" => "refuse to install it",
        var other => other
    };

    private void RefreshHubWriteCommands()
    {
        foreach (var r in HubRows) r.RefreshCommands();
        (UpdateAllHubCommand as RelayCommand)?.Refresh();
    }

    private const string AppliesNextChat = "It applies from the next chat.";

    private static string LastLine(HubActionResult r) => r.Lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";

    /// <summary>
    /// Installs a hub skill. Always a fresh safety check first. A "block" policy ends it there: the scan is shown and nothing is sent.
    /// Otherwise the person must say yes to a message naming the skill, its source, trust level, verdict, policy and findings.
    /// </summary>
    public async Task InstallHubSkillAsync(HubRowVM row)
    {
        if (_hubActing || row.Installed || !HubRows.Contains(row) || !HubProfile(out var api, out var profile, forAction: true)) return;
        HubActing = true;
        HubMessage = $"Checking {row.Name} before installing…";
        try
        {
            HubScan scan;
            try { scan = await api.ScanAsync(profile, row.Skill.Key, CancellationToken.None).ConfigureAwait(true); }
            catch (RestException ex) { HubMessage = $"Couldn't check {row.Name}, so it was not installed: {HubReason(ex)}"; return; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
            { HubMessage = $"Couldn't check {row.Name}, so it was not installed: {ex.Message}"; return; }

            if (scan.Policy == "block")
            {
                OpenHubDetail(row);
                ShowHubScan(scan);
                HubMessage = $"{row.Name} was not installed: the safety check says the gateway would refuse it.";
                return;
            }

            if (ConfirmHub?.Invoke("Install skill", InstallConfirmation(row, scan)) != true) { HubMessage = $"{row.Name} was not installed."; return; }

            HubMessage = $"Installing {row.Name}…";
            var result = await api.InstallAsync(profile, row.Skill.Key, CancellationToken.None).ConfigureAwait(true);
            switch (result)
            {
                case HubActionResult.Done:
                    row.Installed = true;
                    HubMessage = $"Installed {row.Name}. {AppliesNextChat}";
                    break;
                case HubActionResult.Blocked b:
                    HubMessage = $"{row.Name} was blocked by the security scan" + (b.Findings is { } n ? $" ({n} high-risk finding(s))." : ".");
                    OpenHubDetail(row);
                    ShowHubScan(scan);
                    break;
                case HubActionResult.StillRunning s:
                    HubMessage = s.Lines.FirstOrDefault() ?? "The install is still running on the gateway.";
                    break;
                default:
                    HubMessage = string.Join(": ", new[] { $"Couldn't install {row.Name}", LastLine(result) }.Where(x => x.Length > 0));
                    break;
            }
        }
        catch (RestException ex) { HubMessage = $"Couldn't install {row.Name}: {HubReason(ex)}"; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        { HubMessage = $"Couldn't install {row.Name}: {ex.Message}"; }
        finally { HubActing = false; }
    }

    private static string InstallConfirmation(HubRowVM row, HubScan scan)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"Install \u201c{row.Name}\u201d on your gateway?\n\n");
        sb.Append("Source: ").Append(row.Skill.IsOfficial ? "official" : (scan.Source.Length > 0 ? scan.Source : row.Skill.Source));
        var trust = scan.TrustLevel.Length > 0 ? scan.TrustLevel : row.Skill.TrustLevel;
        if (trust.Length > 0) sb.Append(" · trust: ").Append(trust);
        sb.Append("\nSafety check: ").Append(scan.Verdict.Length > 0 ? scan.Verdict : "no verdict");
        if (scan.Policy.Length > 0) sb.Append(" — the gateway would ").Append(PolicyWords(scan.Policy));
        if (scan.PolicyReason.Length > 0) sb.Append(" (").Append(scan.PolicyReason).Append(')');
        if (scan.Summary.Length > 0) sb.Append('\n').Append(scan.Summary);
        foreach (var f in scan.Findings.Take(3))
            sb.Append("\n  \u2022 ").Append(f.Severity).Append(f.File.Length > 0 ? " in " + f.File + (f.Line > 0 ? ":" + f.Line : "") : "").Append(": ").Append(f.Description);
        if (scan.Findings.Count > 3) sb.Append($"\n  \u2026 and {scan.Findings.Count - 3} more");
        sb.Append("\n\nThe agent will be able to follow this skill's instructions and run its files. ").Append(AppliesNextChat);
        return sb.ToString();
    }

    /// <summary>Removes an installed hub skill from the profile, after a yes.</summary>
    public async Task UninstallHubSkillAsync(HubRowVM row)
    {
        if (_hubActing || !row.Installed || !HubRows.Contains(row) || !HubProfile(out var api, out var profile, forAction: true)) return;
        if (ConfirmHub?.Invoke("Remove skill",
                $"Remove the skill \u201c{row.Name}\u201d from this profile?\n\nThe gateway deletes its files. You can install it again from the hub later. {AppliesNextChat}") != true)
        { HubMessage = $"{row.Name} was not removed."; return; }
        HubActing = true;
        HubMessage = $"Removing {row.Name}…";
        try
        {
            var result = await api.UninstallAsync(profile, row.Name, CancellationToken.None).ConfigureAwait(true);
            switch (result)
            {
                case HubActionResult.Done: row.Installed = false; HubMessage = $"Removed {row.Name}. {AppliesNextChat}"; break;
                case HubActionResult.StillRunning s: HubMessage = s.Lines.FirstOrDefault() ?? "The removal is still running on the gateway."; break;
                default: HubMessage = string.Join(": ", new[] { $"Couldn't remove {row.Name}", LastLine(result) }.Where(x => x.Length > 0)); break;
            }
        }
        catch (RestException ex) { HubMessage = $"Couldn't remove {row.Name}: {HubReason(ex)}"; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        { HubMessage = $"Couldn't remove {row.Name}: {ex.Message}"; }
        finally { HubActing = false; }
    }

    /// <summary>Updates every skill installed from the hub on this profile, after a yes.</summary>
    public async Task UpdateAllHubAsync()
    {
        if (_hubActing || !HubProfile(out var api, out var profile, forAction: true)) return;
        if (ConfirmHub?.Invoke("Update skills",
                $"Update every skill installed from the hub on this profile?\n\nThe gateway fetches new versions from their sources and replaces the current ones. Those sources are third parties. {AppliesNextChat}") != true)
        { HubMessage = "Skills were not updated."; return; }
        HubActing = true;
        HubMessage = "Updating skills…";
        try
        {
            var result = await api.UpdateAllAsync(profile, CancellationToken.None).ConfigureAwait(true);
            switch (result)
            {
                case HubActionResult.Done: HubMessage = $"Skills updated. {AppliesNextChat}"; break;
                case HubActionResult.StillRunning s: HubMessage = s.Lines.FirstOrDefault() ?? "The update is still running on the gateway."; break;
                default: HubMessage = string.Join(": ", new[] { "Couldn't update skills", LastLine(result) }.Where(x => x.Length > 0)); break;
            }
        }
        catch (RestException ex) { HubMessage = $"Couldn't update skills: {HubReason(ex)}"; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        { HubMessage = $"Couldn't update skills: {ex.Message}"; }
        finally { HubActing = false; }
    }

    private void CloseHubDetail()
    {
        _hubOpen = null;
        _hubDetailId++;                              // a read still in flight is dropped
        HubDetailBody = ""; HubDetailFiles = ""; HubDetailNote = ""; HubScanVerdict = ""; HubFindings.Clear();
        Raise(nameof(HubDetailVisibility)); Raise(nameof(HubListVisibility));
    }
}
