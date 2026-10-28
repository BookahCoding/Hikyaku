using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hikyaku.Core;

namespace Hikyaku.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ProfileSelectionService _selection;
    private readonly ISecretStore _secrets;
    private readonly string _settingsPath;
    private readonly Func<DashboardAuth?> _liveAuth;
    private readonly NativeTokenStore _nativeTokens;
    private readonly AccessTokenStore _access;
    private CancellationTokenSource? _browserSignInCts;
    private readonly SocketsHttpHandler _updateHttp = new() { AllowAutoRedirect = false };
    private readonly UpdateFlow _updates;
    private CancellationTokenSource? _updateCts;
    private ApprovalMode _loadedMode = ApprovalMode.Manual;
    private bool _approvalSupported;
    private bool _suppressPreview;

    /// <summary>Opened from the "Update to x.y.z" button: go to Updates and check at once.</summary>
    public bool CheckUpdatesOnOpen { get; init; }

    public SettingsWindow(AppSettings settings, string settingsPath, ProfileSelectionService selection, ISecretStore secrets,
        Func<DashboardAuth?>? liveAuth = null, NativeTokenStore? nativeTokens = null, AccessTokenStore? accessTokens = null)
    {
        InitializeComponent();
        _settings = settings; _settingsPath = settingsPath; _selection = selection; _secrets = secrets;
        _liveAuth = liveAuth ?? (() => null);
        _nativeTokens = nativeTokens ?? new NativeTokenStore(secrets);
        _access = accessTokens ?? new AccessTokenStore(secrets);
        Closed += (_, _) =>
        {
            try { _browserSignInCts?.Cancel(); } catch { /* disposed */ }
            try { _updateCts?.Cancel(); } catch { /* disposed */ }
            _updateHttp.Dispose();
        };
        var verifier = new ReleaseSignatureVerifier(UpdateTrust.PublisherPublicKeySpkiBase64);
        var checker = new UpdateChecker(_updateHttp, App.UpdateRepo, UpdateLauncher.CurrentVersion);
        var installer = new UpdateInstaller(_updateHttp, verifier, App.UpdateWorkDir);
        _updates = new UpdateFlow(ct => checker.CheckAsync(ct), installer.PrepareAsync, verifier.IsConfigured);
        _updates.Changed += () => Dispatcher.BeginInvoke(RefreshUpdateUi);
        RefreshUpdateUi();
        Loaded += (_, _) =>
        {
            RefreshAccessStatus();
            if (ApprovalsSection.Visibility == Visibility.Visible) _ = LoadApprovalsForSelectedAsync();
            if (CheckUpdatesOnOpen && UpdatesSection.Visibility == Visibility.Visible)
            {
                UpdatesSection.BringIntoView();
                UpdateCheck_Click(this, new RoutedEventArgs());
            }
        };
        GatewayId.Text = settings.GatewayId;
        GatewayUrl.Text = settings.GatewayUrl;
        Username.Text = settings.Username;
        NotificationsEnabled.IsChecked = settings.NotificationsEnabled;
        UpdateOnStart.IsChecked = settings.CheckUpdatesOnStart;
        ModeAdvanced.IsChecked = settings.IsAdvanced;
        ModeSimple.IsChecked = !settings.IsAdvanced;
        var scale = TextScale.Normalize(settings.TextScalePercent);
        TextNormal.IsChecked = scale == 100;
        TextLarger.IsChecked = scale == 115;
        TextLargest.IsChecked = scale == 130;
        ApplyModeSections(settings.IsAdvanced);
        foreach (var id in settings.KnownProfileIds) AddRow(id);

        _suppressPreview = true;
        ThemePicker.ItemsSource = ThemeCatalog.All.Select(t => t.DisplayName).ToList();
        var theme = ThemeCatalog.Get(settings.ThemeId);
        ThemePicker.SelectedItem = theme.DisplayName;
        switch (settings.AppearanceMode)
        {
            case AppearanceMode.Light: AppearLight.IsChecked = true; break;
            case AppearanceMode.Dark: AppearDark.IsChecked = true; break;
            default: AppearSystem.IsChecked = true; break;
        }
        _suppressPreview = false;
        UpdateMotionHint();
        PreviewTheme();

        // AddRow (above) already put each known profile in this list once; adding them again listed every profile twice.
        // The profile in use (the sidebar switcher) first; it loads as the window opens (see Loaded).
        var active = settings.ActiveProfileId is { } a && ApprovalProfile.Items.Contains(a) ? ApprovalProfile.Items.IndexOf(a) : 0;
        if (ApprovalProfile.Items.Count > 0) ApprovalProfile.SelectedIndex = active;
        ApprovalStatus.Text = "Loading the approval setting from the gateway…";
        BuildApprovalRadios(enabled: false, selected: ApprovalMode.Manual);
    }

    private void AddRow(string profileId)
    {
        if (!ApprovalProfile.Items.Cast<string>().Contains(profileId))   // each profile once in the approvals list
            ApprovalProfile.Items.Add(profileId);
        if (string.IsNullOrWhiteSpace(GatewayId.Text)) return;
        var key = new ProfileKey(GatewayId.Text.Trim(), profileId);
        if (ProfileList.Children.OfType<CheckBox>().Any(c => (string)c.Content == profileId)) return;
        ProfileList.Children.Add(new CheckBox { Content = profileId, IsChecked = _selection.IsAllowed(key) });
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var id = NewProfile.Text.Trim();
        if (id.Length == 0 || string.IsNullOrWhiteSpace(GatewayId.Text)) return;
        AddRow(id);
        NewProfile.Clear();
    }

    private async void SignInDiscover_Click(object sender, RoutedEventArgs e)
    {
        AuthStatus.Text = "Signing in…";
        SignInDiscover.IsEnabled = false;
        try
        {
            PersistFields(keepPasswordIfBlank: true);
            if (_settings.ParsedUrl is null)
            {
                AuthStatus.Text = "Enter a valid http(s) dashboard URL first.";
                return;
            }
            var password = Password.Password.Length > 0 ? Password.Password : _secrets.Get("gateway-password");
            if (string.IsNullOrEmpty(_settings.Username) || string.IsNullOrEmpty(password))
            {
                AuthStatus.Text = "Username and password are required to sign in. Password is never read from the repository.";
                return;
            }
            if (Password.Password.Length > 0) _secrets.Set("gateway-password", Password.Password);
            Password.Clear();

            using var auth = new DashboardAuth(_settings.ParsedUrl, null, _nativeTokens, access: _access);
            await auth.SignInAsync("basic", _settings.Username, password, default);
            await DiscoverAfterSignInAsync(auth);
        }
        catch (AuthFailedException ex)
        {
            AuthStatus.Text = $"Sign-in failed ({ex.Kind}): {ex.Message}";
            ApprovalStatus.Text = "Approvals unavailable: " + ex.Message;
            BuildApprovalRadios(enabled: false, selected: _loadedMode);
        }
        catch (Exception ex)
        {
            AuthStatus.Text = "Sign-in error: " + ex.GetType().Name + " — " + ex.Message;
        }
        finally { SignInDiscover.IsEnabled = true; }
    }

    // ---- Cloudflare Access service token -------------------------------------------------------------

    private AccessTokenSettings AccessRules => new(_access);

    private void RefreshAccessStatus()
    {
        PersistFields(keepPasswordIfBlank: true);
        AccessStatus.Text = AccessRules.Status(_settings.ParsedUrl);
    }

    private void AccessSave_Click(object sender, RoutedEventArgs e)
    {
        PersistFields(keepPasswordIfBlank: true);
        var result = AccessRules.Save(_settings.ParsedUrl, AccessClientId.Text, AccessClientSecret.Password);
        AccessStatus.Text = result.Message;
        if (!result.Ok) return;
        AccessClientSecret.Clear();                 // the secret is never kept in the window once saved
        AccessClientId.Clear();
    }

    private void AccessRemove_Click(object sender, RoutedEventArgs e)
    {
        PersistFields(keepPasswordIfBlank: true);
        AccessStatus.Text = AccessRules.Remove(_settings.ParsedUrl).Message;
        AccessClientSecret.Clear();
    }

    private async void AccessTest_Click(object sender, RoutedEventArgs e)
    {
        PersistFields(keepPasswordIfBlank: true);
        if (_settings.ParsedUrl is not { } url) { AccessStatus.Text = "Enter a valid dashboard URL first."; return; }
        AccessTest.IsEnabled = false;
        AccessStatus.Text = "Checking " + url.Host + "…";
        try
        {
            using var auth = new DashboardAuth(url, null, _nativeTokens, access: _access);
            AccessStatus.Text = AccessTokenSettings.Describe(await auth.ProbeAsync(default));
        }
        catch (Exception ex)                         // an async void handler: anything let out here would close the app
        {
            AccessStatus.Text = "The check did not finish: " + ex.Message;
        }
        finally { AccessTest.IsEnabled = true; }
    }

    private void RefreshUpdateUi()
    {
        var cur = UpdateLauncher.CurrentVersion;
        UpdateCurrent.Text = cur == default ? "Current version: unknown" : $"Current version: {cur}";
        UpdateCheck.IsEnabled = _updates.CanCheck;
        UpdateDownload.IsEnabled = _updates.CanDownload;
        UpdateInstall.IsEnabled = _updates.CanInstall;
        UpdatePage.IsEnabled = _updates.Release is not null;
        UpdateProgress.Visibility = _updates.Phase is UpdatePhase.Checking or UpdatePhase.Downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatus.Text = _updates.Message;
        var notes = _updates.Release?.Notes ?? "";
        UpdateNotes.Text = notes;
        UpdateNotes.Visibility = notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void UpdateCheck_Click(object sender, RoutedEventArgs e)
    {
        _updateCts?.Cancel();
        _updateCts = new CancellationTokenSource();
        await _updates.CheckAsync(_updateCts.Token);
    }

    private async void UpdateDownload_Click(object sender, RoutedEventArgs e)
    {
        _updateCts?.Cancel();
        _updateCts = new CancellationTokenSource();
        await _updates.PrepareAsync(null, _updateCts.Token);
    }

    private void UpdatePage_Click(object sender, RoutedEventArgs e)
    {
        if (_updates.Release is not { } r) return;
        try { UpdateLauncher.OpenReleasePage(r); }
        catch (Exception ex) { UpdateStatus.Text = "Could not open the release page: " + ex.Message; }
    }

    private void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_updates.Staged is not { } staged) return;
        if (!UpdateLauncher.InstallDirWritable())
        {
            UpdateStatus.Text = "The install folder is not writable by this account, so the app cannot update itself here. Use the release page to install manually.";
            return;
        }
        var ok = MessageBox.Show(this,
            $"Install version {staged.Version} now?\n\nHikyaku will close and restart. Your settings and sign-in are kept. " +
            "If the update cannot be applied, the current version is restored.",
            "Install update", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (ok != MessageBoxResult.Yes) { _updates.Discard(); return; }
        try
        {
            UpdateLauncher.StartHelper(staged);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Could not start the installer: " + ex.Message;
        }
    }

    private async void SignInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_browserSignInCts is not null) { _browserSignInCts.Cancel(); return; } // second click cancels
        SignInBrowser.IsEnabled = false;
        try
        {
            PersistFields(keepPasswordIfBlank: true);
            if (_settings.ParsedUrl is null)
            {
                AuthStatus.Text = "Enter a valid http(s) dashboard URL first.";
                return;
            }
            using var auth = new DashboardAuth(_settings.ParsedUrl, null, _nativeTokens, access: _access);
            AuthStatus.Text = "Checking the gateway…";
            if (!await auth.SupportsNativeSignInAsync(default))
            {
                AuthStatus.Text = "This gateway does not advertise browser sign-in (native_pkce). Use username and password.";
                return;
            }
            using var cts = _browserSignInCts = new CancellationTokenSource();
            SignInBrowser.Content = "Cancel browser sign-in";
            SignInBrowser.IsEnabled = true;
            AuthStatus.Text = "Waiting for you to finish signing in in your browser…";
            var gateway = auth.BaseUri;
            await new NativeSignIn(auth, new HttpListenerLoopbackReceiver()).SignInAsync(
                null, url => ShellBrowserLauncher.Open(url, gateway), cts.Token);
            await DiscoverAfterSignInAsync(auth);
        }
        catch (OperationCanceledException)
        {
            AuthStatus.Text = "Browser sign-in cancelled.";
        }
        catch (AuthFailedException ex)
        {
            AuthStatus.Text = $"Browser sign-in failed ({ex.Kind}): {ex.Message}";
        }
        catch (Exception ex)
        {
            AuthStatus.Text = "Browser sign-in error: " + ex.GetType().Name + " — " + ex.Message;
        }
        finally
        {
            _browserSignInCts = null;
            SignInBrowser.Content = "Sign in with browser";
            SignInBrowser.IsEnabled = true;
        }
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        PersistFields(keepPasswordIfBlank: true);
        if (_settings.ParsedUrl is null) return;
        try
        {
            using var auth = new DashboardAuth(_settings.ParsedUrl, null, _nativeTokens, access: _access);
            await auth.SignOutAsync(default);
        }
        catch (Exception ex)                         // an async void handler: anything let out here would close the app
        {
            AuthStatus.Text = "Could not sign out: " + ex.Message;
            return;
        }
        AuthStatus.Text = "Signed out on this PC (browser sign-in tokens forgotten; the gateway cannot revoke them). The saved password, if any, is unchanged.";
    }

    private async Task DiscoverAfterSignInAsync(DashboardAuth auth)
    {
        var me = await auth.MeAsync(default);
        var profiles = await auth.ListProfilesAsync(
            string.IsNullOrWhiteSpace(_settings.GatewayId) ? auth.BaseUri.Host : _settings.GatewayId,
            default);
        foreach (var p in profiles) AddRow(p.Key.ProfileId);
        _settings.ProfileNames = profiles.ToDictionary(p => p.Key.ProfileId, p => p.DisplayName, StringComparer.Ordinal);
        _settings.KnownProfileIds = ProfileList.Children.OfType<CheckBox>().Select(c => (string)c.Content).ToList();
        AuthStatus.Text = $"Signed in as {me.Label}. Discovered {profiles.Count} profile(s). Tick those to show, then Save. Live chat still needs a send test with this session.";
        await LoadApprovalsAsync(auth);
    }

    private async void ReloadApprovals_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var auth = await EnsureAuthAsync();
            if (auth is null) return;
            await LoadApprovalsAsync(auth);
        }
        catch (Exception ex)
        {
            ApprovalStatus.Text = "Could not reload approvals: " + ex.Message;
        }
    }

    private async void ApprovalProfile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;                       // the first selection is loaded by Loaded
        await LoadApprovalsForSelectedAsync();
    }

    /// <summary>
    /// The selected profile's approval mode, with the app's session (or a saved sign-in). Runs as the window opens and on a
    /// profile change; before 0.5.1 it ran only on a change, so the modes stayed greyed out until the profile was switched.
    /// </summary>
    private async Task LoadApprovalsForSelectedAsync()
    {
        try
        {
            var auth = _liveAuth() ?? await TryAuthFromStoreAsync();
            if (auth is null) return;               // TryAuthFromStoreAsync said why
            await LoadApprovalsAsync(auth);
        }
        catch (Exception ex)                         // runs from async void handlers: anything let out here would close the app
        {
            ApprovalStatus.Text = "Could not load the approval setting: " + ex.Message;
        }
    }

    private async Task LoadApprovalsAsync(DashboardAuth auth)
    {
        var profile = ApprovalProfile.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(profile))
        {
            ApprovalStatus.Text = "Select a profile to load approvals.mode.";
            BuildApprovalRadios(enabled: false, selected: ApprovalMode.Manual);
            return;
        }
        var api = new ConfigApi(auth);
        try
        {
            var schema = await api.GetSchemaAsync(profile, default);
            _approvalSupported = schema.SupportsApprovalsMode;
            if (!_approvalSupported)
            {
                ApprovalStatus.Text = "This gateway schema does not expose approvals.mode — controls disabled.";
                BuildApprovalRadios(enabled: false, selected: ApprovalMode.Manual);
                return;
            }
            var values = await api.GetValuesAsync(profile, default);
            _loadedMode = values.ApprovalsMode;
            ApprovalStatus.Text = $"Effective: {_loadedMode.Label}. {_loadedMode.ScopeNote}";
            BuildApprovalRadios(enabled: true, selected: _loadedMode, options: schema.ApprovalsModeOptions);
        }
        catch (AuthFailedException ex)
        {
            ApprovalStatus.Text = $"Auth error ({ex.Kind}): {ex.Message}";
            BuildApprovalRadios(enabled: false, selected: _loadedMode);
        }
        catch (Exception ex)
        {
            ApprovalStatus.Text = "Approvals load failed: " + ex.Message;
            BuildApprovalRadios(enabled: false, selected: _loadedMode);
        }
    }

    private void BuildApprovalRadios(bool enabled, ApprovalMode selected, IReadOnlyList<string>? options = null)
    {
        ApprovalModes.Children.Clear();
        foreach (var mode in ApprovalMode.All)
        {
            if (options is { Count: > 0 } && !options.Any(o => string.Equals(o, mode.Wire, StringComparison.OrdinalIgnoreCase)))
                continue;
            var rb = new RadioButton
            {
                Content = mode.Label,
                Tag = mode,
                IsChecked = ReferenceEquals(mode, selected) || string.Equals(mode.Wire, selected.Wire, StringComparison.OrdinalIgnoreCase),
                IsEnabled = enabled,
                GroupName = "ApprovalMode",
                ToolTip = mode.Description + " " + mode.ScopeNote,
                Margin = new Thickness(0, 4, 0, 0)
            };
            var detail = new TextBlock
            {
                Text = mode.Description,
                Foreground = (Brush)FindResource("Brush.TextMuted"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(22, 0, 0, 6)
            };
            ApprovalModes.Children.Add(rb);
            ApprovalModes.Children.Add(detail);
        }
        if (!enabled && ApprovalModes.Children.Count == 0)
        {
            ApprovalModes.Children.Add(new TextBlock
            {
                Text = "Approval modes unavailable.",
                Foreground = (Brush)FindResource("Brush.TextMuted")
            });
        }
    }

    private ApprovalMode? SelectedApprovalMode()
    {
        foreach (var child in ApprovalModes.Children)
        {
            if (child is RadioButton { IsChecked: true, Tag: ApprovalMode mode })
                return mode;
        }
        return null;
    }

    private async Task<DashboardAuth?> EnsureAuthAsync()
    {
        var live = _liveAuth();
        if (live is not null) return live;
        return await TryAuthFromStoreAsync();
    }

    private async Task<DashboardAuth?> TryAuthFromStoreAsync()
    {
        PersistFields(keepPasswordIfBlank: true);
        if (_settings.ParsedUrl is null) { ApprovalStatus.Text = "Set dashboard URL first."; return null; }
        var password = _secrets.Get("gateway-password");
        var native = new DashboardAuth(_settings.ParsedUrl, null, _nativeTokens, access: _access);
        if (native.HasNativeSession) return native;
        native.Dispose();
        if (string.IsNullOrEmpty(_settings.Username) || string.IsNullOrEmpty(password))
        {
            ApprovalStatus.Text = "Sign in first — approvals need an authenticated dashboard session.";
            return null;
        }
        var auth = new DashboardAuth(_settings.ParsedUrl, null, _nativeTokens, access: _access);
        await auth.SignInAsync("basic", _settings.Username, password, default);
        return auth;
    }

    private void AppearancePreview_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressPreview || !IsLoaded) return;
        PreviewTheme();
    }

    private void PreviewTheme()
    {
        var themeId = ThemeCatalog.All.FirstOrDefault(t => t.DisplayName == ThemePicker.SelectedItem as string)?.Id
            ?? _settings.ThemeId;
        var mode = AppearLight.IsChecked == true ? AppearanceMode.Light
            : AppearDark.IsChecked == true ? AppearanceMode.Dark
            : AppearanceMode.System;
        ThemeService.Apply(themeId, mode);
        var def = ThemeCatalog.Get(themeId);
        ThemePreviewLabel.Text = $"{def.DisplayName} · {(ThemeService.IsLight ? "Light" : "Dark")}";
        ThemePreview.Background = (Brush)FindResource("Brush.Well");
        ThemePreviewLabel.Foreground = (Brush)FindResource("Brush.Text");
        UpdateMotionHint();
    }

    private void UpdateMotionHint()
    {
        MotionHint.Text = ThemeService.ReduceMotion
            ? "Reduced motion: panel changes are instant (Windows accessibility)."
            : ThemeService.HighContrast
                ? "High contrast: borders strengthened."
                : $"Motion: panel duration {ThemeService.PanelDuration.TotalMilliseconds:0} ms (respects OS reduced-motion).";
    }

    private void PersistFields(bool keepPasswordIfBlank)
    {
        _settings.GatewayId = GatewayId.Text.Trim();
        _settings.GatewayUrl = GatewayUrl.Text.Trim();
        _settings.Username = Username.Text.Trim();
        if (!keepPasswordIfBlank || Password.Password.Length > 0)
        {
            if (Password.Password.Length > 0) _secrets.Set("gateway-password", Password.Password);
        }
    }

    /// <summary>
    /// Turning notifications on registers Hikyaku's notification name for this Windows user (one per-user registry entry, no
    /// admin rights); turning them off removes it again.
    /// </summary>
    private void ApplyToastRegistration(bool enabled)
    {
        try
        {
            var registry = new HkcuToastRegistry();
            if (enabled) registry.Register(App.ToastAppId, "Hikyaku");
            else registry.Unregister(App.ToastAppId);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            MessageBox.Show(this, "Windows would not let Hikyaku set up notifications: " + ex.Message, "Notifications", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (ConnectionSection is null) return;   // raised while the window is still being built
        ApplyModeSections(ModeAdvanced.IsChecked == true);
    }

    /// <summary>
    /// Simple shows only what a beginner needs here (sign in / out, appearance, the view choice); everything that manages the
    /// gateway is Advanced. The hidden fields keep their values, so saving in Simple changes none of them.
    /// </summary>
    /// <summary>
    /// A new installation (no usable dashboard address, or no profile chosen) must be set up from this window even in Simple view, or the
    /// first-run screen ("Open Settings to add a profile") would be a dead end. Once set up, Simple hides the gateway fields again.
    /// </summary>
    internal bool SetupNeeded => _settings.ParsedUrl is null || _selection.Selected.Count == 0;

    internal void ApplyModeSections(bool advanced)
    {
        var plan = SettingsSections.For(advanced, SetupNeeded);
        Visibility V(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
        ConnectionSection.Visibility = V(plan.Connection);
        ConnectionHintSection.Visibility = V(plan.ConnectionHint);
        CredentialsSection.Visibility = V(plan.Credentials);
        SignInDiscover.Visibility = V(plan.SignInButtons);
        SignInDiscover.Content = plan.FullSignInLabel ? "Sign in and discover profiles" : "Sign in";
        AccountHeading.Visibility = V(plan.AccountHeading);
        AccessSection.Visibility = V(plan.Access);
        UpdatesSection.Visibility = V(plan.Updates);
        ProfilesSection.Visibility = V(plan.Profiles);
        ApprovalsSection.Visibility = V(plan.Approvals);
        NotificationsSection.Visibility = V(plan.Notifications);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        PersistFields(keepPasswordIfBlank: true);
        var boxes = ProfileList.Children.OfType<CheckBox>().ToList();
        _settings.KnownProfileIds = boxes.Select(b => (string)b.Content).ToList();
        _settings.NotificationsEnabled = NotificationsEnabled.IsChecked == true;
        _settings.CheckUpdatesOnStart = UpdateOnStart.IsChecked == true;
        _settings.SetAdvanced(ModeAdvanced.IsChecked == true);
        _settings.TextScalePercent = TextLargest.IsChecked == true ? 130 : TextLarger.IsChecked == true ? 115 : 100;
        ApplyToastRegistration(_settings.NotificationsEnabled);
        var themeId = ThemeCatalog.All.FirstOrDefault(t => t.DisplayName == ThemePicker.SelectedItem as string)?.Id
            ?? "washi-ink";
        _settings.ThemeId = themeId;
        var mode = AppearLight.IsChecked == true ? AppearanceMode.Light
            : AppearDark.IsChecked == true ? AppearanceMode.Dark
            : AppearanceMode.System;
        _settings.SetAppearanceMode(mode);
        _settings.Save(_settingsPath);
        if (Password.Password.Length > 0) _secrets.Set("gateway-password", Password.Password);
        Password.Clear();
        _secrets.Remove("gateway-token");
        var keys = boxes.Where(b => b.IsChecked == true && !string.IsNullOrWhiteSpace(_settings.GatewayId))
            .Select(b => new ProfileKey(_settings.GatewayId, (string)b.Content));
        try { await _selection.SetSelectedAsync(keys); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("Could not save the profile choice: " + ex.Message, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;                                  // stay open so nothing looks saved that was not
        }

        var chosen = SelectedApprovalMode();
        if (_approvalSupported && chosen is not null && !string.Equals(chosen.Wire, _loadedMode.Wire, StringComparison.OrdinalIgnoreCase))
        {
            if (ApprovalChangeGate.ConfirmRequired(_loadedMode, chosen))
            {
                var ok = MessageBox.Show(
                    chosen.Description + "\n\n" + chosen.ScopeNote + "\n\nApply Off for this profile?",
                    "Turn off dangerous-command prompts?",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning) == MessageBoxResult.OK;
                if (!ok)
                {
                    DialogResult = true;
                    return;
                }
            }
            try
            {
                var auth = await EnsureAuthAsync();
                var profile = ApprovalProfile.SelectedItem as string;
                if (auth is not null && !string.IsNullOrWhiteSpace(profile))
                {
                    var api = new ConfigApi(auth);
                    await api.SavePatchAsync(profile, ConfigPatch.For("approvals.mode", chosen.Wire), default);
                    var readBack = await api.GetValuesAsync(profile, default);
                    ApprovalStatus.Text = "Saved. Effective: " + readBack.ApprovalsMode.Label;
                    _loadedMode = readBack.ApprovalsMode;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save approval mode: " + ex.Message, "Approvals", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        ThemeService.Apply(_settings.ThemeId, _settings.AppearanceMode);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // Revert live preview to saved settings.
        ThemeService.Apply(_settings.ThemeId, _settings.AppearanceMode);
        DialogResult = false;
    }
}
