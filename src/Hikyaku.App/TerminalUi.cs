using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Hikyaku.Core;

namespace Hikyaku.App;

public sealed class TrustedShellRow
{
    public TrustedShellRow(TrustedShell shell)
    {
        Shell = shell;
        Label = shell.DisplayName + " — " + shell.Path;
    }
    public TrustedShell Shell { get; }
    public string Label { get; }
    public string Path => Shell.Path;
}

public partial class MainViewModel
{
    private TerminalOptInStore? _terminalOptIn;
    private LocalTerminalController? _terminal;
    private bool _terminalDrawerOpen;
    private bool _terminalOptedIn;
    private string _terminalOutput = "";
    private string _terminalInput = "";
    private string _terminalStatus = "Terminal is default off. Opt in per gateway + profile + Local Windows.";
    private string _terminalHostLabel = "Host: Local Windows";
    private string _terminalCwdLabel = "Working directory: (not started)";
    private string _terminalSelection = "";
    private TrustedShellRow? _selectedShell;
    private long _lastPaintedGeneration = -1;
    private DispatcherTimer? _terminalUiTimer;
    private readonly TerminalContextStaging _terminalContext = new();
    private readonly TerminalUiCoalesce _terminalUiCoalesce = new();
    private int _lastResizeCols = -1;
    private int _lastResizeRows = -1;

    public void AttachTerminal(TerminalOptInStore store, LocalTerminalController controller)
    {
        _terminalOptIn = store;
        _terminal = controller;
        controller.Changed += OnTerminalChanged;
        EnableTerminalOptInCommand = new RelayCommand(_ => _ = EnableTerminalOptInAsync());
        RevokeTerminalOptInCommand = new RelayCommand(_ => _ = RevokeTerminalOptInAsync());
        ToggleTerminalDrawerCommand = new RelayCommand(_ => ToggleTerminalDrawer());
        StartTerminalCommand = new RelayCommand(_ => _ = StartTerminalAsync(), _ => _terminalOptedIn);
        CloseTerminalSessionCommand = new RelayCommand(_ => CloseTerminalSession(), _ => _terminal?.ActiveSession is not null);
        HideTerminalDrawerCommand = new RelayCommand(_ => HideTerminalDrawerKeepSession());
        SubmitTerminalLineCommand = new RelayCommand(_ => SubmitTerminalLine(), _ => _terminal?.ActiveSession is not null);
        PasteTerminalCommand = new RelayCommand(_ => PasteTerminal(), _ => _terminal?.ActiveSession is not null);
        AddTerminalSelectionToChatCommand = new RelayCommand(_ => AddTerminalSelectionToChat(), _ => !string.IsNullOrWhiteSpace(_terminalSelection));
        ShowRemoteTerminalUnavailableCommand = new RelayCommand(_ => ShowRemoteUnavailable());
        ClearStagedTerminalContextCommand = new RelayCommand(_ => ClearStagedTerminalContext(), _ => _terminalContext.HasPending);

        ShellChoices.Clear();
        foreach (var s in TrustedShellDiscovery.EnumerateInstalled())
            ShellChoices.Add(new TrustedShellRow(s));
        SelectedShell = ShellChoices.FirstOrDefault();

        _terminalUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _terminalUiTimer.Tick += (_, _) =>
        {
            if (_terminalUiCoalesce.Consume())
                PaintTerminalChrome();
            PaintTerminalOutput();
        };
        _terminalUiTimer.Start();

        Raise(nameof(EnableTerminalOptInCommand));
        Raise(nameof(RevokeTerminalOptInCommand));
        Raise(nameof(ToggleTerminalDrawerCommand));
        Raise(nameof(StartTerminalCommand));
        Raise(nameof(CloseTerminalSessionCommand));
        Raise(nameof(HideTerminalDrawerCommand));
        Raise(nameof(SubmitTerminalLineCommand));
        Raise(nameof(PasteTerminalCommand));
        Raise(nameof(AddTerminalSelectionToChatCommand));
        Raise(nameof(ShowRemoteTerminalUnavailableCommand));
        Raise(nameof(ClearStagedTerminalContextCommand));
        _ = RefreshTerminalOptInAsync();
    }

