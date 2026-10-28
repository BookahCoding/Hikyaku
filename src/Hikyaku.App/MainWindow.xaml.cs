using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

public partial class MainWindow : Window
{
    private readonly string _settingsPath = Path.Combine(App.DataDir, "settings.json");
    private readonly AppSettings _settings;
    private readonly ProfileSelectionService _selection;
    private readonly NotificationGate _gate;
    private readonly ISecretStore _secrets = new DpapiSecretStore(Path.Combine(App.DataDir, "secrets"));
    private GatewayVoiceChatBackend? _voiceChatBackend;
    private NativeTokenStore? _nativeTokens;
    private AccessTokenStore? _accessTokens;

    /// <summary>Cloudflare Access service tokens (DPAPI-protected), shared by the live transport and Settings.</summary>
    private AccessTokenStore AccessTokens => _accessTokens ??= new AccessTokenStore(_secrets);

    /// <summary>One app-wide store so Settings and the live transport see the same browser sign-in.</summary>
    private NativeTokenStore NativeTokens => _nativeTokens ??= new NativeTokenStore(_secrets);
    private readonly MainViewModel _vm;

    private static void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { /* clipboard busy */ }
    }
    private readonly InboxStore _inboxStore;
    private readonly DraftStore _drafts;
    private readonly OfflineChatCache _offline;
    private readonly InboxGroupsStore _groupsStore;
    private readonly LocalFolderGrantStore _folderGrants;
    private readonly IByteSealer _sealer = new DpapiByteSealer();
    private HermesDashboardTransport? _hermes;
    private bool _roomScrollHooked;
    private InboxService? _inbox;   // the herald-push Inbox client: kept for a future PC registration, not created (Stage 42)
    private readonly JobInboxPrefs _jobInboxPrefs = new(new FilePreferenceStore(Path.Combine(App.DataDir, "inbox-hidden-jobs.json")));
    private CancellationTokenSource? _jobInboxPoll;
    private NeedsYouService? _needsYou;
    private SessionCatalog? _sessions;
    private BotRosterService? _bots;
    private Action<GatewayEvent>? _needsHandler;
    private Action<GatewayEvent>? _subagentHandler;
    private bool _localHistoryOpen = true;
    private bool _gatewayHistoryOpen = true;
    private bool _sidebarCollapsed;
    private GridLength _sidebarExpandedWidth = new(260);

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load(_settingsPath);
        // herald-push accepts only hex digits and dashes; an id in the first ("desktop-…") format is replaced once.
        if (!InboxDevice.IsValid(_settings.DesktopDeviceId))
        {
            _settings.DesktopDeviceId = InboxDevice.Ensure(_settings.DesktopDeviceId);
            try { _settings.Save(_settingsPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* used for this run only */ }
        }
        ThemeService.Apply(_settings.ThemeId, _settings.AppearanceMode);
        _sidebarCollapsed = _settings.SidebarCollapsed;

        _selection = new ProfileSelectionService(new FilePreferenceStore(Path.Combine(App.DataDir, "profiles.json")));
        _gate = new NotificationGate(_selection);
        _inboxStore = new InboxStore(new FilePreferenceStore(Path.Combine(App.DataDir, "inbox.json")),
            (url, bot) =>
            {
                var gw = string.IsNullOrWhiteSpace(_settings.GatewayId)
                    ? (_settings.ParsedUrl?.Host ?? "local")
                    : _settings.GatewayId;
                return _gate.Allow(Surface.Inbox, new ProfileKey(gw, bot));
            });
        _drafts = new DraftStore(new FilePreferenceStore(Path.Combine(App.DataDir, "drafts.json")));
        _offline = new OfflineChatCache(new FilePreferenceStore(Path.Combine(App.DataDir, "offline-chat.json")),
            _sealer, k => _gate.Allow(Surface.Conversation, k));
        _groupsStore = new InboxGroupsStore(new FilePreferenceStore(Path.Combine(App.DataDir, "inbox-groups.json")));
        _folderGrants = new LocalFolderGrantStore(
            new FilePreferenceStore(Path.Combine(App.DataDir, "folder-grants.json")),
            () => _settings.DesktopDeviceId);
        var terminalOptIn = new TerminalOptInStore(
            new FilePreferenceStore(Path.Combine(App.DataDir, "terminal-optin.json")));
        var terminalController = new LocalTerminalController(terminalOptIn, new ConPtySessionFactory());
        var networkPublish = new NetworkPublishGrantStore(
            new FilePreferenceStore(Path.Combine(App.DataDir, "network-publish.json")));

        _vm = new MainViewModel(_selection, _gate, BuildTransport, BuildBanner, () => _inbox, () => _needsYou,
            () => _sessions, () => _drafts, () => _offline,
            () => _hermes is null ? null : new AttachmentUploader(() => _hermes.Client ?? throw new InvalidOperationException("Not connected")),
            () => _hermes?.CreateMediaApi(),
            () => _groupsStore,
            () => BuildIdentityStrip(),
            () => _bots,
            () => _hermes?.Client);
        _vm.ConfirmRewind = c => MessageBox.Show(this,
            (c.IsEdit ? "Replace this message with your edit?" : "Ask again with this message?")
            + $"\n\nThis message and the {c.MessagesRemoved - 1} after it will be removed from this chat, here and on the gateway, "
            + "and cannot be restored. Your other chats are not affected.",
            c.IsEdit ? "Edit message" : "Regenerate",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        _vm.PickArtifactSavePath = suggested =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = suggested, Title = "Save artifact" };
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        };
        _vm.CopyArtifactText = CopyToClipboard;
        _vm.CopyPlainText = CopyToClipboard;
        _vm.SignInOffered = () => PlainStatus.For(CurrentConnectionKind()).OfferSignIn;
        _vm.ConfirmConfig = (title, message) => MessageBox.Show(this, message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        _vm.ConfirmHub = (title, message) => MessageBox.Show(this, message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        _vm.ConfirmRoom = (title, message) => MessageBox.Show(this, message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        _vm.ConfirmCapability = (title, message) => MessageBox.Show(this, message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        _vm.ShowCapabilityResult = (title, text) => MessageBox.Show(this, text, title, MessageBoxButton.OK, MessageBoxImage.Information);
        _vm.ConfirmSessionAction = (title, message) => MessageBox.Show(this, message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        _vm.PromptText = (title, current) => TextPromptWindow.Ask(this, title, current);
        _vm.ConfirmCron = (title, message) => MessageBox.Show(this, message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        _vm.EditCron = request =>
        {
            var win = new CronEditorWindow(request) { Owner = this };
            return Task.FromResult(win.ShowDialog() == true ? win.Result : null);
        };
        _vm.FindScrollRequested += ScrollMessageIntoView;
        _vm.PaletteOpenSettingsRequested += () => Settings_Click(this, new RoutedEventArgs());
        _vm.PaletteSearchSessionsRequested += FocusSessionSearch;
        _vm.PaletteShowShortcutsRequested += ShowShortcuts;
        _vm.PaletteToggleSidebarRequested += () => SidebarToggle_Click(this, new RoutedEventArgs());
        _vm.ApplyExperience(_settings.IsAdvanced);
        Loaded += (_, _) => ApplyTextScale();
        _vm.ExperienceChanged += advanced =>
        {
            _settings.SetAdvanced(advanced);
            try { _settings.Save(_settingsPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* keep the in-memory choice */ }
        };
        Loaded += (_, _) => HookMessageScroller();
        HookAttachmentPasteAndDrop();
        _vm.EarlierLoaded += ScrollMessageIntoView;   // keep the reader on the message they were at
        _vm.AttachFolderGrants(_folderGrants);
        _vm.AttachNetworkPublish(networkPublish, new SetupRequiredGitHubPullRequestApi());
        _vm.AttachTerminal(terminalOptIn, terminalController);
        _vm.AttachVoice(BuildVoiceController());
        // 0.6.0 voice chat: the gateway's voice and this PC's microphone; demo and test runs get a silent fixture (no device).
        _vm.AttachVoiceChat(App.DemoMode || App.SmokeMode || App.UiAcceptanceMode
            ? () => new FixtureVoiceChatBackend()
            : () => _hermes?.Auth is not null ? _voiceChatBackend ??= new GatewayVoiceChatBackend(() => _hermes?.Auth) : null);
        DataContext = _vm;
        Loaded += async (_, _) =>
        {
            await _selection.LoadAsync();
            try { await _jobInboxPrefs.LoadAsync(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* nothing hidden */ }
            _vm.SetProfileNames(_settings.ProfileNames);
            _vm.InitActiveProfile(_settings.ActiveProfileId);
            RebuildSessionServices();
            ConnectNow();
            _vm.RefreshVisibility();
            ApplySidebarLayout(animate: false);
            HighlightNav();
            if (!App.SmokeMode && !App.UiAcceptanceMode && !App.DemoMode && _settings.CheckUpdatesOnStart) _ = CheckForUpdateAtStartAsync();
            if (App.SmokeMode)
            {
                Directory.CreateDirectory(App.DataDir);
                File.WriteAllText(Path.Combine(App.DataDir, "smoke.txt"),
                    $"HIKYAKU_SMOKE_OK loaded={_selection.IsLoaded} onboarding={_selection.NeedsOnboarding} theme={_settings.ThemeId}/{_settings.Appearance} sidebar={(_sidebarCollapsed ? "collapsed" : "open")} cp11=1 terminalOptIn={_vm.TerminalOptedIn} terminalDrawer={_vm.TerminalDrawerOpen}");
                Application.Current.Shutdown(0);
            }
            else if (App.UiAcceptanceMode)
            {
                var outDir = Path.Combine(App.DataDir, "ui-acceptance");
                try
                {
                    await UiAcceptance.RunAsync(this, _vm, _settings, _selection, _gate, outDir);
                }
                catch (Exception ex)
                {
                    Directory.CreateDirectory(outDir);
                    await File.WriteAllTextAsync(Path.Combine(outDir, "ui-acceptance.txt"),
                        "HIKYAKU_UI_FAIL " + ex.GetType().Name + " " + ex.Message);
                }
                Application.Current.Shutdown(0);
            }
        };
        Activated += (_, _) => _appActive = true;
        Deactivated += (_, _) => _appActive = false;
        _vm.AttachJobInbox(_jobInboxPrefs);
        _vm.ModelMenuRequested += () => ModelMenu_Click(ModelMenuButton, new RoutedEventArgs());
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Nav)) FadeInPage(); };
        _vm.ActiveProfileChosen += id =>
        {
            _settings.ActiveProfileId = id;
            try { _settings.Save(_settingsPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* keep the in-memory choice */ }
        };
        Closed += (_, _) => _vm.EndVoiceChat(null);
        Closed += async (_, _) =>
        {
            _jobInboxPoll?.Cancel();
            _needsNotifier?.Dispose();
            DetachNeedsYou();
            DetachSubagents();
            _vm.ShutdownTerminal();
            _vm.ShutdownVoice();
            if (_hermes is not null) await _hermes.DisposeAsync();
        };
    }

    private VoiceCaptureController BuildVoiceController()
    {
        // Smoke / UI acceptance / Demo: synthetic capture + fixture transport only (no real mic / live upload).
        if (App.DemoMode || App.SmokeMode || App.UiAcceptanceMode)
        {
            return new VoiceCaptureController(
                new FakeMicrophoneCapture { NextPcm = new byte[640] },
                new FixtureAudioTransport { Transcript = "synthetic voice fixture" },
                new FakeSpeechPlayer());
        }
        return new VoiceCaptureController(
            new WindowsWaveInCapture(),
            new DeferredAudioTransport(() => _hermes?.Auth),
            new WindowsSpeechPlayer());
    }

    private string BuildIdentityStrip()
    {
        // Simple view says only whether the app is connected: no profile ids, host names or local paths.
        if (!_vm.IsAdvanced) return App.DemoMode ? "Demo" : (_settings.ParsedUrl is null ? "Not connected" : "Connected");
        // The profile is in the sidebar's switcher; here only where the gateway is and, when there is one, the shared local folder.
        if (App.DemoMode) return "Demo";
        var host = _settings.ParsedUrl?.Host ?? "No gateway set up";
        var path = _vm.CurrentProjectPathHint;
        var folder = string.IsNullOrEmpty(path) ? "" : " · " + path
            + (string.IsNullOrEmpty(_vm.CurrentProjectBranchHint) ? "" : " · " + _vm.CurrentProjectBranchHint);
        return host + folder;
    }

    /// <summary>
    /// Windows has no push channel for scheduled-job results, so new ones are noticed by reading them now and then: every few
    /// minutes, only while notifications are on, never in demo, smoke or acceptance runs. The first read only learns what is there;
    /// after that each new result is announced once (job and profile, never its text), and only while Hikyaku is not in front.
    /// </summary>
    private void StartJobInboxNotifications(string gatewayId)
    {
        _jobInboxPoll?.Cancel();
        _jobInboxPoll = null;
        if (App.DemoMode || App.SmokeMode || App.UiAcceptanceMode) return;
        var cts = new CancellationTokenSource();
        _jobInboxPoll = cts;
        var news = new JobInboxNews();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    if (_settings.NotificationsEnabled && _hermes?.Auth is { } auth)
                    {
                        var api = new CronApi(auth);
                        var load = await JobInbox.LoadAsync(ct => api.JobsAsync(null, true, ct), (job, ct) => api.RunsAsync(job, 3, ct),
                            null, true, p => _gate.Allow(Surface.Inbox, new ProfileKey(gatewayId, p)), _jobInboxPrefs.Hidden, cts.Token);
                        var fresh = news.Next(load.Results);
                        if (fresh.Count > 0)
                        {
                            if (!_appActive)
                            {
                                var (title, body) = JobInboxNews.Describe(fresh, p => ActiveProfile.Label(new ProfileKey(gatewayId, p), _settings.ProfileNames));
                                TryNotify(new ProfileKey(gatewayId, fresh[0].Profile), title, body);
                            }
                            await Dispatcher.InvokeAsync(() => { if (_vm.Nav == MainNav.Inbox) _ = _vm.RefreshJobInboxAsync(); });
                        }
                    }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
                catch (Exception) { /* a failed read waits for the next round */ }
                try { await Task.Delay(TimeSpan.FromMinutes(3), cts.Token); }
                catch (OperationCanceledException) { return; }
            }
        });
    }

    private NeedsYouNotifier? _needsNotifier;
    private volatile bool _appActive = true;   // updated on the UI thread; read from the RPC thread by the notifier

    private void RebuildSessionServices()
    {
        DetachNeedsYou();
        DetachSubagents();
        _needsNotifier?.Dispose();
        _needsNotifier = null;
        _jobInboxPoll?.Cancel();
        _jobInboxPoll = null;
        _inbox = null;
        _needsYou = null;
        _sessions = null;
        _bots = null;

        if (App.DemoMode)
        {
            var demoGw = _selection.Selected.Select(k => k.GatewayId).FirstOrDefault() ?? "demo";
            _bots = new BotRosterService(_gate, demoGw,
                _ => Task.FromResult(DemoBotFixtures.Roster),
                _ => Task.FromResult(DemoBotFixtures.RoomsFromDefaultMeta()));
            _vm.RaiseIdentity();
            return;
        }

        var uri = _settings.ParsedUrl;
        if (uri is null) return;
        var gatewayId = string.IsNullOrWhiteSpace(_settings.GatewayId) ? uri.Host : _settings.GatewayId;

        _needsYou = new NeedsYouService(_gate, _selection, gatewayId, () => _hermes?.Client);
        _vm.AttachNeedsYou(_needsYou);
        // Toasts for new requests (kind + profile only); silent while this window is in front.
        _needsNotifier = new NeedsYouNotifier(_needsYou, TryNotify, () => _appActive);

        StartJobInboxNotifications(gatewayId);

        _sessions = new SessionCatalog(_gate, () => _hermes?.CreateSessionsApi());
        _vm.AttachSessionActions(() => _hermes?.Auth is { } sessAuth ? new SessionActionsApi(sessAuth) : null);
        _vm.AttachCron(() => _hermes?.Auth is { } cronAuth ? new CronApi(cronAuth) : null, gatewayId);
        _vm.AttachRooms(() => new HostedRoomsApi(() => _hermes?.Client), gatewayId);
        EnsureRoomPolling();
        if (!_roomScrollHooked)                                                          // this method runs on every service rebuild; hook once
        {
            _roomScrollHooked = true;
            _vm.RoomTranscriptLines.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(() => RoomScroll.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        _vm.AttachCapabilities(() => _hermes?.Auth is { } capAuth ? new CapabilitiesApi(capAuth) : null);
        _vm.AttachTypedSettings(() => _hermes?.Auth is { } typedAuth ? new ConfigApi(typedAuth) : null);
        _vm.AttachConfigEditor(() => _hermes?.Auth is { } cfgAuth ? new GatewayConfigEditor(cfgAuth) : null);
        _vm.AttachSkillsHub(() => _hermes?.Auth is { } hubAuth ? new SkillsHubApi(hubAuth) : null);
        _vm.AttachJourney(() => _hermes?.Auth is { } journeyAuth ? new JourneyApi(journeyAuth) : null);
        _vm.AttachInsights(() => _hermes?.Auth is { } insAuth ? new InsightsApi(insAuth) : null);

        var api = new BotsApi(() => _hermes?.Client);
        _bots = new BotRosterService(_gate, gatewayId,
            ct => api.RosterAsync(ct),
            async ct =>
            {
                var roster = await api.RosterAsync(ct).ConfigureAwait(false);
                var def = roster.Bots.FirstOrDefault(b => b.IsDefault) ?? roster.Bots.FirstOrDefault();
                return def?.UiMeta is null ? Array.Empty<DesktopRoom>() : DesktopRooms.Parse(def.UiMeta);
            });
        _vm.RaiseIdentity();
    }

    private void DetachNeedsYou()
    {
        if (_hermes?.Client is not null && _needsHandler is not null)
            _hermes.Client.EventReceived -= _needsHandler;
        _needsHandler = null;
    }

    private void DetachSubagents()
    {
        if (_hermes?.Client is not null && _subagentHandler is not null)
            _hermes.Client.EventReceived -= _subagentHandler;
        _subagentHandler = null;
    }

    private void StartSubagentPump()
    {
        if (_hermes?.Client is null) return;
        DetachSubagents();
        _subagentHandler = ev =>
        {
            if (!ev.Type.StartsWith("subagent.", StringComparison.Ordinal)) return;
            Dispatcher.InvokeAsync(() =>
            {
                // The chat the event belongs to decides the profile; otherwise the profile chosen in the switcher.
                var p = _vm.ProfileForSession(ev.SessionId);
                if (p is null) return;
                _vm.ApplySubagentEvent(p.Value, ev.SessionId, ev.Type, ev.Payload as System.Text.Json.Nodes.JsonObject);
            });
        };
        _hermes.Client.EventReceived += _subagentHandler;
    }

    private void StartNeedsYouPump()
    {
        if (_hermes?.Client is null || _needsYou is null) return;
        DetachNeedsYou();
        var needs = _needsYou;
        _needsHandler = ev =>
        {
            if (!needs.HandleGatewayEvent(ev)) return;
            Dispatcher.InvokeAsync(() => _vm.RefreshNeedsYou());
        };
        _hermes.Client.EventReceived += _needsHandler;
    }

    private IGatewayTransport? BuildTransport()
    {
        if (App.DemoMode) return new DemoTransport();
        var uri = _settings.ParsedUrl;
        if (uri is null) return null;
        if (!HasSignInMaterial(uri))
            return null;

        if (_hermes is not null) return _hermes;

        _connectedAs = CurrentConnectionIdentity();
        var auth = new DashboardAuth(uri, null, NativeTokens, access: AccessTokens);
        var gatewayId = string.IsNullOrWhiteSpace(_settings.GatewayId) ? uri.Host : _settings.GatewayId;
        var username = _settings.Username;
        _hermes = new HermesDashboardTransport(auth, gatewayId, ensureSignedIn: async ct =>
        {
            // Only reached when there is no live session; a stored browser sign-in is already a session.
            var password = _secrets.Get("gateway-password")
                ?? throw new InvalidOperationException("Password missing from DPAPI store.");
            await auth.SignInAsync("basic", username, password, ct).ConfigureAwait(false);
        });
        RebuildSessionServices();
        _hermes.PhaseChanged += _ => Dispatcher.BeginInvoke(() => _vm.RaiseStatus());   // the status line and the sign-in prompt follow the connection
        _ = Task.Run(async () =>
        {
            try
            {
                await _hermes.EnsureConnectedAsync(default);
                await Dispatcher.InvokeAsync(() =>
                {
                    StartNeedsYouPump();
                    StartSubagentPump();
                    _ = _vm.RefreshSessionsAsync();
                    _ = _vm.RefreshApprovalsRiskAsync();   // Simple view shows "approvals are Off" if that is the case
                    _vm.RaiseIdentity();
                });
                await LoadProfileNamesAsync();
            }
            catch { /* banner already explains connection state */ }
        });
        return _hermes;
    }

    /// <summary>Reads the gateway's profile list (read-only) for the switcher's display names, and remembers them for the next start.</summary>
    private async Task LoadProfileNamesAsync()
    {
        if (_hermes?.Auth is not { } auth) return;
        try
        {
            var gatewayId = string.IsNullOrWhiteSpace(_settings.GatewayId) ? auth.BaseUri.Host : _settings.GatewayId;
            var profiles = await auth.ListProfilesAsync(gatewayId, default);
            var names = profiles.ToDictionary(p => p.Key.ProfileId, p => p.DisplayName, StringComparer.Ordinal);
            await Dispatcher.InvokeAsync(() =>
            {
                _settings.ProfileNames = names;
                try { _settings.Save(_settingsPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* names are a convenience */ }
                _vm.SetProfileNames(names);
            });
        }
        catch (Exception) { /* the switcher falls back to profile ids */ }
    }

    /// <summary>What the live connection was built from; Settings keeps the connection while this stays the same.</summary>
    private ConnectionIdentity? _connectedAs;

    private ConnectionIdentity CurrentConnectionIdentity()
    {
        var uri = _settings.ParsedUrl;
        return ConnectionIdentity.Of(uri, string.IsNullOrWhiteSpace(_settings.GatewayId) ? uri?.Host : _settings.GatewayId,
            _settings.Username, _secrets.Get("gateway-password"),
            uri is null ? null : NativeTokens.Get(uri), uri is null ? null : AccessTokens.Get(uri.Host));
    }

    /// <summary>Connects as soon as the app has what it needs (not in smoke / acceptance runs), so pages never wait for a first send.</summary>
    private void ConnectNow()
    {
        if (App.SmokeMode || App.UiAcceptanceMode) return;
        _ = BuildTransport();
    }

    /// <summary>A stored browser sign-in, or a saved username + DPAPI password.</summary>
    private bool HasSignInMaterial(Uri uri) =>
        NativeTokens.Get(uri) is not null
        || (!string.IsNullOrEmpty(_settings.Username) && !string.IsNullOrEmpty(_secrets.Get("gateway-password")));

    /// <summary>Where the connection stands, as one value both views describe in their own words.</summary>
    private ConnectionKind CurrentConnectionKind()
    {
        if (App.DemoMode) return ConnectionKind.Demo;
        if (_settings.ParsedUrl is null) return ConnectionKind.NotConfigured;
        if (!HasSignInMaterial(_settings.ParsedUrl)) return ConnectionKind.NeedsSignIn;
        return _hermes?.ConnectionPhase switch
        {
            ConnectionConnected => ConnectionKind.Connected,
            ConnectionConnecting => ConnectionKind.Connecting,
            ConnectionReconnecting => ConnectionKind.Reconnecting,
            ConnectionSessionExpired => ConnectionKind.SessionExpired,
            ConnectionFailed => ConnectionKind.Failed,
            _ => ConnectionKind.Starting
        };
    }

    private string BuildBanner()
    {
        // Simple view: one plain sentence (or nothing when connected), never protocol names, counters or addresses.
        if (!_vm.IsAdvanced) return PlainStatus.For(CurrentConnectionKind()).Text;
        var reason = _hermes?.ConnectionPhase switch
        {
            ConnectionReconnecting r => r.Reason,
            ConnectionFailed f => f.Reason,
            _ => null
        };
        return PlainStatus.ForAdvanced(CurrentConnectionKind(), reason);
    }

    private ToastNotificationService? _toastService;

    /// <summary>Profile-gated, user-enabled, throttled by the callers; text is cleaned and bounded by <see cref="ToastXml"/>.</summary>
    public ToastDeliveryResult TryNotify(ProfileKey profile, string title, string body)
    {
        _toastService ??= new ToastNotificationService(_gate, () => _settings.NotificationsEnabled,
            new WindowsToastPresenter(App.ToastAppId, new HkcuToastRegistry()));
        return _toastService.TryShow(new GatedNotification(profile, title, body, Surface.Toast));
    }

    internal void ApplyThemeFromSettings()
    {
        ThemeService.Apply(_settings.ThemeId, _settings.AppearanceMode);
        _vm.RaiseIdentity();
    }

    private void NewChat_Click(object sender, RoutedEventArgs e) => _vm.NewChat();

    private void NavChats_Click(object sender, RoutedEventArgs e)
    {
        _vm.ShowChatsCommand.Execute(null);
        HighlightNav();
    }

    private void NavBots_Click(object sender, RoutedEventArgs e)
    {
        _vm.ShowBotsCommand.Execute(null);
        HighlightNav();
    }

    private void NavProjects_Click(object sender, RoutedEventArgs e)
    {
        _vm.ShowProjectsCommand?.Execute(null);
        HighlightNav();
    }

    private void NavSide_Click(object sender, RoutedEventArgs e) => HighlightNav();

    /// <summary>
    /// The row a single click landed on (lists open on one click, like the rest of Windows 11 and macOS sidebars). A click on a
    /// button inside the row is that button's, not the row's.
    /// </summary>
    private static T? ClickedRow<T>(MouseButtonEventArgs e) where T : class
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null;
             d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase) return null;
            if (d is ListBoxItem item) return item.DataContext as T;
        }
        return null;
    }

    private void BotRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (ClickedRow<BotRowVM>(e) is { } row)
            _vm.OpenBotCommand.Execute(row);
        HighlightNav();
    }

    private void RoomComposer_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) _vm.RoomComposerCaretMoved(tb.CaretIndex);
    }

    private System.Windows.Threading.DispatcherTimer? _roomPoll;

    /// <summary>Reads the open room every few seconds, only while the Rooms page is shown (rooms send no events to wait on).</summary>
    private void EnsureRoomPolling()
    {
        if (_roomPoll is null)
        {
            _roomPoll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _roomPoll.Tick += (_, _) =>
            {
                if (_vm.Nav == MainNav.Rooms && IsActive) _ = _vm.PollRoomAsync();
            };
        }
        if (App.DemoMode || App.SmokeMode || App.UiAcceptanceMode) return;
        if (!_roomPoll.IsEnabled) _roomPoll.Start();
    }

    private void RoomRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (ClickedRow<RoomRowVM>(e) is { } row)
            _vm.OpenRoomCommand.Execute(row);
        HighlightNav();
    }

    private void ProjectFile_Click(object sender, MouseButtonEventArgs e)
    {
        if (ClickedRow<ProjectFileRowVM>(e) is { } row)
            _vm.OpenProjectEntryCommand?.Execute(row);
    }

    /// <summary>A short fade when the page changes (none with Windows' reduced-motion setting, or in the acceptance run's captures).</summary>
    private void FadeInPage()
    {
        if (ThemeService.ReduceMotion || App.UiAcceptanceMode) return;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        MainArea.BeginAnimation(OpacityProperty, fade);
    }

    private void HighlightNav()
    {
        void StyleNav(Button btn, bool selected)
        {
            btn.Style = (Style)FindResource(selected ? "NavRowSelected" : "NavRowButton");
        }
        StyleNav(NavChats, _vm.Nav == MainNav.Chats);
        StyleNav(NavBots, _vm.Nav == MainNav.Bots);
        StyleNav(NavInbox, _vm.Nav == MainNav.Inbox);
        StyleNav(NavScheduled, _vm.Nav == MainNav.Scheduled);
        StyleNav(NavCapabilities, _vm.Nav == MainNav.Capabilities);
        StyleNav(NavRooms, _vm.Nav == MainNav.Rooms);
        StyleNav(NavArtifacts, _vm.Nav == MainNav.Artifacts);
        StyleNav(NavJourney, _vm.Nav == MainNav.Journey);
        StyleNav(NavInsights, _vm.Nav == MainNav.Insights);
        StyleNav(NavNeeds, _vm.Nav == MainNav.NeedsYou);
        StyleNav(NavProjects, _vm.Nav == MainNav.Projects);
    }

    /// <summary>One quiet check at start-up (Settings → Updates can turn it off): it only shows a button, nothing is downloaded.</summary>
    private async Task CheckForUpdateAtStartAsync()
    {
        await Task.Delay(StartupUpdateCheck.Delay);
        using var http = new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false };
        var checker = new UpdateChecker(http, App.UpdateRepo, UpdateLauncher.CurrentVersion);
        if (await StartupUpdateCheck.FindNewerAsync(checker.CheckAsync, default) is { } release)
            _vm.UpdateNotice = $"Update to {release.Version}";
    }

    private void UpdateNotice_Click(object sender, RoutedEventArgs e) => ShowSettings(checkUpdates: true);

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings(checkUpdates: false);

    private void ShowSettings(bool checkUpdates)
    {
        var dlg = new SettingsWindow(_settings, _settingsPath, _selection, _secrets, () => _hermes?.Auth, NativeTokens, AccessTokens)
        { Owner = this, CheckUpdatesOnOpen = checkUpdates };
        dlg.ShowDialog();
        _vm.ApplyExperience(_settings.IsAdvanced);
        ApplyThemeFromSettings();
        ApplyTextScale();
        // Keep a working connection: only an address / gateway / sign-in change replaces it, and then it reconnects at once.
        if (_hermes is null || CurrentConnectionIdentity() != _connectedAs)
        {
            if (_hermes is not null)
            {
                DetachNeedsYou();
                DetachSubagents();
                _hermes.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _hermes = null;
            }
            RebuildSessionServices();
            ConnectNow();
        }
        _vm.RefreshVisibility();
        _vm.RaiseStatus();
        _ = _vm.RefreshApprovalsRiskAsync();
        HighlightNav();
    }

    /// <summary>The "Sign in again" button in the status line (Simple view): opens Settings, which shows Account in Simple view.</summary>
    private void SignInAgain_Click(object sender, RoutedEventArgs e) => Settings_Click(sender, e);

    /// <summary>Scales the whole window to the chosen text size (Normal / Larger / Largest).</summary>
    internal void ApplyTextScale()
    {
        var factor = TextScale.Factor(_settings.TextScalePercent);
        ShellRoot.LayoutTransform = factor == 1.0 ? System.Windows.Media.Transform.Identity : new System.Windows.Media.ScaleTransform(factor, factor);
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        _settings.SidebarCollapsed = _sidebarCollapsed;
        _settings.Save(_settingsPath);
        ApplySidebarLayout(animate: true);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => ApplySidebarLayout(animate: false);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // One table (Hikyaku.Core.Shortcuts, Hermes Desktop's keys) drives this and the palette hints. Exact modifiers only.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key == Key.Escape && mods == ModifierKeys.None && _vm.VoiceChatActive)
        {
            _vm.EndVoiceChat(null);                      // Esc ends a voice chat before anything else
            e.Handled = true;
            return;
        }
        var action = Shortcuts.Match(key.ToString(), (mods & ModifierKeys.Control) != 0, (mods & ModifierKeys.Shift) != 0, (mods & ModifierKeys.Alt) != 0);
        if (action is not { } a) return;
        switch (a)
        {
            case ShortcutAction.NewChat:
                _vm.NewChat();
                break;
            case ShortcutAction.Palette:
                OpenPalette();
                break;
            case ShortcutAction.Settings:
                Settings_Click(sender, e);
                break;
            case ShortcutAction.SearchSessions:
                FocusSessionSearch();
                break;
            case ShortcutAction.ToggleSidebar:
                SidebarToggle_Click(sender, e);
                break;
            case ShortcutAction.FindInChat:
                // Local search only; without an open chat the key is left alone.
                if (_vm.OpenFindCommand?.CanExecute(null) != true) return;
                _vm.OpenFindCommand.Execute(null);
                Dispatcher.BeginInvoke(() => { FindBox.Focus(); FindBox.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
                break;
            case ShortcutAction.ToggleTerminal:
                // Toggles the drawer only after opt-in; never auto-starts a shell. Simple view does not offer it.
                if (_vm.Shows(ExperienceFeature.Terminal)) _vm.ToggleTerminalDrawerCommand?.Execute(null);
                break;
            case ShortcutAction.ShowShortcuts:
                ShowShortcuts();
                break;
        }
        e.Handled = true;
    }

    // Right-click selects the row under the pointer (a ListBox does not), so the menu always acts on what was clicked.
    private void SessionRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(GatewayHistoryList, d) is ListBoxItem item)
            item.IsSelected = true;
    }

    private void SessionRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (GatewayHistoryList.SelectedItem is not SessionRowVM row) { e.Handled = true; return; }
        var menu = new ContextMenu();
        foreach (var (verb, label) in _vm.SessionMenuFor(row))
        {
            var v = verb;
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => _ = _vm.RunSessionActionAsync(row, v);
            menu.Items.Add(item);
        }
        GatewayHistoryList.ContextMenu = menu;
    }

    private void OpenPalette()
    {
        _vm.OpenPaletteCommand!.Execute(null);
        Dispatcher.BeginInvoke(() => { PaletteBox.Focus(); PaletteBox.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void FocusSessionSearch()
    {
        _vm.Nav = MainNav.Chats;
        MorePopup.IsOpen = true;
        Dispatcher.BeginInvoke(() => { SessionSearchBox.Focus(); SessionSearchBox.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ShowShortcuts() =>
        MessageBox.Show(this, Shortcuts.HelpText(_vm.IsAdvanced), "Keyboard shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);

    private void PaletteBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: _vm.MovePaletteSelectionCommand!.Execute(1); e.Handled = true; break;
            case Key.Up: _vm.MovePaletteSelectionCommand!.Execute(-1); e.Handled = true; break;
            case Key.Enter: _vm.RunPaletteSelectedCommand!.Execute(null); e.Handled = true; break;
            case Key.Escape: _vm.ClosePaletteCommand!.Execute(null); e.Handled = true; break;
        }
    }

    private void PaletteScrim_Click(object sender, MouseButtonEventArgs e) { _vm.ClosePaletteCommand!.Execute(null); e.Handled = true; }

    private void PalettePanel_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;   // clicks inside the panel do not dismiss it

    private void FindBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? _vm.FindPreviousCommand : _vm.FindNextCommand)?.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                _vm.CloseFindCommand?.Execute(null);
                Composer.Focus();
                e.Handled = true;
                break;
        }
    }

    private ScrollViewer? MessageScrollViewer => MessageList.Template?.FindName("MessageScroll", MessageList) as ScrollViewer;

    private VirtualizingStackPanel? MessagePanel()
    {
        static VirtualizingStackPanel? Find(DependencyObject root)
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is VirtualizingStackPanel p) return p;
                if (Find(child) is { } deeper) return deeper;
            }
            return null;
        }
        return Find(MessageList);
    }

    private void HookMessageScroller()
    {
        MessageList.ApplyTemplate();
        if (MessageScrollViewer is not { } sv) return;
        // "Load earlier messages" only shows while the reader is near the oldest loaded message.
        sv.ScrollChanged += (_, e) =>
        {
            _vm.MessagesNearTop = sv.VerticalOffset < 160;
            // Follow the newest message while a reply streams (and only while the reader is at the bottom).
            if (_vm.ReportMessageScroll(sv.VerticalOffset, sv.ExtentHeight, sv.ViewportHeight, e.VerticalChange, e.ExtentHeightChange))
                // Queued, so by the time it runs the reader may have scrolled up: only scroll if they are still following.
                Dispatcher.BeginInvoke(() => { if (_vm.FollowingLatest) sv.ScrollToEnd(); }, System.Windows.Threading.DispatcherPriority.Background);
        };
        _vm.ScrollToEndRequested += () =>
            Dispatcher.BeginInvoke(() => { MessageScrollViewer?.ScrollToEnd(); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Synthetic acceptance only: scroll the chat to its oldest loaded message.</summary>
    internal void AcceptanceScrollMessagesToStart()
    {
        MessageScrollViewer?.ScrollToHome();
        UpdateLayout();
    }

    /// <summary>Synthetic acceptance only: the message list's scroll position.</summary>
    internal (double Offset, double Extent, double Viewport) AcceptanceMessageScroll() =>
        MessageScrollViewer is { } sv ? (sv.VerticalOffset, sv.ExtentHeight, sv.ViewportHeight) : (0, 0, 0);

    /// <summary>Synthetic acceptance only: whether the Jump to latest button is actually on screen (once, not per message).</summary>
    internal bool AcceptanceJumpLatestShown() => JumpLatest.IsVisible && JumpLatest.ActualWidth > 0 && JumpLatest.ActualHeight > 0;

    /// <summary>Synthetic acceptance only: scroll the chat to a position, as a reader would.</summary>
    internal void AcceptanceScrollMessagesTo(double offset)
    {
        MessageScrollViewer?.ScrollToVerticalOffset(offset);
        UpdateLayout();
    }

    /// <summary>Synthetic acceptance only: scroll the chat to its newest message.</summary>
    internal void AcceptanceSelectCapabilityTab(int index) => CapabilityTabs.SelectedIndex = index;

    internal void AcceptanceScrollMessagesToEnd()
    {
        MessageScrollViewer?.ScrollToEnd();
        UpdateLayout();
    }

    /// <summary>
    /// Brings a message into view with a little headroom. With a virtualized list the container may not exist yet, so the
    /// panel is asked to realize it first and the headroom is applied once layout has run.
    /// </summary>
    private void ScrollMessageIntoView(MessageVM message)
    {
        var index = _vm.Messages.IndexOf(message);
        if (index < 0) return;
        void Go()
        {
            if (MessageList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement el)
            {
                el.BringIntoView();
                // Headroom: keep the message clear of the find / load-earlier bars at the top.
                if (MessageScrollViewer is { } sv && el.IsVisible)
                {
                    var top = el.TransformToAncestor(sv).Transform(new Point(0, 0)).Y;
                    if (top < 64) sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset - (64 - top)));
                }
            }
            else
                MessagePanel()?.BringIndexIntoViewPublic(index);
        }
        Go();
        Dispatcher.BeginInvoke(Go, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void TerminalOutput_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox box)
            _vm.TerminalSelection = box.SelectedText ?? "";
    }

    private void TerminalDrawer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        // Viewport for PTY resize: prefer output textbox if laid out, else drawer body.
        var output = FindName("TerminalOutputBox") as FrameworkElement;
        var w = output is { ActualWidth: > 1 } ? output.ActualWidth : el.ActualWidth - 24;
        var h = output is { ActualHeight: > 1 } ? output.ActualHeight : Math.Max(40, el.ActualHeight - 120);
        _vm.NotifyTerminalViewport(w, h);
    }

    private void SidebarScrim_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_sidebarCollapsed)
        {
            _sidebarCollapsed = true;
            _settings.SidebarCollapsed = true;
            _settings.Save(_settingsPath);
            ApplySidebarLayout(animate: true);
        }
    }

    private void ApplySidebarLayout(bool animate)
    {
        _ = animate; // PanelDuration is zero under reduced-motion; width snaps — no per-frame animation.
        var presentation = SidebarLayout.ForWidth(ActualWidth > 0 ? ActualWidth : Width, _sidebarCollapsed);

        switch (presentation)
        {
            case SidebarPresentation.Inline:
                SidebarPanel.Visibility = Visibility.Visible;
                SidebarScrim.Visibility = Visibility.Collapsed;
                SidebarOpenNarrow.Visibility = Visibility.Collapsed;
                BrandTitle.Visibility = Visibility.Visible;
                // the toggle shows a pane icon (set in XAML)
                SidebarToggle.ToolTip = "Collapse sidebar (Ctrl+B)";
                SidebarColumn.Width = _sidebarExpandedWidth;
                Panel.SetZIndex(SidebarPanel, 0);
                break;
            case SidebarPresentation.CollapsedRail:
                SidebarPanel.Visibility = Visibility.Collapsed;
                SidebarScrim.Visibility = Visibility.Collapsed;
                SidebarOpenNarrow.Visibility = Visibility.Visible;
                SidebarColumn.Width = new GridLength(0);
                break;
            case SidebarPresentation.Overlay:
                SidebarPanel.Visibility = Visibility.Visible;
                SidebarScrim.Visibility = Visibility.Visible;
                SidebarOpenNarrow.Visibility = Visibility.Collapsed;
                BrandTitle.Visibility = Visibility.Visible;
                // the toggle shows a pane icon (set in XAML)
                SidebarColumn.Width = _sidebarExpandedWidth;
                Panel.SetZIndex(SidebarPanel, 2);
                Panel.SetZIndex(SidebarScrim, 1);
                break;
            case SidebarPresentation.Hidden:
                SidebarPanel.Visibility = Visibility.Collapsed;
                SidebarScrim.Visibility = Visibility.Collapsed;
                SidebarOpenNarrow.Visibility = Visibility.Visible;
                SidebarColumn.Width = new GridLength(0);
                break;
        }
    }

    private void MoreOverflow_Click(object sender, RoutedEventArgs e) => MorePopup.IsOpen = true;
    private void CloseMore_Click(object sender, RoutedEventArgs e) => MorePopup.IsOpen = false;

    private void LocalHistoryToggle_Click(object sender, RoutedEventArgs e)
    {
        _localHistoryOpen = !_localHistoryOpen;
        LocalHistoryList.Visibility = _localHistoryOpen ? Visibility.Visible : Visibility.Collapsed;
        LocalHistoryToggle.Content = (_localHistoryOpen ? "▾  " : "▸  ") + "Open here";
    }

    private void GatewayHistoryToggle_Click(object sender, RoutedEventArgs e)
    {
        _gatewayHistoryOpen = !_gatewayHistoryOpen;
        GatewayHistoryList.Visibility = _gatewayHistoryOpen ? Visibility.Visible : Visibility.Collapsed;
        GatewayHistoryToggle.Content = (_gatewayHistoryOpen ? "▾  " : "▸  ") + "Recent chats";
    }

    private void Composer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // While a "/" command name is being typed, Tab (or Enter on a partial name) completes the first suggestion.
        if (_vm.SlashSuggestions.Count > 0 && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            && (e.Key == Key.Tab || (e.Key == Key.Enter && !SlashCommands.TryParse(_vm.InputText, out _, out _))))
        {
            _vm.CompleteSlashCommand(_vm.SlashSuggestions[0]);
            Composer.CaretIndex = Composer.Text.Length;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _vm.SendCommand.CanExecute(null))
        {
            _vm.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>The chat box's model menu (Advanced): the gateway's models for this chat, grouped by provider.</summary>
    private async void ModelMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ModelMenuButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        menu.Items.Add(new MenuItem { Header = new TextBlock { Text = "Loading models…" }, IsEnabled = false });
        menu.IsOpen = true;
        IReadOnlyList<ModelChoice> choices;
        try { choices = await _vm.ModelChoicesAsync(); }
        catch (Exception) { choices = []; }                         // an async void handler: never let anything out
        menu.Items.Clear();
        if (choices.Count == 0)
            menu.Items.Add(new MenuItem { Header = new TextBlock { Text = "No models listed right now" }, IsEnabled = false });
        foreach (var group in choices.GroupBy(c => c.ProviderName))
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = new TextBlock { Text = group.Key, FontWeight = FontWeights.SemiBold }, IsEnabled = false });
            foreach (var choice in group)
            {
                // A TextBlock header: a plain string would turn "_" in a model id into an access key.
                var item = new MenuItem { Header = new TextBlock { Text = choice.Id }, IsChecked = choice.IsCurrent };
                item.Click += async (_, _) =>
                {
                    try { await _vm.PickModelAsync(choice); } catch (Exception) { /* PickModelAsync reports its own failures */ }
                };
                menu.Items.Add(item);
            }
        }
    }

    private async void SecretSubmit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not NeedsYouItemVM vm) return;
        string value = "";
        if (btn.Parent is Panel row && row.Parent is Panel col)
        {
            foreach (var child in col.Children)
            {
                if (child is PasswordBox box)
                {
                    value = box.Password;
                    box.Password = "";
                    break;
                }
            }
        }
        _vm.SecretValue = value;
        if (_vm.AnswerSecretCommand.CanExecute(vm))
            _vm.AnswerSecretCommand.Execute(vm);
        await Task.CompletedTask;
    }

    private void SessionRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (ClickedRow<SessionRowVM>(e) is { } row)
            _vm.OpenSessionCommand.Execute(row);
    }

    private void InboxFolder_Click(object sender, MouseButtonEventArgs e)
    {
        if (ClickedRow<InboxFolderVM>(e) is { } folder) folder.OpenCommand.Execute(null);
    }

    private void JobResult_Click(object sender, MouseButtonEventArgs e)
    {
        if (ClickedRow<JobResultVM>(e) is { } row) row.OpenCommand.Execute(null);
        HighlightNav();
    }

    /// <summary>Used by synthetic UI acceptance for theme / width / sidebar evidence.</summary>
    internal void AcceptanceSetSidebarCollapsed(bool collapsed)
    {
        _sidebarCollapsed = collapsed;
        _settings.SidebarCollapsed = collapsed;
        ApplySidebarLayout(animate: false);
    }

    internal void AcceptanceSetWidth(double width)
    {
        Width = width;
        UpdateLayout();
        ApplySidebarLayout(animate: false);
    }
}
