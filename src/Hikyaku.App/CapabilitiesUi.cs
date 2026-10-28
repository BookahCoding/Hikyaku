using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>One skill, toolset or MCP server row on the Capabilities page.</summary>
public sealed class CapabilityRowVM : Observable
{
    private bool _enabled;
    private bool _busy;
    private string _note;
    private readonly Action<CapabilityRowVM> _toggle;
    private readonly Action<CapabilityRowVM>? _test;

    public CapabilityRowVM(string kind, string name, string title, string detail, string meta, bool enabled, string note,
        bool canToggle, Action<CapabilityRowVM> toggle, Action<CapabilityRowVM>? test = null)
    {
        Kind = kind; Name = name; Title = title; Detail = detail; Meta = meta; _enabled = enabled; _note = note; CanToggle = canToggle;
        _toggle = toggle; _test = test;
        ToggleCommand = new RelayCommand(_ => _toggle(this), _ => CanToggle && !_busy);
        TestCommand = new RelayCommand(_ => _test?.Invoke(this), _ => _test is not null && !_busy);
    }

    public string Kind { get; }
    public string Name { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Meta { get; }
    public bool CanToggle { get; }
    public bool Enabled { get => _enabled; set { Set(ref _enabled, value); Raise(nameof(StateLabel)); Raise(nameof(ToggleLabel)); } }
    public string Note { get => _note; set { Set(ref _note, value); Raise(nameof(NoteVisibility)); } }
    public bool Busy { get => _busy; set { Set(ref _busy, value); ((RelayCommand)ToggleCommand).Refresh(); ((RelayCommand)TestCommand).Refresh(); } }

    public string StateLabel => Enabled ? "On" : "Off";
    public string ToggleLabel => Enabled ? "Turn off" : "Turn on";
    public Visibility NoteVisibility => _note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TestVisibility => _test is not null ? Visibility.Visible : Visibility.Collapsed;
    public ICommand ToggleCommand { get; }
    public ICommand TestCommand { get; }
}

/// <summary>
/// Capabilities (Hermes Desktop's settings for skills, toolsets and MCP servers; stays visible in Simple mode). Everything is for
/// one profile, the active one, and a switch is saved to that profile's config on the gateway and applies from the next chat.
/// Turning a toolset or an MCP server ON widens what the agent can do, so it is confirmed in words; turning anything OFF and
/// switching skills are not. A connection test starts the server on the gateway, so it is confirmed too. Nothing is retried.
/// </summary>
public sealed partial class MainViewModel
{
    private Func<CapabilitiesApi?> _capApi = () => null;
    private string _capStatus = "";
    private bool _capLoading;
    private int _capRefreshId;
    private string _capProfile = "";

    public ObservableCollection<CapabilityRowVM> SkillRows { get; } = new();
    public ObservableCollection<CapabilityRowVM> ToolsetRows { get; } = new();
    public ObservableCollection<CapabilityRowVM> McpRows { get; } = new();

    public ICommand? RefreshCapabilitiesCommand { get; private set; }
    public ICommand? ShowCapabilitiesCommand { get; private set; }

    /// <summary>Shows what a connection test found (title, text).</summary>
    public Action<string, string>? ShowCapabilityResult { get; set; }

    public void AttachCapabilities(Func<CapabilitiesApi?> api) => _capApi = api;

    public string CapabilitiesStatus { get => _capStatus; private set { Set(ref _capStatus, value); Raise(nameof(CapabilitiesStatusVisibility)); } }
    public Visibility CapabilitiesStatusVisibility => _capStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool CapabilitiesLoading { get => _capLoading; private set { Set(ref _capLoading, value); (RefreshCapabilitiesCommand as RelayCommand)?.Refresh(); } }
    public string CapabilitiesProfileLabel => _capProfile.Length > 0 ? $"Profile: {_capProfile}. Changes apply to this profile from the next chat." : "";
    public Visibility CapabilitiesVisibility =>
        ChatShellVisibility == Visibility.Visible && Nav == MainNav.Capabilities ? Visibility.Visible : Visibility.Collapsed;