    public ObservableCollection<TrustedShellRow> ShellChoices { get; } = new();
    public ICommand? EnableTerminalOptInCommand { get; private set; }
    public ICommand? RevokeTerminalOptInCommand { get; private set; }
    public ICommand? ToggleTerminalDrawerCommand { get; private set; }
    public ICommand? StartTerminalCommand { get; private set; }
    public ICommand? CloseTerminalSessionCommand { get; private set; }
    public ICommand? HideTerminalDrawerCommand { get; private set; }
    public ICommand? SubmitTerminalLineCommand { get; private set; }
    public ICommand? PasteTerminalCommand { get; private set; }
    public ICommand? AddTerminalSelectionToChatCommand { get; private set; }
    public ICommand? ShowRemoteTerminalUnavailableCommand { get; private set; }
    public ICommand? ClearStagedTerminalContextCommand { get; private set; }

    public TerminalContextStaging TerminalContextStaging => _terminalContext;

    public string? StagedTerminalContextLabel => _terminalContext.PendingLabel;
    public Visibility StagedTerminalContextVisibility =>
        _terminalContext.HasPending ? Visibility.Visible : Visibility.Collapsed;

    public bool TerminalDrawerOpen
    {
        get => _terminalDrawerOpen;
        private set
        {
            if (_terminalDrawerOpen == value) return;
            Set(ref _terminalDrawerOpen, value);
            _terminal?.SetDrawerVisible(value);
            Raise(nameof(TerminalDrawerVisibility));
            Raise(nameof(TerminalDrawerToggleLabel));
        }
    }

    public Visibility TerminalDrawerVisibility =>
        TerminalDrawerOpen ? Visibility.Visible : Visibility.Collapsed;

    public string TerminalDrawerToggleLabel => TerminalDrawerOpen ? "Hide terminal drawer" : "Open terminal…";
    public bool TerminalOptedIn { get => _terminalOptedIn; private set { Set(ref _terminalOptedIn, value); Raise(nameof(TerminalOptInLabel)); } }
    public string TerminalOptInLabel => TerminalOptedIn
        ? "Local terminal enabled (not a folder sandbox; not a gateway shell bridge)"
        : "Local terminal: off (default)";
    public string TerminalOutput { get => _terminalOutput; private set => Set(ref _terminalOutput, value); }
    public string TerminalInput { get => _terminalInput; set => Set(ref _terminalInput, value); }
    public string TerminalStatus { get => _terminalStatus; private set => Set(ref _terminalStatus, value); }
    public string TerminalHostLabel { get => _terminalHostLabel; private set => Set(ref _terminalHostLabel, value); }
    public string TerminalCwdLabel { get => _terminalCwdLabel; private set => Set(ref _terminalCwdLabel, value); }
    public string TerminalSelection { get => _terminalSelection; set { Set(ref _terminalSelection, value); ((RelayCommand?)AddTerminalSelectionToChatCommand)?.Refresh(); } }
    public string TerminalHonesty =>
        TerminalOptInStore.WarningCopy + " " + TerminalOptInStore.NoGatewayBridgeCopy + " " +
        TerminalOptInStore.NotSandboxCopy + " " + TerminalOptInStore.RemoteUnavailableCopy;
    public TrustedShellRow? SelectedShell
    {
        get => _selectedShell;
        set => Set(ref _selectedShell, value);
    }

    public void SyncTerminalProfileBinding()
    {
        var key = ActiveProjectProfile();
        ClearTerminalUiForProfileSwitch(key);
        _terminal?.BindProfile(key);
        _ = RefreshTerminalOptInAsync();
    }

    public void NotifyTerminalViewport(double widthPx, double heightPx)
    {
        if (_terminal?.ActiveSession is null) return;
        // Approx monospace cell size for caption code font.
        var cols = (int)Math.Floor(widthPx / 7.2);
        var rows = (int)Math.Floor(heightPx / 14.0);
        var (c, r) = TerminalViewport.Clamp(cols, rows);
        if (c == _lastResizeCols && r == _lastResizeRows) return;
        _lastResizeCols = c;
        _lastResizeRows = r;
        _terminal.Resize(c, r);
    }

    /// <summary>Synthetic acceptance only — enables opt-in without MessageBox.</summary>
    public async Task AcceptanceEnableTerminalOptInAsync()
    {
        if (!App.UiAcceptanceMode || _terminalOptIn is null) return;
        var key = ActiveProjectProfile();
        if (key is null) return;
        var result = await _terminalOptIn.EnableAsync(key.Value, TerminalHostKind.LocalWindows, warningAcknowledged: true).ConfigureAwait(true);
        if (!result.Ok) return;
        var still = ActiveProjectProfile();
        if (still is null || still.Value.GatewayId != key.Value.GatewayId || still.Value.ProfileId != key.Value.ProfileId)
            return;
        TerminalOptedIn = true;
        TerminalStatus = "Opt-in saved (acceptance). Explicit Start still required.";
        TerminalDrawerOpen = true;
        ((RelayCommand?)StartTerminalCommand)?.Refresh();
    }

