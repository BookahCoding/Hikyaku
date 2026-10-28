using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>
/// The gateway's config.yaml and MCP servers (Capabilities → Config / MCP servers). <b>Advanced view only</b> (<see cref="ExperienceFeature.ConfigEditor"/>,
/// <see cref="ExperienceFeature.McpEdit"/>; approved by the maintainer on the condition that Simple hides them). These write the gateway's own
/// configuration, so: every write is confirmed in words naming the profile and what it changes; a raw save first checks that the file
/// has not changed on the gateway since it was loaded (a stale editor never overwrites a newer file), keeps a copy of the previous text on
/// this PC, and refuses if that copy cannot be written; an MCP server is a validated patch that touches only that server's keys, a literal secret
/// in an environment variable or header is called out (use a ${VARIABLE}), and with no confirmation handler nothing is sent.
/// </summary>
public sealed partial class MainViewModel
{
    private Func<GatewayConfigEditor?> _configApi = () => null;
    private string _configYaml = "", _configLoadedYaml = "", _configPath = "", _configStatus = "", _configProfile = "";
    private bool _configBusy;
    private bool _mcpFormOpen, _mcpRemote, _mcpEnabled = true;
    private bool _mcpEditing;
    private string _mcpName = "", _mcpCommand = "", _mcpArgs = "", _mcpEnv = "", _mcpUrl = "", _mcpHeaders = "", _mcpTimeout = "", _mcpConnectTimeout = "";
    private string _mcpProblems = "", _mcpNote = "";
    private readonly Dictionary<string, McpServerInfo> _mcpInfo = new(StringComparer.Ordinal);
    private bool _mcpListLoaded;
    private string _mcpListProfile = "";                                                     // the profile the remembered server list belongs to

    public ICommand? LoadConfigCommand { get; private set; }
    public ICommand? SaveConfigCommand { get; private set; }
    public ICommand? AddMcpServerCommand { get; private set; }
    public ICommand? EditMcpServerCommand { get; private set; }
    public ICommand? SaveMcpServerCommand { get; private set; }
    public ICommand? CancelMcpFormCommand { get; private set; }

    public void AttachConfigEditor(Func<GatewayConfigEditor?> api) => _configApi = api;

    /// <summary>Asked before any write to config.yaml (title, message) → true only on an explicit yes. Unset means no.</summary>
    public Func<string, string, bool>? ConfirmConfig { get; set; }

    /// <summary>Where the previous text of config.yaml is copied before a raw save. Replaceable in tests.</summary>
    public string ConfigBackupDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hikyaku", "config-backups");

    public string ConfigYaml { get => _configYaml; set { Set(ref _configYaml, value ?? ""); Raise(nameof(ConfigDirty)); (SaveConfigCommand as RelayCommand)?.Refresh(); } }
    public bool ConfigDirty => _configYaml != _configLoadedYaml;
    public string ConfigPath { get => _configPath; private set => Set(ref _configPath, value); }
    public string ConfigStatus { get => _configStatus; private set { Set(ref _configStatus, value); Raise(nameof(ConfigStatusVisibility)); } }
    public Visibility ConfigStatusVisibility => _configStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool ConfigBusy { get => _configBusy; private set { Set(ref _configBusy, value); RefreshConfigCommands(); } }
    public Visibility ConfigEditorVisibility => Vis(ExperienceFeature.ConfigEditor);
    public Visibility McpEditVisibility => Vis(ExperienceFeature.McpEdit);

