using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;
using Microsoft.Win32;

namespace Hikyaku.App;

public sealed class ProjectFileRowVM : Observable
{
    public ProjectFileRowVM(ProjectEntry entry)
    {
        Entry = entry;
        Name = entry.IsDirectory ? entry.Name + "/" : entry.Name;
        Meta = entry.Sensitive ? "sensitive · not shared by default" : (entry.IsDirectory ? "folder" : "file");
    }
    public ProjectEntry Entry { get; }
    public string Name { get; }
    public string Meta { get; }
    public string RelativePath => Entry.RelativePath;
}

public sealed class GitChangeRowVM : Observable
{
    private bool _selected;
    public GitChangeRowVM(GitChangeEntry entry)
    {
        Entry = entry;
        Label = entry.StatusCode + "  " + entry.Path;
        Scope = entry.InIndex && entry.InWorkingTree ? "staged + unstaged"
            : entry.InIndex ? "staged" : "unstaged";
    }
    public GitChangeEntry Entry { get; }
    public string Label { get; }
    public string Scope { get; }
    public string Path => Entry.Path;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

public sealed class GitWorktreeRowVM : Observable
{
    public GitWorktreeRowVM(GitWorktreeEntry entry)
    {
        Entry = entry;
        Label = (string.IsNullOrEmpty(entry.Branch) ? "(detached)" : entry.Branch) + "  " + entry.Path;
    }
    public GitWorktreeEntry Entry { get; }
    public string Label { get; }
    public string Path => Entry.Path;
}

public partial class MainViewModel
{
    private LocalFolderGrantStore? _folderGrants;
    private NetworkPublishGrantStore? _networkPublish;
    private IGitHubPullRequestApi _prApi = new SetupRequiredGitHubPullRequestApi();
    private string _projectStatus = "";
    private string _projectRootLabel = "No folder granted for this profile.";
    private string _projectTab = "Files";
    private string _projectPreview = "";
    private string _projectRelDir = ".";
    private string _gitBranchLabel = "";
    private string _secretShareWarning = SecretContentReview.LimitsCopy + " " + SecretContentReview.KeysExclusionCopy;
    private string _commitMessage = "";
    private string _worktreeBranch = "";
    private string _worktreeBaseRev = "HEAD";
    private string _gitSafetyLabel = "";
    private string _lastCheckpointResult = "";
    private string _lastPushResult = "";
    private string _pushPreviewLabel = "";
    private string _prTitle = "";
    private string _prBody = "";
    private string _prBaseBranch = "main";
    private bool _networkPublishEnabled;
    private bool _projectWriteEnabled;
    private GitPushPreview? _lastPushPreview;
    private GitPushResult? _lastVerifiedPush;
    private ProjectFileRowVM? _selectedProjectFile;
    private GitChangeRowVM? _selectedGitChange;
    private GitWorktreeRowVM? _selectedWorktree;

    public void AttachFolderGrants(LocalFolderGrantStore store)
    {
        _folderGrants = store;
        GrantFolderCommand = new RelayCommand(_ => _ = GrantFolderAsync());
        RevokeFolderCommand = new RelayCommand(_ => _ = RevokeFolderAsync(), _ => _folderGrants is not null);
        RefreshProjectCommand = new RelayCommand(_ => _ = RefreshProjectAsync());
        OpenProjectEntryCommand = new RelayCommand(p => _ = OpenProjectEntryAsync(p));
        ShareProjectFileCommand = new RelayCommand(p => _ = ShareProjectFileAsync(p), _ => SelectedConversation is not null);
        OverwriteProjectFileCommand = new RelayCommand(p => _ = OverwriteProjectFileAsync(p));
        DeleteProjectFileCommand = new RelayCommand(p => _ = DeleteProjectFileAsync(p));
        ShowProjectFilesTabCommand = new RelayCommand(_ => { ProjectTab = "Files"; });
        ShowProjectPreviewTabCommand = new RelayCommand(_ => { ProjectTab = "Preview"; });
        ShowProjectChangesTabCommand = new RelayCommand(_ => { ProjectTab = "Changes"; _ = RefreshGitAsync(); });
        ToggleProjectWriteCommand = new RelayCommand(_ => _ = ToggleProjectWriteAsync());
        ShowProjectsCommand = new RelayCommand(_ => { Nav = MainNav.Projects; _ = RefreshProjectAsync(); });
        StageSelectedCommand = new RelayCommand(_ => _ = StageSelectedAsync());
        UnstageSelectedCommand = new RelayCommand(_ => _ = UnstageSelectedAsync());
        SaveCheckpointCommand = new RelayCommand(_ => _ = SaveCheckpointAsync());
        PreviewPushCommand = new RelayCommand(_ => _ = PreviewPushAsync());
        ConfirmPushCommand = new RelayCommand(_ => _ = ConfirmPushAsync());
        EnableNetworkPublishCommand = new RelayCommand(_ => _ = EnableNetworkPublishAsync());
        RevokeNetworkPublishCommand = new RelayCommand(_ => _ = RevokeNetworkPublishAsync());
        CreatePullRequestCommand = new RelayCommand(_ => _ = CreatePullRequestAsync());
        CreateWorktreeCommand = new RelayCommand(_ => _ = CreateWorktreeAsync());
        RemoveWorktreeCommand = new RelayCommand(_ => _ = RemoveWorktreeAsync());
        RefreshWorktreesCommand = new RelayCommand(_ => _ = RefreshWorktreesAsync());
        Raise(nameof(GrantFolderCommand));
        Raise(nameof(RevokeFolderCommand));
        Raise(nameof(RefreshProjectCommand));
        Raise(nameof(ShowProjectsCommand));
        Raise(nameof(StageSelectedCommand));
        Raise(nameof(UnstageSelectedCommand));
        Raise(nameof(SaveCheckpointCommand));
        Raise(nameof(PreviewPushCommand));
        Raise(nameof(ConfirmPushCommand));
        Raise(nameof(EnableNetworkPublishCommand));
        Raise(nameof(RevokeNetworkPublishCommand));
        Raise(nameof(CreatePullRequestCommand));
        Raise(nameof(CreateWorktreeCommand));
        Raise(nameof(RemoveWorktreeCommand));
        Raise(nameof(RefreshWorktreesCommand));
    }