    private void InitCapabilities()
    {
        RefreshCapabilitiesCommand = new RelayCommand(_ => _ = RefreshCapabilitiesAsync(), _ => !_capLoading);
        ShowCapabilitiesCommand = new RelayCommand(_ => { Nav = MainNav.Capabilities; _ = RefreshCapabilitiesAsync(); });
    }

    private static string CapMessage(RestException ex) => ex.Failure switch
    {
        RestFailure.Auth => "Sign in again to manage capabilities.",
        RestFailure.Unsupported => "This gateway does not offer this list through the dashboard.",
        RestFailure.NotFound => "That item no longer exists. The list was refreshed.",
        _ => ex.Message
    };

    public async Task RefreshCapabilitiesAsync()
    {
        if (_capApi() is not { } api) { CapabilitiesStatus = "Connect to a gateway to manage its capabilities."; return; }
        var profile = ActiveProfileId();
        if (profile is null || !CronAllowed(profile)) { CapabilitiesStatus = "Choose a profile first."; return; }
        var id = ++_capRefreshId;
        _capProfile = profile;
        Raise(nameof(CapabilitiesProfileLabel));
        CapabilitiesLoading = true;
        CapabilitiesStatus = "";
        // Each list stands alone: a gateway without MCP routes still shows skills.
        var notes = new List<string>();
        async Task<List<T>?> Load<T>(string what, Func<Task<IReadOnlyList<T>>> call)
        {
            try { return (await call().ConfigureAwait(true)).ToList(); }
            catch (RestException ex) { notes.Add($"{what}: {CapMessage(ex)}"); return null; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
            { notes.Add($"{what}: couldn't reach the gateway ({ex.Message})"); return null; }
        }
        try
        {
            var skills = await Load("Skills", () => api.SkillsAsync(profile, CancellationToken.None));
            var toolsets = await Load("Toolsets", () => api.ToolsetsAsync(profile, CancellationToken.None));
            var mcp = await Load("MCP servers", () => api.McpServersAsync(profile, CancellationToken.None));
            if (id != _capRefreshId) return;
            if (skills is not null) Replace(SkillRows, skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s => SkillRow(profile, s)));
            if (toolsets is not null) Replace(ToolsetRows, toolsets.Select(t => ToolsetRow(profile, t)));
            if (mcp is not null) { RememberMcpServers(mcp, profile); Replace(McpRows, mcp.Select(m => McpRow(profile, m))); }
            CapabilitiesStatus = string.Join("\n", notes);
        }
        finally { if (id == _capRefreshId) CapabilitiesLoading = false; }
    }

    private static void Replace(ObservableCollection<CapabilityRowVM> rows, IEnumerable<CapabilityRowVM> next)
    {
        rows.Clear();
        foreach (var r in next) rows.Add(r);
    }

    private CapabilityRowVM SkillRow(string profile, SkillInfo s) => new("skill", s.Name, s.Name, s.Description,
        string.Join(" · ", new[] { s.Category, s.Provenance, s.Usage > 0 ? $"used {s.Usage}×" : null }.Where(x => !string.IsNullOrWhiteSpace(x))),
        s.Enabled, "", true, r => _ = ToggleCapabilityAsync(profile, r));

    private CapabilityRowVM ToolsetRow(string profile, ToolsetInfo t) => new("toolset", t.Name, t.Label, t.Description,
        t.Tools.Count > 0 ? $"{t.Tools.Count} tools: " + string.Join(", ", t.Tools.Take(6)) + (t.Tools.Count > 6 ? ", …" : "") : "",
        t.Enabled, !t.Configured ? "Needs setup on the gateway (a key or login) before it can be turned on." : "",
        t.Configured || t.Enabled, r => _ = ToggleCapabilityAsync(profile, r));