    // the MCP form
    public bool McpFormOpen { get => _mcpFormOpen; private set { Set(ref _mcpFormOpen, value); Raise(nameof(McpFormVisibility)); } }
    public Visibility McpFormVisibility => _mcpFormOpen ? Visibility.Visible : Visibility.Collapsed;
    public bool McpEditing { get => _mcpEditing; private set { Set(ref _mcpEditing, value); Raise(nameof(McpNameEditable)); Raise(nameof(McpTransportEditable)); Raise(nameof(McpFormTitle)); } }
    public bool McpNameEditable => !_mcpEditing;
    /// <summary>The transport of an existing server cannot be changed here: a merge cannot delete the old command / address.</summary>
    public bool McpTransportEditable => !_mcpEditing;
    public string McpFormTitle => _mcpEditing ? "Edit MCP server" : "Add an MCP server";
    public string McpName { get => _mcpName; set => Set(ref _mcpName, value ?? ""); }
    public bool McpRemote { get => _mcpRemote; set { Set(ref _mcpRemote, value); Raise(nameof(McpLocal)); Raise(nameof(McpLocalVisibility)); Raise(nameof(McpRemoteVisibility)); } }
    public bool McpLocal { get => !_mcpRemote; set => McpRemote = !value; }
    public Visibility McpLocalVisibility => _mcpRemote ? Visibility.Collapsed : Visibility.Visible;
    public Visibility McpRemoteVisibility => _mcpRemote ? Visibility.Visible : Visibility.Collapsed;
    public string McpCommand { get => _mcpCommand; set => Set(ref _mcpCommand, value ?? ""); }
    public string McpArgs { get => _mcpArgs; set => Set(ref _mcpArgs, value ?? ""); }
    public string McpEnv { get => _mcpEnv; set => Set(ref _mcpEnv, value ?? ""); }
    public string McpUrl { get => _mcpUrl; set => Set(ref _mcpUrl, value ?? ""); }
    public string McpHeaders { get => _mcpHeaders; set => Set(ref _mcpHeaders, value ?? ""); }
    public bool McpEnabled { get => _mcpEnabled; set => Set(ref _mcpEnabled, value); }
    public string McpTimeout { get => _mcpTimeout; set => Set(ref _mcpTimeout, value ?? ""); }
    public string McpConnectTimeout { get => _mcpConnectTimeout; set => Set(ref _mcpConnectTimeout, value ?? ""); }
    public string McpProblems { get => _mcpProblems; private set { Set(ref _mcpProblems, value); Raise(nameof(McpProblemsVisibility)); } }
    public Visibility McpProblemsVisibility => _mcpProblems.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string McpNote { get => _mcpNote; private set { Set(ref _mcpNote, value); Raise(nameof(McpNoteVisibility)); } }
    public Visibility McpNoteVisibility => _mcpNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void InitConfigEditor()
    {
        LoadConfigCommand = new RelayCommand(_ => _ = LoadConfigAsync(), _ => !_configBusy);
        SaveConfigCommand = new RelayCommand(_ => _ = SaveConfigAsync(), _ => !_configBusy && ConfigDirty);
        AddMcpServerCommand = new RelayCommand(_ => OpenMcpForm(null), _ => Shows(ExperienceFeature.McpEdit));
        EditMcpServerCommand = new RelayCommand(p => { if (p is CapabilityRowVM r && _mcpInfo.TryGetValue(r.Name, out var info)) OpenMcpForm(info); },
            p => p is CapabilityRowVM { CanToggle: true } && Shows(ExperienceFeature.McpEdit));
        SaveMcpServerCommand = new RelayCommand(_ => _ = SaveMcpServerAsync(), _ => !_configBusy);
        CancelMcpFormCommand = new RelayCommand(_ => CloseMcpForm());
    }

    private void RefreshConfigCommands()
    {
        (LoadConfigCommand as RelayCommand)?.Refresh();
        (SaveConfigCommand as RelayCommand)?.Refresh();
        (SaveMcpServerCommand as RelayCommand)?.Refresh();
    }