    /// <summary>Synthetic acceptance only — stage terminal context without MessageBox confirm.</summary>
    public void AcceptanceStageTerminalContext(string approvedPayload)
    {
        if (!App.UiAcceptanceMode) return;
        var convo = SelectedConversation;
        if (convo is null) return;
        _terminalContext.Stage(new TerminalShareDestination(convo.Profile, convo.SessionId), approvedPayload);
        Raise(nameof(StagedTerminalContextLabel));
        Raise(nameof(StagedTerminalContextVisibility));
        ((RelayCommand?)ClearStagedTerminalContextCommand)?.Refresh();
        ((RelayCommand)SendCommand).Refresh();
    }

    public void ShutdownTerminal()
    {
        _terminalUiTimer?.Stop();
        ClearStagedTerminalContext();
        TerminalShareGate.DiscardAll();
        _terminal?.CloseTerminal();
        _terminal?.Dispose();
    }

    private void ClearTerminalUiForProfileSwitch(ProfileKey? key)
    {
        TerminalShareGate.DiscardAll();
        ClearStagedTerminalContext();
        TerminalSelection = "";
        TerminalInput = "";
        TerminalOutput = "";
        _lastPaintedGeneration = -1;
        _lastResizeCols = -1;
        _lastResizeRows = -1;
        if (key is null)
            TerminalStatus = "Select a profile before enabling terminal.";
    }

    private void ClearStagedTerminalContext()
    {
        _terminalContext.Clear();
        Raise(nameof(StagedTerminalContextLabel));
        Raise(nameof(StagedTerminalContextVisibility));
        ((RelayCommand?)ClearStagedTerminalContextCommand)?.Refresh();
        ((RelayCommand)SendCommand).Refresh();
    }

    private async Task RefreshTerminalOptInAsync()
    {
        if (_terminalOptIn is null) return;
        var key = ActiveProjectProfile();
        if (key is null)
        {
            TerminalOptedIn = false;
            TerminalStatus = "Select a profile before enabling terminal.";
            return;
        }
        var enabled = await _terminalOptIn.IsEnabledAsync(key.Value, TerminalHostKind.LocalWindows).ConfigureAwait(true);
        var still = ActiveProjectProfile();
        if (still is null || still.Value.GatewayId != key.Value.GatewayId || still.Value.ProfileId != key.Value.ProfileId)
            return;
        TerminalOptedIn = enabled;
        TerminalHostLabel = "Host: Local Windows · gateway " + key.Value.GatewayId + " · profile " + key.Value.ProfileId;
        ((RelayCommand?)StartTerminalCommand)?.Refresh();
    }