    private CapabilityRowVM McpRow(string profile, McpServerInfo m) => new("mcp", m.Name, m.Name, m.Target,
        m.Transport, m.Enabled, m.Locked ? $"Provided by the {m.Plugin} plugin; switch it there." : "",
        !m.Locked, r => _ = ToggleCapabilityAsync(profile, r), r => _ = TestMcpAsync(profile, r));

    public async Task ToggleCapabilityAsync(string profile, CapabilityRowVM row)
    {
        if (!Shows(ExperienceFeature.Capabilities)) return;                                  // Simple view never reaches it, whoever asks
        if (_capApi() is not { } api || !CronAllowed(profile)) return;
        var turnOn = !row.Enabled;
        if (turnOn && row.Kind is "toolset" or "mcp")
        {
            var what = row.Kind == "toolset"
                ? $"Turn on the “{row.Title}” toolset for profile {profile}?\n\nThe agent in this profile's chats can use these tools from the next chat on. Approvals, YOLO and folder grants still apply as set."
                : $"Turn on the MCP server “{row.Name}” for profile {profile}?\n\nIt connects to {row.Detail} when a chat starts and gives the agent its tools, from the next chat on. Approvals and YOLO still apply as set.";
            if (ConfirmCapability?.Invoke("Turn on", what) != true) return;
        }
        row.Busy = true;
        try
        {
            switch (row.Kind)
            {
                case "skill": await api.SetSkillEnabledAsync(profile, row.Name, turnOn, CancellationToken.None).ConfigureAwait(true); break;
                case "toolset": await api.SetToolsetEnabledAsync(profile, row.Name, turnOn, CancellationToken.None).ConfigureAwait(true); break;
                default: await api.SetMcpServerEnabledAsync(profile, row.Name, turnOn, CancellationToken.None).ConfigureAwait(true); break;
            }
            row.Enabled = turnOn;     // only after the gateway accepted it
            CapabilitiesStatus = $"{(turnOn ? "Turned on" : "Turned off")} “{row.Title}” for profile {profile}. It applies from the next chat.";
        }
        catch (RestException ex)
        {
            CapabilitiesStatus = CapMessage(ex);
            if (ex.Failure == RestFailure.NotFound) _ = RefreshCapabilitiesAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        {
            CapabilitiesStatus = "Couldn't reach the gateway: " + ex.Message;
        }
        finally { row.Busy = false; }
    }

    /// <summary>Asks before something that widens what the agent can do or starts something on the gateway. Unset means no.</summary>
    public Func<string, string, bool>? ConfirmCapability { get; set; }

    public async Task TestMcpAsync(string profile, CapabilityRowVM row)
    {
        if (!Shows(ExperienceFeature.Capabilities)) return;                                  // Simple view never reaches it, whoever asks
        if (_capApi() is not { } api || !CronAllowed(profile)) return;
        if (ConfirmCapability?.Invoke("Test connection",
                $"Test the MCP server “{row.Name}”?\n\nThe gateway will connect to {row.Detail} (starting it if it is a local command), list its tools and disconnect.") != true)
            return;
        row.Busy = true;
        try
        {
            var r = await api.TestMcpServerAsync(profile, row.Name, CancellationToken.None).ConfigureAwait(true);
            var text = r.Ok
                ? $"Connected. {r.Tools.Count} tool(s):\n" + string.Join("\n", r.Tools.Take(40).Select(t => "• " + t.Name))
                : "Could not connect" + (string.IsNullOrWhiteSpace(r.Error) ? "." : ": " + r.Error);
            CapabilitiesStatus = $"{row.Name}: " + (r.Ok ? $"connected, {r.Tools.Count} tool(s)." : "could not connect.");
            ShowCapabilityResult?.Invoke("MCP connection test", text);
        }
        catch (RestException ex) { CapabilitiesStatus = CapMessage(ex); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        {
            CapabilitiesStatus = "Couldn't reach the gateway: " + ex.Message;
        }
        finally { row.Busy = false; }
    }
}