    public void AttachNetworkPublish(NetworkPublishGrantStore store, IGitHubPullRequestApi? prApi = null)
    {
        _networkPublish = store;
        if (prApi is not null) _prApi = prApi;
        _ = RefreshNetworkPublishStateAsync();
    }

    public ObservableCollection<ProjectFileRowVM> ProjectFiles { get; } = new();
    public ObservableCollection<GitChangeRowVM> GitChanges { get; } = new();
    public ObservableCollection<GitWorktreeRowVM> GitWorktrees { get; } = new();
    public ICommand? GrantFolderCommand { get; private set; }
    public ICommand? RevokeFolderCommand { get; private set; }
    public ICommand? RefreshProjectCommand { get; private set; }
    public ICommand? OpenProjectEntryCommand { get; private set; }
    public ICommand? ShareProjectFileCommand { get; private set; }
    public ICommand? OverwriteProjectFileCommand { get; private set; }
    public ICommand? DeleteProjectFileCommand { get; private set; }
    public ICommand? ShowProjectFilesTabCommand { get; private set; }
    public ICommand? ShowProjectPreviewTabCommand { get; private set; }
    public ICommand? ShowProjectChangesTabCommand { get; private set; }
    public ICommand? ToggleProjectWriteCommand { get; private set; }
    public ICommand? ShowProjectsCommand { get; private set; }
    public ICommand? StageSelectedCommand { get; private set; }
    public ICommand? UnstageSelectedCommand { get; private set; }
    public ICommand? SaveCheckpointCommand { get; private set; }
    public ICommand? PreviewPushCommand { get; private set; }
    public ICommand? ConfirmPushCommand { get; private set; }
    public ICommand? EnableNetworkPublishCommand { get; private set; }
    public ICommand? RevokeNetworkPublishCommand { get; private set; }
    public ICommand? CreatePullRequestCommand { get; private set; }
    /// <summary>Legacy name kept for older acceptance probes — routes to PreviewPush.</summary>
    public ICommand? ExplainPushCommand => PreviewPushCommand;
    public ICommand? CreateWorktreeCommand { get; private set; }
    public ICommand? RemoveWorktreeCommand { get; private set; }
    public ICommand? RefreshWorktreesCommand { get; private set; }

    public Visibility ProjectsVisibility =>
        ChatShellVisibility == Visibility.Visible && Nav == MainNav.Projects ? Visibility.Visible : Visibility.Collapsed;

    public string ProjectStatus { get => _projectStatus; private set => Set(ref _projectStatus, value); }
    public string ProjectRootLabel { get => _projectRootLabel; private set => Set(ref _projectRootLabel, value); }
    public string ProjectTab
    {
        get => _projectTab;
        set
        {
            Set(ref _projectTab, value);
            Raise(nameof(ProjectFilesTabVisibility));
            Raise(nameof(ProjectPreviewTabVisibility));
            Raise(nameof(ProjectChangesTabVisibility));
        }
    }
    public Visibility ProjectFilesTabVisibility => ProjectTab is "Files" or "Preview" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProjectPreviewTabVisibility => ProjectTab is "Preview" or "Files" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProjectChangesTabVisibility => ProjectTab == "Changes" ? Visibility.Visible : Visibility.Collapsed;
    public string ProjectPreview { get => _projectPreview; set => Set(ref _projectPreview, value); }
    public string GitBranchLabel { get => _gitBranchLabel; private set => Set(ref _gitBranchLabel, value); }
    public string SecretShareWarning { get => _secretShareWarning; private set => Set(ref _secretShareWarning, value); }
    public string CommitMessage { get => _commitMessage; set => Set(ref _commitMessage, value); }
    public string WorktreeBranch { get => _worktreeBranch; set => Set(ref _worktreeBranch, value); }
    public string WorktreeBaseRev { get => _worktreeBaseRev; set => Set(ref _worktreeBaseRev, value); }
    public string GitSafetyLabel { get => _gitSafetyLabel; private set => Set(ref _gitSafetyLabel, value); }
    public string LastCheckpointResult { get => _lastCheckpointResult; private set => Set(ref _lastCheckpointResult, value); }
    public string LastPushResult { get => _lastPushResult; private set => Set(ref _lastPushResult, value); }
    public string PushPreviewLabel { get => _pushPreviewLabel; private set => Set(ref _pushPreviewLabel, value); }
    public string PrTitle { get => _prTitle; set => Set(ref _prTitle, value); }
    public string PrBody { get => _prBody; set => Set(ref _prBody, value); }
    public string PrBaseBranch { get => _prBaseBranch; set => Set(ref _prBaseBranch, value); }
    public bool NetworkPublishEnabled
    {
        get => _networkPublishEnabled;
        private set
        {
            Set(ref _networkPublishEnabled, value);
            Raise(nameof(NetworkPublishLabel));
        }
    }
    public string NetworkPublishLabel => NetworkPublishEnabled
        ? "Network publish enabled (each push still confirms)"
        : "Network publish off — enable before push";
    public bool ProjectWriteEnabled { get => _projectWriteEnabled; private set { Set(ref _projectWriteEnabled, value); Raise(nameof(ProjectWriteLabel)); } }
    public string ProjectWriteLabel => ProjectWriteEnabled ? "Write enabled (confirm still required)" : "Read only — enable write";
    public string ProjectHonesty =>
        "Files stay on this PC. Selecting a folder never uploads. Local processing is not the same as a local-only model — sharing to chat is deliberate. " +
        "Folder grant is not a shell. Save checkpoint is a local commit only — not push. " +
        "Push/PR need a separate network publish grant and explicit confirmation. " +
        "Local terminal is a separate explicit opt-in (default off) — not limited to the folder grant, not a gateway shell bridge.";