    private async Task EnableTerminalOptInAsync()
    {
        if (_terminalOptIn is null) return;
        var key = ActiveProjectProfile();
        if (key is null)
        {
            MessageBox.Show("Select a profile first.", "Terminal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var body = TerminalOptInStore.WarningCopy + "\n\n" +
                   TerminalOptInStore.NoGatewayBridgeCopy + "\n\n" +
                   TerminalOptInStore.NotSandboxCopy + "\n\n" +
                   "Enable local terminal for this gateway + profile?";
        if (MessageBox.Show(body, "Enable local terminal?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var result = await _terminalOptIn.EnableAsync(key.Value, TerminalHostKind.LocalWindows, warningAcknowledged: true).ConfigureAwait(true);
        if (!result.Ok)
        {
            MessageBox.Show(result.Error ?? "Could not enable.", "Terminal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var still = ActiveProjectProfile();
        if (still is null || still.Value.GatewayId != key.Value.GatewayId || still.Value.ProfileId != key.Value.ProfileId)
        {
            TerminalStatus = "Profile changed during opt-in — not applied to the new profile.";
            return;
        }
        TerminalOptedIn = true;
        TerminalStatus = "Opt-in saved. Explicit Start is still required — preference alone never launches a shell.";
        TerminalDrawerOpen = true;
        ((RelayCommand?)StartTerminalCommand)?.Refresh();
    }

    private async Task RevokeTerminalOptInAsync()
    {
        if (_terminalOptIn is null || _terminal is null) return;
        var key = ActiveProjectProfile();
        if (key is null) return;
        if (MessageBox.Show("Revoke local terminal opt-in for this profile? Running session will stop and scrollback clears.",
                "Revoke terminal", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await _terminalOptIn.RevokeAsync(key.Value, TerminalHostKind.LocalWindows).ConfigureAwait(true);
        await _terminal.OnOptInChangedAsync(key.Value).ConfigureAwait(true);
        var still = ActiveProjectProfile();
        if (still is null || still.Value.GatewayId != key.Value.GatewayId || still.Value.ProfileId != key.Value.ProfileId)
            return;
        TerminalOptedIn = false;
        TerminalOutput = "";
        ClearStagedTerminalContext();
        TerminalShareGate.DiscardForProfile(key.Value);
        TerminalStatus = "Terminal opt-in revoked; session stopped.";
        ((RelayCommand?)StartTerminalCommand)?.Refresh();
        { ((RelayCommand?)CloseTerminalSessionCommand)?.Refresh(); RaiseRiskNotice(); }
    }

    private void ToggleTerminalDrawer()
    {
        if (!TerminalDrawerOpen && !_terminalOptedIn)
        {
            MessageBox.Show("Enable local terminal opt-in first (Projects). Folder grant / YOLO / Advanced do not authorise a shell.",
                "Terminal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        TerminalDrawerOpen = !TerminalDrawerOpen;
        // Hide preserves session; does not kill.
    }

    /// <summary>Simple view hides the drawer but never ends a session: a live terminal is shown as a notice with a Close button.</summary>
    private void CloseTerminalDrawerIfOpenKeepingSession() { if (TerminalDrawerOpen) TerminalDrawerOpen = false; }

    private void HideTerminalDrawerKeepSession()
    {
        TerminalDrawerOpen = false;
        TerminalStatus = "Drawer hidden — session kept if running. Use Close terminal to kill the PTY.";
    }

    private async Task StartTerminalAsync()
    {
        if (_terminal is null || _terminalOptIn is null) return;
        var key = ActiveProjectProfile();
        if (key is null) return;
        if (!await _terminalOptIn.IsEnabledAsync(key.Value, TerminalHostKind.LocalWindows).ConfigureAwait(true))
        {
            MessageBox.Show("Opt-in required first.", "Terminal");
            return;
        }
        var still = ActiveProjectProfile();
        if (still is null || still.Value.GatewayId != key.Value.GatewayId || still.Value.ProfileId != key.Value.ProfileId)
        {
            TerminalStatus = "Start cancelled — profile changed.";
            return;
        }
        var shell = SelectedShell?.Path ?? TrustedShellDiscovery.EnumerateInstalled().FirstOrDefault()?.Path;
        if (string.IsNullOrWhiteSpace(shell))
        {
            MessageBox.Show("No supported installed shell found.", "Terminal");
            return;
        }

        var cwd = CurrentProjectPathHint;
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
            cwd = Path.GetTempPath();

        TerminalDrawerOpen = true;
        TerminalStatus = "Starting local ConPTY…";
        var result = await _terminal.StartAsync(key.Value, TerminalHostKind.LocalWindows, shell, cwd, 100, 28).ConfigureAwait(true);
        still = ActiveProjectProfile();
        if (still is null || still.Value.GatewayId != key.Value.GatewayId || still.Value.ProfileId != key.Value.ProfileId)
        {
            _terminal.CloseTerminal();
            TerminalStatus = "Start cancelled — profile changed after launch.";
            return;
        }
        if (!result.Ok)
        {
            TerminalStatus = "Start failed: " + result.Error;
            MessageBox.Show(result.Error ?? "Start failed", "Terminal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TerminalCwdLabel = "Working directory: " + _terminal.WorkingDirectory + "  ·  " + TerminalOptInStore.NotSandboxCopy;
        TerminalHostLabel = "Host: " + _terminal.HostLabel + " · " + key.Value.GatewayId + "/" + key.Value.ProfileId;
        TerminalStatus = "Running. Paste does not submit Enter. Close terminal kills the session; hiding the drawer does not.";
        { ((RelayCommand?)CloseTerminalSessionCommand)?.Refresh(); RaiseRiskNotice(); }
        ((RelayCommand?)SubmitTerminalLineCommand)?.Refresh();
        ((RelayCommand?)PasteTerminalCommand)?.Refresh();
    }

    private void CloseTerminalSession()
    {
        _terminal?.CloseTerminal();
        TerminalOutput = "";
        TerminalCwdLabel = "Working directory: (closed)";
        TerminalStatus = "Terminal closed — child process tree stopped.";
        { ((RelayCommand?)CloseTerminalSessionCommand)?.Refresh(); RaiseRiskNotice(); }
        ((RelayCommand?)SubmitTerminalLineCommand)?.Refresh();
        ((RelayCommand?)PasteTerminalCommand)?.Refresh();
    }

    private void SubmitTerminalLine()
    {
        if (_terminal?.ActiveSession is null) return;
        var line = TerminalInput ?? "";
        _terminal.WriteRaw(line);
        _terminal.SubmitLine();
        TerminalInput = "";
    }

    private void PasteTerminal()
    {
        if (_terminal?.ActiveSession is null) return;
        try
        {
            if (Clipboard.ContainsText())
                _terminal.Paste(Clipboard.GetText());
        }
        catch (Exception ex)
        {
            TerminalStatus = "Paste failed: " + ex.Message;
        }
    }

    private void AddTerminalSelectionToChat()
    {
        var convo = SelectedConversation;
        if (convo is null)
        {
            MessageBox.Show("Open a chat first.", "Add to chat");
            return;
        }
        var dest = new TerminalShareDestination(convo.Profile, convo.SessionId);
        var preview = TerminalShareGate.Preview(TerminalSelection, dest);
        if (!preview.Ok)
        {
            MessageBox.Show(
                (preview.Error ?? "Blocked") + "\n\n" + SecretContentReview.KeysExclusionCopy +
                "\n\nRedacted preview:\n" + (preview.RedactedPreview ?? ""),
                "Add to chat — blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (preview.PreviewToken is not null)
                TerminalShareGate.Discard(preview.PreviewToken); // ack ≠ share; drop raw retention
            return;
        }

        var confirm = MessageBox.Show(
            "Stage selected terminal text for the next Send?\n\nDestination: " + preview.DestinationLabel +
            "\n\nRedacted preview:\n" + preview.RedactedPreview +
            "\n\nPending until Send — not delivered yet.\n\n" + SecretContentReview.LimitsCopy,
            "Confirm stage for chat", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            if (preview.PreviewToken is not null)
                TerminalShareGate.Discard(preview.PreviewToken);
            TerminalStatus = "Add to chat cancelled — pending preview discarded.";
            return;
        }

        // Revalidate destination after preview (profile/session may have changed).
        var dest2 = new TerminalShareDestination(
            SelectedConversation?.Profile ?? convo.Profile,
            SelectedConversation?.SessionId);
        var done = TerminalShareGate.Confirm(preview.PreviewToken!, dest2, acknowledgeRisk: false);
        if (!done.Ok)
        {
            MessageBox.Show(done.Error ?? "Share failed", "Add to chat", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Stage for next real prompt — no pretend delivered user bubble.
        _terminalContext.Stage(dest2, done.Payload!);
        Raise(nameof(StagedTerminalContextLabel));
        Raise(nameof(StagedTerminalContextVisibility));
        ((RelayCommand?)ClearStagedTerminalContextCommand)?.Refresh();
        ((RelayCommand)SendCommand).Refresh();
        TerminalStatus = "Terminal selection staged (pending until Send). No raw terminal cache.";
    }

    private static void ShowRemoteUnavailable() =>
        MessageBox.Show(TerminalOptInStore.RemoteUnavailableCopy, "Remote terminal", MessageBoxButton.OK, MessageBoxImage.Information);

    private void OnTerminalChanged()
    {
        // Mark only — timer coalesces dispatcher/UI work under high output.
        _terminalUiCoalesce.MarkChanged();
    }

    private void PaintTerminalChrome()
    {
        if (_terminal is null) return;
        TerminalStatus = _terminal.Status ?? TerminalStatus;
        if (!string.IsNullOrEmpty(_terminal.WorkingDirectory))
            TerminalCwdLabel = "Working directory: " + _terminal.WorkingDirectory + "  ·  " + TerminalOptInStore.NotSandboxCopy;
        { ((RelayCommand?)CloseTerminalSessionCommand)?.Refresh(); RaiseRiskNotice(); }
        ((RelayCommand?)SubmitTerminalLineCommand)?.Refresh();
        ((RelayCommand?)PasteTerminalCommand)?.Refresh();
    }

    private void PaintTerminalOutput()
    {
        if (_terminal is null) return;
        var gen = _terminal.OutputGeneration;
        if (gen == _lastPaintedGeneration) return;
        _lastPaintedGeneration = gen;
        TerminalOutput = _terminal.ScrollbackText;
    }
}