    private bool ConfigProfile(out GatewayConfigEditor api, out string profile)
    {
        api = null!; profile = "";
        if (!Shows(ExperienceFeature.ConfigEditor)) return false;                       // Simple view never reaches the editor, whoever asks
        if (_configApi() is not { } a) { ConfigStatus = "Connect to a gateway to edit its configuration."; return false; }
        var p = ActiveProfileId();
        if (p is null || !CronAllowed(p)) { ConfigStatus = "Choose a profile first."; return false; }
        if (_configProfile != p)
        {
            // Switching from another profile: never show one profile's file under another. (The first use just remembers the profile.)
            var switched = _configProfile.Length > 0;
            _configProfile = p;
            if (switched) { _configLoadedYaml = ""; ConfigYaml = ""; ConfigPath = ""; CloseMcpForm(); _mcpInfo.Clear(); _mcpListLoaded = false; }
        }
        api = a; profile = p;
        return true;
    }

    private static string ConfigReason(RestException ex) => ex.Failure switch
    {
        RestFailure.Auth => "Sign in again to edit the configuration.",
        RestFailure.Unsupported => "This gateway does not offer configuration editing through the dashboard.",
        RestFailure.Invalid => "The gateway refused it: " + ex.Message,
        _ => ex.Message
    };

    public async Task LoadConfigAsync()
    {
        if (!ConfigProfile(out var api, out var profile)) return;
        ConfigBusy = true;
        ConfigStatus = "Loading…";
        try
        {
            var raw = await api.RawAsync(profile, CancellationToken.None).ConfigureAwait(true);
            _configLoadedYaml = raw.Yaml;
            _configYaml = raw.Yaml;
            Raise(nameof(ConfigYaml)); Raise(nameof(ConfigDirty));
            ConfigPath = raw.Path;
            ConfigStatus = $"Loaded config.yaml for profile {profile}.";
        }
        catch (RestException ex) { ConfigStatus = ConfigReason(ex); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        { ConfigStatus = "Couldn't reach the gateway: " + ex.Message; }
        finally { ConfigBusy = false; }
    }

    /// <summary>Replaces the whole file. Refuses when nothing changed, when the file changed on the gateway since it was loaded, or when no safety copy can be kept.</summary>
    public async Task SaveConfigAsync()
    {
        if (!ConfigDirty || !ConfigProfile(out var api, out var profile)) return;
        if (_configLoadedYaml.Length == 0) { ConfigStatus = "Load the configuration first, so there is a safety copy and a way to tell if it changed meanwhile."; return; }
        ConfigBusy = true;
        try
        {
            ConfigStatus = "Checking the file has not changed on the gateway…";
            var current = await api.RawAsync(profile, CancellationToken.None).ConfigureAwait(true);
            if (!string.Equals(current.Yaml, _configLoadedYaml, StringComparison.Ordinal))
            {
                ConfigStatus = "The configuration changed on the gateway since you loaded it. Nothing was saved. Load it again and re-apply your change.";
                return;
            }
            if (ConfirmConfig?.Invoke("Replace configuration",
                    $"Replace the whole config.yaml of profile “{profile}” on the gateway?\n\nEveryone and every client using this profile is affected. A mistake in the settings can stop Hermes working or change what the agent can reach. " +
                    $"The gateway refuses text that is not a YAML mapping. A copy of the current file is kept on this PC first (plain text: it may contain secrets that are in the file).") != true)
            { ConfigStatus = "Nothing was saved."; return; }

            string backup;
            try { backup = WriteConfigBackup(profile, _configLoadedYaml); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { ConfigStatus = "Couldn't keep a safety copy on this PC, so nothing was changed: " + ex.Message; return; }

            // The dialog may have been open for minutes: look again immediately before writing, not only before asking.
            var latest = await api.RawAsync(profile, CancellationToken.None).ConfigureAwait(true);
            if (!string.Equals(latest.Yaml, _configLoadedYaml, StringComparison.Ordinal))
            {
                ConfigStatus = "The configuration changed on the gateway while you were deciding. Nothing was saved. Load it again and re-apply your change.";
                return;
            }
            ConfigStatus = "Saving…";
            await api.SaveRawAsync(profile, _configYaml, CancellationToken.None).ConfigureAwait(true);
            var after = await api.RawAsync(profile, CancellationToken.None).ConfigureAwait(true);
            _configLoadedYaml = after.Yaml; _configYaml = after.Yaml;
            Raise(nameof(ConfigYaml)); Raise(nameof(ConfigDirty));
            ConfigStatus = $"Saved. The gateway reads config.yaml again on its own; the previous version is kept at {backup}.";
        }
        catch (RestException ex) { ConfigStatus = "Not saved: " + ConfigReason(ex); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        { ConfigStatus = "Couldn't reach the gateway: " + ex.Message; }
        finally { ConfigBusy = false; }
    }

    /// <summary>How many copies of a profile's config.yaml are kept; older ones are deleted. The copies are plain text and can hold secrets from the file.</summary>
    public const int ConfigBackupsKept = 10;

    internal string WriteConfigBackup(string profile, string yaml)
    {
        Directory.CreateDirectory(ConfigBackupDirectory);
        var safe = new string(profile.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        // The short hash keeps "a.b" and "a_b" (same cleaned name) apart, and the exact shape below keeps "work" from matching "work-2".
        safe += "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(profile)))[..6].ToLowerInvariant();
        var path = Path.Combine(ConfigBackupDirectory, $"{safe}-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}.yaml");
        File.WriteAllText(path, yaml);
        try
        {
            foreach (var old in new DirectoryInfo(ConfigBackupDirectory).GetFiles(safe + "-*.yaml")
                         .Where(f => System.Text.RegularExpressions.Regex.IsMatch(f.Name, "^" + System.Text.RegularExpressions.Regex.Escape(safe) + @"-\d{8}-\d{9}-[0-9a-f]{8}\.yaml$"))
                         .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name).Skip(ConfigBackupsKept))
                old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* pruning is best effort; the new copy is already safe */ }
        return path;
    }

    // ---- MCP servers ----

    internal void RememberMcpServers(IEnumerable<McpServerInfo> servers, string? profile = null)
    {
        _mcpInfo.Clear();
        foreach (var s in servers) _mcpInfo[s.Name] = s;
        _mcpListLoaded = true;
        _mcpListProfile = profile ?? ActiveProfileId() ?? "";   // the profile the list was asked for, not whichever is active when the answer lands
    }

    private void OpenMcpForm(McpServerInfo? existing)
    {
        if (!Shows(ExperienceFeature.McpEdit)) return;
        McpEditing = existing is not null;
        McpName = existing?.Name ?? "";
        McpRemote = existing is not null ? existing.Transport == "http" || existing.Url is not null : false;
        McpCommand = existing?.Command ?? "";
        McpArgs = existing is null ? "" : string.Join("\n", existing.Args);
        McpUrl = existing?.Url ?? "";
        McpEnv = ""; McpHeaders = ""; McpTimeout = ""; McpConnectTimeout = "";
        McpEnabled = existing?.Enabled ?? true;
        McpProblems = "";
        McpNote = existing is null
            ? "Use ${NAME} for a secret (the gateway reads it from its environment) instead of typing the value."
            : "Environment variables and headers are not shown (they may hold secrets). Fields you fill in are merged into the server; leave them blank to keep what is there. To remove one, use the config.yaml editor.";
        McpFormOpen = true;
    }

    private void CloseMcpForm() { McpFormOpen = false; McpProblems = ""; }

    private McpServerSpec? BuildMcpSpec(out IReadOnlyList<string> problems)
    {
        var probs = new List<string>();
        var (env, envProblems) = McpServerSpec.ParsePairs(_mcpEnv, '=');
        var (headers, headerProblems) = McpServerSpec.ParsePairs(_mcpHeaders, ':');
        probs.AddRange(_mcpRemote ? headerProblems : envProblems);
        int? Num(string text, string what)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (int.TryParse(text.Trim(), out var n)) return n;
            probs.Add($"{what} must be a whole number of seconds.");
            return null;
        }
        var timeout = Num(_mcpTimeout, "The timeout");
        var connect = Num(_mcpConnectTimeout, "The connection timeout");
        var spec = new McpServerSpec(_mcpName.Trim(), _mcpRemote ? McpTransport.Url : McpTransport.Command, _mcpCommand,
            McpServerSpec.ParseArgs(_mcpArgs), _mcpRemote ? new Dictionary<string, string>() : env,
            _mcpUrl.Trim(), _mcpRemote ? headers : new Dictionary<string, string>(), _mcpEnabled, timeout, connect);
        probs.AddRange(spec.Problems());
        problems = probs;
        return probs.Count == 0 ? spec : null;
    }

    public async Task SaveMcpServerAsync()
    {
        if (!Shows(ExperienceFeature.McpEdit) || !ConfigProfile(out var api, out var profile)) return;
        var spec = BuildMcpSpec(out var problems);
        if (spec is null) { McpProblems = string.Join("\n", problems); return; }
        McpProblems = "";
        if (_mcpListLoaded && _mcpListProfile != profile)                                                       // the list was read for another profile: the checks below would be about the wrong file
        {
            _mcpInfo.Clear(); _mcpListLoaded = false;
            McpProblems = "The profile changed since the server list was loaded. Refresh Capabilities, then try again.";
            return;
        }
        // A merge can neither delete a key nor tell "add" from "change": check against the servers the gateway listed.
        if (_mcpEditing)
        {
            if (!_mcpInfo.TryGetValue(spec.Name, out var current)) { McpProblems = $"\u201c{spec.Name}\u201d is no longer in the server list. Refresh the list, then try again."; return; }
            var wasRemote = current.Transport == "http" || current.Url is not null;
            if (wasRemote != (spec.Transport == McpTransport.Url))
            {
                McpProblems = "Changing between a local command and a remote address is not done here: the old command or address would stay in the file. Use the Config tab to change it.";
                return;
            }
        }
        else
        {
            if (!_mcpListLoaded) { McpProblems = "The server list has not been loaded yet. Refresh Capabilities first, so a name that already exists is not overwritten."; return; }
            if (_mcpInfo.ContainsKey(spec.Name)) { McpProblems = $"A server called \u201c{spec.Name}\u201d already exists. Use Edit on it instead."; return; }
        }
        var secrets = spec.PlaintextSecretKeys();
        var summary = spec.Transport == McpTransport.Command
            ? $"Runs: {spec.Command} {string.Join(" ", spec.Args)}".TrimEnd()
            : $"Connects to: {spec.Url}";
        var message = $"{(_mcpEditing ? "Change" : "Add")} the MCP server “{spec.Name}” in profile “{profile}”?\n\n{summary}\n\n" +
                      "It is written into config.yaml. The agent in this profile's chats will be able to use this server's tools, and a local command runs on the gateway machine. " +
                      "Approvals and YOLO still apply as set." +
                      (secrets.Count > 0
                          ? $"\n\n⚠ {string.Join(", ", secrets)} look like secrets but hold the value itself, so it would be stored in config.yaml in plain text. Use ${{NAME}} to refer to an environment variable instead."
                          : "");
        if (ConfirmConfig?.Invoke(_mcpEditing ? "Change MCP server" : "Add MCP server", message) != true) { ConfigStatus = "Nothing was saved."; return; }
        ConfigBusy = true;
        ConfigStatus = "Saving…";
        try
        {
            await api.SavePatchAsync(profile, spec.ToPatch(), CancellationToken.None).ConfigureAwait(true);
            ConfigStatus = $"{(_mcpEditing ? "Changed" : "Added")} “{spec.Name}”. The gateway picks up config.yaml changes on its own; it applies from the next chat.";
            CloseMcpForm();
            _ = RefreshCapabilitiesAsync();
        }
        catch (RestException ex) { ConfigStatus = "Not saved: " + ConfigReason(ex); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        { ConfigStatus = "Couldn't reach the gateway: " + ex.Message; }
        finally { ConfigBusy = false; }
    }
}