    public ProjectFileRowVM? SelectedProjectFile
    {
        get => _selectedProjectFile;
        set { Set(ref _selectedProjectFile, value); if (value is { Entry.IsDirectory: false }) _ = PreviewSelectedAsync(value); }
    }

    public GitChangeRowVM? SelectedGitChange
    {
        get => _selectedGitChange;
        set
        {
            Set(ref _selectedGitChange, value);
            if (value is not null)
            {
                value.Selected = true;
                _ = ShowGitDiffAsync(value);
            }
        }
    }

    public GitWorktreeRowVM? SelectedWorktree
    {
        get => _selectedWorktree;
        set => Set(ref _selectedWorktree, value);
    }

    public string? CurrentProjectPathHint { get; internal set; }
    public string? CurrentProjectBranchHint { get; private set; }

    private ProfileKey? ActiveProjectProfile()
    {
        if (SelectedConversation is { } c) return c.Profile;
        return ActiveProfileKey();
    }

    private async Task GrantFolderAsync()
    {
        if (_folderGrants is null) return;
        var key = ActiveProjectProfile();
        if (key is null) { ProjectStatus = "Select a profile first."; return; }
        var dlg = new OpenFolderDialog { Title = "Grant a project folder on this PC (not whole drive/home)" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var mode = ProjectWriteEnabled ? FolderGrantMode.ReadWrite : FolderGrantMode.Read;
            var grant = await _folderGrants.GrantAsync(key.Value, dlg.FolderName, mode).ConfigureAwait(true);
            ProjectStatus = "Granted. Revoke anytime. No automatic backups.";
            CurrentProjectPathHint = grant.CanonicalRoot;
            RaiseIdentity();
            await RefreshProjectAsync().ConfigureAwait(true);
        }
        catch (FolderGrantException ex)
        {
            ProjectStatus = ex.Message;
            MessageBox.Show(ex.Message, "Folder grant", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RevokeFolderAsync()
    {
        if (_folderGrants is null) return;
        var key = ActiveProjectProfile();
        if (key is null) return;
        var plain = !_advanced;     // Simple view says it in everyday words
        if (MessageBox.Show(plain ? "Stop sharing this folder with Hermes? Your files are not deleted or changed."
                                  : "Revoke this profile’s folder grant? (Does not delete your files.)",
                plain ? "Stop sharing" : "Revoke grant", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await _folderGrants.RevokeAsync(key.Value).ConfigureAwait(true);
        ProjectFiles.Clear();
        GitChanges.Clear();
        GitWorktrees.Clear();
        ProjectPreview = "";
        LastCheckpointResult = "";
        GitSafetyLabel = "";
        CurrentProjectPathHint = null;
        CurrentProjectBranchHint = null;
        ProjectRootLabel = "No folder granted for this profile.";
        ProjectStatus = "Grant revoked.";
        RaiseIdentity();
    }

    private async Task ToggleProjectWriteAsync()
    {
        if (_folderGrants is null) return;
        var key = ActiveProjectProfile();
        if (key is null) return;
        var existing = await _folderGrants.GetAsync(key.Value).ConfigureAwait(true);
        if (existing is null)
        {
            ProjectWriteEnabled = !ProjectWriteEnabled;
            return;
        }
        var next = existing.Mode == FolderGrantMode.ReadWrite ? FolderGrantMode.Read : FolderGrantMode.ReadWrite;
        if (next == FolderGrantMode.ReadWrite &&
            MessageBox.Show("Enable write for this grant? Overwrite and delete still require confirmation every time. No automatic backups.",
                "Enable write", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await _folderGrants.GrantAsync(key.Value, existing.CanonicalRoot, next).ConfigureAwait(true);
        ProjectWriteEnabled = next == FolderGrantMode.ReadWrite;
        ProjectStatus = next == FolderGrantMode.ReadWrite ? "Write enabled — confirmations still required." : "Read only.";
    }

    private async Task RefreshProjectAsync()
    {
        if (_folderGrants is null) return;
        var key = ActiveProjectProfile();
        if (key is null)
        {
            ProjectRootLabel = "No profile selected.";
            return;
        }
        var grant = await _folderGrants.GetAsync(key.Value).ConfigureAwait(true);
        if (grant is null)
        {
            ProjectRootLabel = "No folder granted for this profile.";
            CurrentProjectPathHint = null;
            CurrentProjectBranchHint = null;
            ProjectFiles.Clear();
            GitChanges.Clear();
            GitWorktrees.Clear();
            LastCheckpointResult = "";
            GitSafetyLabel = "";
            RaiseIdentity();
            return;
        }
        ProjectWriteEnabled = grant.Mode == FolderGrantMode.ReadWrite;
        ProjectRootLabel = $"{grant.CanonicalRoot} · {grant.Mode} · machine {grant.MachineId}";
        CurrentProjectPathHint = grant.CanonicalRoot;
        var ws = new ProjectWorkspace(_folderGrants, key.Value);
        var list = await ws.ListAsync(_projectRelDir).ConfigureAwait(true);
        ProjectFiles.Clear();
        if (!list.Ok)
        {
            ProjectStatus = list.Error ?? "List failed.";
            RaiseIdentity();
            return;
        }
        foreach (var e in list.Entries) ProjectFiles.Add(new ProjectFileRowVM(e));
        ProjectStatus = list.Entries.Count == 0 ? "Empty folder." : $"{list.Entries.Count} items.";
        if (ProjectTab == "Changes") await RefreshGitAsync().ConfigureAwait(true);
        else
        {
            var git = await new GitChangesReader().ReadAsync(grant.CanonicalRoot).ConfigureAwait(true);
            CurrentProjectBranchHint = git.Ok ? git.Branch : null;
            GitBranchLabel = git.Ok ? (git.Branch ?? "(detached)") : "";
        }
        RaiseIdentity();
    }

    private async Task OpenProjectEntryAsync(object? param)
    {
        if (param is not ProjectFileRowVM row) return;
        if (row.Entry.IsDirectory)
        {
            _projectRelDir = row.RelativePath;
            await RefreshProjectAsync().ConfigureAwait(true);
            return;
        }
        ProjectTab = "Preview";
        await PreviewSelectedAsync(row).ConfigureAwait(true);
    }

    private async Task PreviewSelectedAsync(ProjectFileRowVM row)
    {
        var key = ActiveProjectProfile();
        if (key is null || _folderGrants is null) return;
        var ws = new ProjectWorkspace(_folderGrants, key.Value);
        var preview = await ws.PreviewTextAsync(row.RelativePath).ConfigureAwait(true);
        ProjectPreview = preview.Ok ? preview.Text! : (preview.Error ?? "Preview unavailable.");
        SelectedProjectFile = row;
    }

    private async Task ShareProjectFileAsync(object? param)
    {
        var row = param as ProjectFileRowVM ?? SelectedProjectFile;
        if (row is null || row.Entry.IsDirectory) return;
        var convo = SelectedConversation;
        if (convo is null) { MessageBox.Show("Open a chat first to share into the attachment queue."); return; }
        var key = ActiveProjectProfile();
        if (key is null || _folderGrants is null) return;
        var ws = new ProjectWorkspace(_folderGrants, key.Value);
        var preview = await ws.PreviewTextAsync(row.RelativePath).ConfigureAwait(true);
        if (!preview.Ok)
        {
            MessageBox.Show(preview.Error ?? "Cannot share this file.");
            return;
        }
        var decision = ProjectShareGate.Decide(preview.Text!, row.Entry.Name, acknowledgeRisk: false);
        SecretShareWarning = decision.Message;
        if (!decision.Allowed)
        {
            // Keys-exclusion: acknowledge records understanding only — never shares suspected keys.
            if (decision.HardBlocked)
            {
                MessageBox.Show(decision.Message + "\n\n" + SecretContentReview.KeysExclusionCopy,
                    "Sharing blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
                _ = ProjectShareGate.Decide(preview.Text!, row.Entry.Name, acknowledgeRisk: true);
                ProjectStatus = "Share blocked (keys exclusion). Acknowledgement does not share secrets.";
                return;
            }
            return;
        }
        try
        {
            var bytes = Encoding.UTF8.GetBytes(preview.Text!);
            var att = new OutgoingAttachment(Guid.NewGuid().ToString("N"), row.Entry.Name, GuessMime(row.Entry.Name), bytes);
            var err = _attachQueue.TryAdd(convo.Profile, _gate, att);
            if (err is not null) MessageBox.Show(err);
            else
            {
                RefreshPendingAttachments();
                ((RelayCommand)SendCommand).Refresh();
                ProjectStatus = "Queued for chat (not uploaded until Send). " + SecretContentReview.LimitsCopy;
                Nav = MainNav.Chats;
            }
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    private async Task OverwriteProjectFileAsync(object? param)
    {
        var row = param as ProjectFileRowVM ?? SelectedProjectFile;
        if (row is null || row.Entry.IsDirectory) return;
        var key = ActiveProjectProfile();
        if (key is null || _folderGrants is null) return;
        var (grant, _, gate) = await new ProjectWorkspace(_folderGrants, key.Value).OpenMutationsAsync().ConfigureAwait(true);
        if (grant is null || gate is null) { MessageBox.Show("No writable grant."); return; }
        if (MessageBox.Show($"OVERWRITE this file?\n{row.RelativePath}\n\nNo automatic backup will be created.",
                "Confirm overwrite", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var bytes = Encoding.UTF8.GetBytes(ProjectPreview ?? "");
        var result = gate.Overwrite(row.RelativePath, bytes, confirmed: true);
        ProjectStatus = result.Ok ? "Overwritten (no backup)." : (result.Error ?? "Overwrite failed.");
        if (!result.Ok) MessageBox.Show(ProjectStatus);
    }

    private async Task DeleteProjectFileAsync(object? param)
    {
        var row = param as ProjectFileRowVM ?? SelectedProjectFile;
        if (row is null || row.Entry.IsDirectory) return;
        var key = ActiveProjectProfile();
        if (key is null || _folderGrants is null) return;
        var (grant, _, gate) = await new ProjectWorkspace(_folderGrants, key.Value).OpenMutationsAsync().ConfigureAwait(true);
        if (grant is null || gate is null) { MessageBox.Show("No writable grant."); return; }
        if (MessageBox.Show($"DELETE this file?\n{row.RelativePath}\n\nNo automatic backup will be created.",
                "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var result = gate.Delete(row.RelativePath, confirmed: true);
        ProjectStatus = result.Ok ? "Deleted (no backup)." : (result.Error ?? "Delete failed.");
        if (result.Ok) await RefreshProjectAsync().ConfigureAwait(true);
        else MessageBox.Show(ProjectStatus);
    }

    private async Task RefreshGitAsync()
    {
        GitChanges.Clear();
        var key = ActiveProjectProfile();
        if (key is null || _folderGrants is null) return;
        var grant = await _folderGrants.GetAsync(key.Value).ConfigureAwait(true);
        if (grant is null) { GitBranchLabel = ""; GitSafetyLabel = ""; return; }
        var ops = new GitLocalOps();
        var safety = await ops.AssessAsync(grant.CanonicalRoot).ConfigureAwait(true);
        GitSafetyLabel = safety.Summary;
        var snap = await new GitChangesReader().ReadAsync(grant.CanonicalRoot).ConfigureAwait(true);
        if (!snap.Ok)
        {
            GitBranchLabel = snap.Error ?? "Not a Git repo / git unavailable.";
            CurrentProjectBranchHint = null;
            return;
        }
        GitBranchLabel = "Branch " + (snap.Branch ?? "(detached)") + " · Save checkpoint ≠ push · push needs network grant";
        CurrentProjectBranchHint = snap.Branch;
        foreach (var e in snap.Entries) GitChanges.Add(new GitChangeRowVM(e));
        await RefreshWorktreesAsync().ConfigureAwait(true);
        RaiseIdentity();
    }

    private async Task ShowGitDiffAsync(GitChangeRowVM row)
    {
        var key = ActiveProjectProfile();
        if (key is null || _folderGrants is null) return;
        var grant = await _folderGrants.GetAsync(key.Value).ConfigureAwait(true);
        if (grant is null) return;
        var reader = new GitChangesReader();
        var parts = new StringBuilder();
        if (row.Entry.InWorkingTree)
        {
            var u = await reader.DiffUnstagedAsync(grant.CanonicalRoot, row.Path).ConfigureAwait(true);
            parts.AppendLine("--- unstaged ---");
            parts.AppendLine(u.Ok ? u.Text : u.Error);
        }
        if (row.Entry.InIndex)
        {
            var s = await reader.DiffStagedAsync(grant.CanonicalRoot, row.Path).ConfigureAwait(true);
            parts.AppendLine("--- staged ---");
            parts.AppendLine(s.Ok ? s.Text : s.Error);
        }
        ProjectPreview = parts.ToString();
        ProjectTab = "Preview";
    }

    private List<string> SelectedChangePaths()
    {
        var selected = GitChanges.Where(c => c.Selected).Select(c => c.Path).Distinct(StringComparer.Ordinal).ToList();
        if (selected.Count == 0 && SelectedGitChange is not null)
            selected.Add(SelectedGitChange.Path);
        return selected;
    }

    private async Task<(ProfileKey Key, LocalFolderGrant Grant, PathBoundary Boundary)?> RequireGitGrantAsync()
    {
        if (_folderGrants is null) return null;
        var key = ActiveProjectProfile();
        if (key is null) { ProjectStatus = "Select a profile first."; return null; }
        var grant = await _folderGrants.GetAsync(key.Value).ConfigureAwait(true);
        if (grant is null) { ProjectStatus = "No folder grant for this profile."; return null; }
        if (!Directory.Exists(grant.CanonicalRoot))
        {
            ProjectStatus = "Granted path missing — grant fails closed.";
            return null;
        }
        return (key.Value, grant, new PathBoundary(grant.CanonicalRoot));
    }

    private async Task StageSelectedAsync()
    {
        var ctx = await RequireGitGrantAsync().ConfigureAwait(true);
        if (ctx is null) return;
        var paths = SelectedChangePaths();
        if (paths.Count == 0) { ProjectStatus = "Select exact files to stage (checkbox or list selection)."; return; }
        var result = await new GitLocalOps().StageExactAsync(ctx.Value.Grant.CanonicalRoot, ctx.Value.Boundary, paths).ConfigureAwait(true);
        ProjectStatus = result.Ok ? (result.Detail ?? "Staged.") : (result.Error ?? "Stage failed.");
        if (!result.Ok) MessageBox.Show(ProjectStatus, "Stage", MessageBoxButton.OK, MessageBoxImage.Warning);
        await RefreshGitAsync().ConfigureAwait(true);
    }

    private async Task UnstageSelectedAsync()
    {
        var ctx = await RequireGitGrantAsync().ConfigureAwait(true);
        if (ctx is null) return;
        var paths = SelectedChangePaths();
        if (paths.Count == 0) { ProjectStatus = "Select exact files to unstage."; return; }
        var result = await new GitLocalOps().UnstageExactAsync(ctx.Value.Grant.CanonicalRoot, ctx.Value.Boundary, paths).ConfigureAwait(true);
        ProjectStatus = result.Ok ? (result.Detail ?? "Unstaged.") : (result.Error ?? "Unstage failed.");
        if (!result.Ok) MessageBox.Show(ProjectStatus, "Unstage", MessageBoxButton.OK, MessageBoxImage.Warning);
        await RefreshGitAsync().ConfigureAwait(true);
    }

    private async Task SaveCheckpointAsync()
    {
        var ctx = await RequireGitGrantAsync().ConfigureAwait(true);
        if (ctx is null) return;
        var paths = SelectedChangePaths();
        if (paths.Count == 0) { ProjectStatus = "Select the exact files for this local checkpoint."; return; }
        if (string.IsNullOrWhiteSpace(CommitMessage) || CommitMessage.Trim().Length < 3)
        {
            MessageBox.Show("Enter a meaningful commit message first.", "Save checkpoint", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            "Save checkpoint creates a LOCAL Git commit only.\nIt does NOT push or publish.\n\nFiles:\n"
            + string.Join("\n", paths.Take(12))
            + (paths.Count > 12 ? "\n…" : "")
            + "\n\nMessage: " + CommitMessage.Trim(),
            "Confirm Save checkpoint",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var result = await new GitLocalOps().SaveCheckpointAsync(
            ctx.Value.Grant.CanonicalRoot,
            ctx.Value.Boundary,
            paths,
            CommitMessage,
            confirmed: true).ConfigureAwait(true);

        if (result.Ok)
        {
            LastCheckpointResult = $"Local checkpoint saved{(result.Sha is null ? "" : " · " + result.Sha)}. Not pushed. Not published.";
            ProjectStatus = LastCheckpointResult;
            CommitMessage = "";
        }
        else
        {
            LastCheckpointResult = "";
            ProjectStatus = result.Error ?? "Checkpoint failed.";
            MessageBox.Show(ProjectStatus, "Save checkpoint", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        await RefreshGitAsync().ConfigureAwait(true);
    }

    private async Task RefreshNetworkPublishStateAsync()
    {
        if (_networkPublish is null) { NetworkPublishEnabled = false; return; }
        var key = ActiveProjectProfile();
        if (key is null) { NetworkPublishEnabled = false; return; }
        NetworkPublishEnabled = await _networkPublish.IsEnabledAsync(key.Value).ConfigureAwait(true);
    }

    /// <summary>Synthetic acceptance only — enables network publish without MessageBox.</summary>
    public async Task AcceptanceEnableNetworkPublishAsync()
    {
        if (_networkPublish is null) return;
        var key = ActiveProjectProfile();
        if (key is null) return;
        await _networkPublish.EnableAsync(key.Value, warningAcknowledged: true).ConfigureAwait(true);
        await RefreshNetworkPublishStateAsync().ConfigureAwait(true);
    }

    /// <summary>Synthetic acceptance only — builds push preview without MessageBox confirm/execute.</summary>
    public async Task AcceptancePreviewPushAsync()
    {
        var ctx = await RequireGitGrantAsync().ConfigureAwait(true);
        if (ctx is null) return;
        var preview = await new GitPushOps().PreviewAsync(ctx.Value.Grant.CanonicalRoot, ctx.Value.Boundary).ConfigureAwait(true);
        _lastPushPreview = preview.Ok ? preview : null;
        PushPreviewLabel = preview.Ok
            ? $"Preview: {preview.Branch} @ {preview.LocalSha?[..Math.Min(7, preview.LocalSha.Length)]} → {preview.RemoteName} ({preview.RemoteUrlDisplay})"
            : (preview.Error ?? "Push preview failed.");
        ProjectStatus = PushPreviewLabel;
    }

    private async Task EnableNetworkPublishAsync()
    {
        if (_networkPublish is null) { ProjectStatus = "Network publish store not attached."; return; }
        var key = ActiveProjectProfile();
        if (key is null)
        {
            MessageBox.Show("Select a profile first.", "Network publish", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var body = NetworkPublishGrantStore.WarningCopy + "\n\n" + NetworkPublishGrantStore.NotFromCheckpointCopy;
        if (MessageBox.Show(body, "Enable network publish?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var result = await _networkPublish.EnableAsync(key.Value, warningAcknowledged: true).ConfigureAwait(true);
        if (!result.Ok)
            MessageBox.Show(result.Error ?? "Could not enable.", "Network publish", MessageBoxButton.OK, MessageBoxImage.Warning);
        await RefreshNetworkPublishStateAsync().ConfigureAwait(true);
        ProjectStatus = NetworkPublishEnabled ? "Network publish enabled for this profile." : (result.Error ?? "Not enabled.");
    }

    private async Task RevokeNetworkPublishAsync()
    {
        if (_networkPublish is null) return;
        var key = ActiveProjectProfile();
        if (key is null) return;
        if (MessageBox.Show("Revoke network publish for this profile? Folder grant is unchanged.",
                "Revoke network publish", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await _networkPublish.RevokeAsync(key.Value).ConfigureAwait(true);
        _lastPushPreview = null;
        _lastVerifiedPush = null;
        PushPreviewLabel = "";
        await RefreshNetworkPublishStateAsync().ConfigureAwait(true);
        ProjectStatus = "Network publish revoked.";
    }

    private async Task PreviewPushAsync()
    {
        var ctx = await RequireGitGrantAsync().ConfigureAwait(true);
        if (ctx is null) return;
        await RefreshNetworkPublishStateAsync().ConfigureAwait(true);
        var preview = await new GitPushOps().PreviewAsync(ctx.Value.Grant.CanonicalRoot, ctx.Value.Boundary).ConfigureAwait(true);
        if (!preview.Ok)
        {
            _lastPushPreview = null;
            PushPreviewLabel = "";
            LastPushResult = "";
            ProjectStatus = preview.Error ?? "Push preview failed.";
            MessageBox.Show(ProjectStatus, "Push preview", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _lastPushPreview = preview;
        PushPreviewLabel =
            "Branch: " + preview.Branch + "\n" +
            "Local revision: " + preview.LocalSha + "\n" +
            "Remote: " + preview.RemoteName + " → " + preview.RemoteUrlDisplay + "\n" +
            "Upstream ref: " + preview.UpstreamRef + "\n\n" +
            preview.WarningCopy;
        ProjectStatus = "Push preview ready — confirm to publish (separate from Save checkpoint).";
        MessageBox.Show(PushPreviewLabel + "\n\n" +
            (NetworkPublishEnabled
                ? "Use Confirm push… next. Each push asks again."
                : "Enable network publish first (separate from folder grant)."),
            "Push preview (not published yet)", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task ConfirmPushAsync()
    {
        var ctx = await RequireGitGrantAsync().ConfigureAwait(true);
        if (ctx is null) return;
        await RefreshNetworkPublishStateAsync().ConfigureAwait(true);
        if (!NetworkPublishEnabled)
        {
            MessageBox.Show("Enable network publish for this profile first. Folder grant alone does not authorise push.",
                "Push", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_lastPushPreview is not { Ok: true, Fingerprint: not null })
        {
            MessageBox.Show("Run Push preview first so the exact remote destination is shown.",
                "Push", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var p = _lastPushPreview;
        var confirm = MessageBox.Show(
            "Publish this branch to the remote?\n\n" +
            "Branch: " + p.Branch + "\n" +
            "Local revision: " + p.LocalSha + "\n" +
            "Destination: " + p.RemoteName + " → " + p.RemoteUrlDisplay + "\n\n" +
            p.WarningCopy + "\n\n" +
            "This is NOT Save checkpoint. Force/delete/mirror/--all/--tags are not used.",
            "Confirm push to remote",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        // Re-check profile+grant immediately before execution (fail closed on change).
        var ctx2 = await RequireGitGrantAsync().ConfigureAwait(true);
        if (ctx2 is null ||
            ctx2.Value.Key.GatewayId != ctx.Value.Key.GatewayId ||
            ctx2.Value.Key.ProfileId != ctx.Value.Key.ProfileId ||
            !string.Equals(ctx2.Value.Grant.CanonicalRoot, ctx.Value.Grant.CanonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            ProjectStatus = "Profile or folder grant changed — push refused.";
            MessageBox.Show(ProjectStatus, "Push", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!await (_networkPublish?.IsEnabledAsync(ctx2.Value.Key) ?? Task.FromResult(false)).ConfigureAwait(true))
        {
            ProjectStatus = "Network publish grant revoked — push refused.";
            MessageBox.Show(ProjectStatus, "Push", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = await new GitPushOps().PushAsync(
            ctx2.Value.Grant.CanonicalRoot,
            ctx2.Value.Boundary,
            p.Fingerprint!,
            confirmed: true,
            networkGranted: true).ConfigureAwait(true);

        if (result.Ok)
        {
            _lastVerifiedPush = result;
            LastPushResult = result.Detail ?? $"Pushed. Remote SHA {result.RemoteShaVerified} verified.";
            ProjectStatus = LastPushResult;
            LastCheckpointResult = ""; // keep push result distinct from checkpoint
        }
        else
        {
            _lastVerifiedPush = result.OutcomeUncertain ? result : null;
            LastPushResult = result.Error ?? "Push failed.";
            ProjectStatus = LastPushResult;
            MessageBox.Show(LastPushResult, result.OutcomeUncertain ? "Push uncertain" : "Push",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        await RefreshGitAsync().ConfigureAwait(true);
    }

    private async Task CreatePullRequestAsync()
    {
        var ctx = await RequireGitGrantAsync().ConfigureAwait(true);
        if (ctx is null) return;
        await RefreshNetworkPublishStateAsync().ConfigureAwait(true);
        if (!NetworkPublishEnabled)
        {
            MessageBox.Show("Enable network publish first.", "Create PR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_lastVerifiedPush is not { Ok: true, Published: true, RemoteShaVerified: not null })
        {
            MessageBox.Show("Create PR requires a verified successful push first (exact remote SHA). " +
                            GitHubPrIdentityParser.CompareUrlIsNotPrCopy,
                "Create PR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_lastPushPreview is not { Ok: true })
        {
            MessageBox.Show("Push preview destination is required for PR identity.", "Create PR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(PrTitle) || PrTitle.Trim().Length < 3)
        {
            MessageBox.Show("Enter a meaningful PR title first.", "Create PR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var baseBranch = string.IsNullOrWhiteSpace(PrBaseBranch) ? "main" : PrBaseBranch.Trim();
        var previewBody =
            "Create pull request?\n\n" +
            "Title: " + PrTitle.Trim() + "\n" +
            "Head: " + _lastPushPreview.Branch + "\n" +
            "Base: " + baseBranch + "\n" +
            "Remote: " + _lastPushPreview.RemoteUrlDisplay + "\n\n" +
            (string.IsNullOrWhiteSpace(PrBody) ? "(no body)" : PrBody.Trim()) + "\n\n" +
            GitHubPrIdentityParser.CompareUrlIsNotPrCopy;

        if (MessageBox.Show(previewBody, "Confirm create PR", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var guided = new GuidedPullRequestOps(_prApi);
        var result = await guided.CreateAfterVerifiedPushAsync(
            _lastVerifiedPush,
            _lastPushPreview.RemoteUrlDisplay ?? "",
            _lastPushPreview.Branch!,
            baseBranch,
            PrTitle.Trim(),
            PrBody ?? "",
            confirmed: true).ConfigureAwait(true);

        if (result.SetupRequired)
        {
            ProjectStatus = "PR setup required — " + (result.Error ?? SetupRequiredGitHubPullRequestApi.SetupCopy);
            MessageBox.Show(SetupRequiredGitHubPullRequestApi.SetupCopy, "Create PR — setup required",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (result.Duplicate)
        {
            ProjectStatus = result.Error ?? "Open PR already exists.";
            MessageBox.Show(ProjectStatus + (result.HtmlUrl is null ? "" : "\n" + result.HtmlUrl),
                "Create PR — duplicate", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!result.Ok)
        {
            ProjectStatus = result.Error ?? "Create PR failed.";
            MessageBox.Show(ProjectStatus, "Create PR", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ProjectStatus = $"PR #{result.Number} created · {result.HtmlUrl}";
        MessageBox.Show(ProjectStatus, "Create PR", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task RefreshWorktreesAsync()
    {
        GitWorktrees.Clear();
        var ctx = await RequireGitGrantAsync().ConfigureAwait(true);
        if (ctx is null) return;
        var list = await new GitLocalOps().ListWorktreesAsync(ctx.Value.Grant.CanonicalRoot).ConfigureAwait(true);
        if (!list.Ok) return;
        foreach (var e in list.Entries) GitWorktrees.Add(new GitWorktreeRowVM(e));
    }

    private async Task CreateWorktreeAsync()
    {
        var ctx = await RequireGitGrantAsync().ConfigureAwait(true);
        if (ctx is null) return;
        if (string.IsNullOrWhiteSpace(WorktreeBranch))
        {
            MessageBox.Show("Enter a branch name for the separate working copy.", "Separate working copy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new OpenFolderDialog
        {
            Title = "Choose EMPTY destination folder (separate consent — not an extension of the project grant)"
        };
        if (dlg.ShowDialog() != true) return;
        var dest = dlg.FolderName;

        var confirm = MessageBox.Show(
            "Create a separate working copy (Git worktree)?\n\n"
            + "Source (unchanged checkout): " + ctx.Value.Grant.CanonicalRoot + "\n"
            + "Destination (this consent only): " + dest + "\n"
            + "New branch: " + WorktreeBranch.Trim() + "\n"
            + "Base revision: " + (string.IsNullOrWhiteSpace(WorktreeBaseRev) ? "HEAD" : WorktreeBaseRev.Trim())
            + "\n\nThis does not switch your current folder grant. No force. No push.",
            "Confirm separate working copy",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var result = await new GitLocalOps().CreateWorktreeAsync(
            ctx.Value.Grant.CanonicalRoot,
            ctx.Value.Boundary,
            dest,
            destinationConsented: true,
            WorktreeBranch.Trim(),
            string.IsNullOrWhiteSpace(WorktreeBaseRev) ? "HEAD" : WorktreeBaseRev.Trim()).ConfigureAwait(true);

        ProjectStatus = result.Ok ? (result.Detail ?? "Created.") : (result.Error ?? "Create failed.");
        if (!result.Ok) MessageBox.Show(ProjectStatus, "Separate working copy", MessageBoxButton.OK, MessageBoxImage.Warning);
        else WorktreeBranch = "";
        await RefreshWorktreesAsync().ConfigureAwait(true);
    }

    private async Task RemoveWorktreeAsync()
    {
        var ctx = await RequireGitGrantAsync().ConfigureAwait(true);
        if (ctx is null) return;
        if (SelectedWorktree is null)
        {
            MessageBox.Show("Select a separate working copy to remove.", "Remove working copy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var path = SelectedWorktree.Path;
        var confirm = MessageBox.Show(
            "Remove this separate working copy?\n\n" + path
            + "\n\nDirty trees are refused (no force). Branch deletion is not performed.",
            "Confirm remove working copy",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var result = await new GitLocalOps().RemoveWorktreeAsync(
            ctx.Value.Grant.CanonicalRoot,
            ctx.Value.Boundary,
            path,
            confirmed: true,
            force: false).ConfigureAwait(true);
        ProjectStatus = result.Ok ? (result.Detail ?? "Removed.") : (result.Error ?? "Remove failed.");
        if (!result.Ok) MessageBox.Show(ProjectStatus, "Remove working copy", MessageBoxButton.OK, MessageBoxImage.Warning);
        await RefreshWorktreesAsync().ConfigureAwait(true);
    }

    internal void OnProjectsProfileChanged()
    {
        _projectRelDir = ".";
        _lastPushPreview = null;
        _lastVerifiedPush = null;
        PushPreviewLabel = "";
        LastPushResult = "";
        _ = RefreshNetworkPublishStateAsync();
        if (Nav == MainNav.Projects) _ = RefreshProjectAsync();
        else _ = RefreshProjectHintsAsync();
    }

    private async Task RefreshProjectHintsAsync()
    {
        if (_folderGrants is null) return;
        var key = ActiveProjectProfile();
        if (key is null) { CurrentProjectPathHint = null; CurrentProjectBranchHint = null; RaiseIdentity(); return; }
        var grant = await _folderGrants.GetAsync(key.Value).ConfigureAwait(true);
        CurrentProjectPathHint = grant?.CanonicalRoot;
        if (grant is not null)
        {
            var git = await new GitChangesReader().ReadAsync(grant.CanonicalRoot).ConfigureAwait(true);
            CurrentProjectBranchHint = git.Ok ? git.Branch : null;
        }
        else CurrentProjectBranchHint = null;
        RaiseIdentity();
    }
}
