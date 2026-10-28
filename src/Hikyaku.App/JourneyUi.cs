using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>One skill or memory on the timeline.</summary>
public sealed class JourneyNodeVM(JourneyNode node, ICommand open)
{
    public JourneyNode Node { get; } = node;
    public string Title => Node.Title;
    public string DetailLine => Node.DetailLine;
    public string Glyph => Node.IsMemory ? "◌" : "✦";
    public Visibility PinnedVisibility => Node.Pinned ? Visibility.Visible : Visibility.Collapsed;
    public ICommand OpenCommand { get; } = open;
}

public sealed class JourneyMonthVM(string label, IEnumerable<JourneyNodeVM> nodes)
{
    public string Label { get; } = label;
    public ObservableCollection<JourneyNodeVM> Nodes { get; } = new(nodes);
}

/// <summary>
/// Journey (Hermes Desktop's memory graph / star map): what the profile's agent has learned, newest first by month, with Desktop's
/// All / Used / Learned filter, and a node's own text on request. Read-only: Desktop can edit and delete nodes from its panel, but no
/// wire call for that is documented anywhere Hikyaku may rely on, so none is made. The data is the active profile's
/// (<c>GET api/learning/graph</c>, <c>GET api/learning/node</c>); it is a timeline list, not Desktop's zoomable graph.
/// </summary>
public sealed partial class MainViewModel
{
    private Func<JourneyApi?> _journeyApi = () => null;
    private JourneyGraph? _journeyGraph;
    private JourneyFilter _journeyFilter = JourneyFilter.All;
    private string _journeyStatus = "";
    private string _journeySummary = "";
    private string _journeyProfile = "";
    private bool _journeyLoading;
    private int _journeyRefreshId, _journeyDetailId;
    private JourneyNodeVM? _journeyOpen;
    private string _journeyDetailText = "";
    private string _journeyDetailNote = "";

    public ObservableCollection<JourneyMonthVM> JourneyMonths { get; } = new();
    public ICommand? ShowJourneyCommand { get; private set; }
    public ICommand? RefreshJourneyCommand { get; private set; }
    public ICommand? SetJourneyFilterCommand { get; private set; }
    public ICommand? OpenJourneyNodeCommand { get; private set; }
    public ICommand? CloseJourneyNodeCommand { get; private set; }

    public void AttachJourney(Func<JourneyApi?> api) => _journeyApi = api;

    public string JourneyStatus { get => _journeyStatus; private set { Set(ref _journeyStatus, value); Raise(nameof(JourneyStatusVisibility)); } }
    public Visibility JourneyStatusVisibility => _journeyStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string JourneySummary { get => _journeySummary; private set => Set(ref _journeySummary, value); }
    public string JourneyProfileLabel => _journeyProfile.Length > 0 ? "Profile: " + _journeyProfile : "";
    public bool JourneyLoading { get => _journeyLoading; private set { Set(ref _journeyLoading, value); (RefreshJourneyCommand as RelayCommand)?.Refresh(); } }
    public JourneyFilter JourneyFilterMode => _journeyFilter;
    public Visibility JourneyVisibility =>
        ChatShellVisibility == Visibility.Visible && Nav == MainNav.Journey ? Visibility.Visible : Visibility.Collapsed;
    public Visibility JourneyDetailVisibility => _journeyOpen is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility JourneyListVisibility => _journeyOpen is null ? Visibility.Visible : Visibility.Collapsed;
    public string JourneyDetailTitle => _journeyOpen?.Title ?? "";
    public string JourneyDetailSubtitle => _journeyOpen?.DetailLine ?? "";
    public string JourneyDetailText { get => _journeyDetailText; private set => Set(ref _journeyDetailText, value); }
    public string JourneyDetailNote { get => _journeyDetailNote; private set { Set(ref _journeyDetailNote, value); Raise(nameof(JourneyDetailNoteVisibility)); } }
    public Visibility JourneyDetailNoteVisibility => _journeyDetailNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void InitJourney()
    {
        ShowJourneyCommand = new RelayCommand(_ => { Nav = MainNav.Journey; _ = RefreshJourneyAsync(); });
        RefreshJourneyCommand = new RelayCommand(_ => _ = RefreshJourneyAsync(), _ => !_journeyLoading);
        SetJourneyFilterCommand = new RelayCommand(p =>
        {
            if (p is JourneyFilter f) SetJourneyFilter(f);
            else if (p is string s && Enum.TryParse<JourneyFilter>(s, out var parsed)) SetJourneyFilter(parsed);
        });
        OpenJourneyNodeCommand = new RelayCommand(p => { if (p is JourneyNodeVM n) _ = OpenJourneyNodeAsync(n); });
        CloseJourneyNodeCommand = new RelayCommand(_ => CloseJourneyNode());
    }

    /// <summary>Synthetic UI acceptance only: shows a graph without any gateway.</summary>
    internal void AcceptanceSetJourney(JourneyGraph graph, string profile)
    {
        _journeyGraph = graph;
        _journeyProfile = profile;
        Raise(nameof(JourneyProfileLabel));
        PaintJourney();
    }

    public void SetJourneyFilter(JourneyFilter f)
    {
        _journeyFilter = f;
        Raise(nameof(JourneyFilterMode));
        PaintJourney();
    }

    public async Task RefreshJourneyAsync()
    {
        if (_journeyApi() is not { } api) { JourneyStatus = "Connect to a gateway to see what its agent has learned."; return; }
        var profile = ActiveProfileId();
        if (profile is null || !CronAllowed(profile)) { JourneyStatus = "Choose a profile first."; return; }
        var id = ++_journeyRefreshId;
        if (_journeyProfile != profile) { _journeyGraph = null; CloseJourneyNode(); PaintJourney(); }   // another profile's learning is never shown under this one
        _journeyProfile = profile;
        Raise(nameof(JourneyProfileLabel));
        JourneyLoading = true;
        JourneyStatus = "";
        try
        {
            var graph = await api.GraphAsync(profile, CancellationToken.None).ConfigureAwait(true);
            if (id != _journeyRefreshId) return;
            _journeyGraph = graph;
            PaintJourney();
        }
        catch (RestException ex)
        {
            if (id == _journeyRefreshId)
                JourneyStatus = ex.Failure switch
                {
                    RestFailure.Auth => "Sign in again to see what the agent has learned.",
                    RestFailure.Unsupported => "This gateway does not offer the learning graph through the dashboard.",
                    _ => ex.Message
                };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        {
            if (id == _journeyRefreshId) JourneyStatus = "Couldn't reach the gateway: " + ex.Message;
        }
        finally { if (id == _journeyRefreshId) JourneyLoading = false; }
    }

    private void PaintJourney()
    {
        JourneyMonths.Clear();
        if (_journeyGraph is not { } g) { JourneySummary = ""; return; }
        JourneySummary = $"{g.Skills} skill{(g.Skills == 1 ? "" : "s")} and {g.Memories} memor{(g.Memories == 1 ? "y" : "ies")} this profile's agent has picked up.";
        foreach (var m in JourneyTimeline.Group(g.Nodes, _journeyFilter))
            JourneyMonths.Add(new JourneyMonthVM(m.Label, m.Nodes.Select(n => new JourneyNodeVM(n, OpenJourneyNodeCommand!))));
        if (g.Nodes.Count == 0) JourneyStatus = "Nothing learned yet. Skills the agent writes and memories it saves show up here.";
        else if (JourneyMonths.Count == 0) JourneyStatus = _journeyFilter == JourneyFilter.Used ? "No skill has been used yet." : "Nothing here.";
        else JourneyStatus = "";
    }

    /// <summary>Reads one node's own text on request (the skill's SKILL.md or the memory). A late answer for a node that is no longer open is dropped.</summary>
    public async Task OpenJourneyNodeAsync(JourneyNodeVM node)
    {
        _journeyOpen = node;
        var id = ++_journeyDetailId;
        JourneyDetailText = "";
        JourneyDetailNote = "Loading…";
        Raise(nameof(JourneyDetailVisibility)); Raise(nameof(JourneyListVisibility));
        Raise(nameof(JourneyDetailTitle)); Raise(nameof(JourneyDetailSubtitle));
        if (_journeyApi() is not { } api || _journeyProfile.Length == 0) { JourneyDetailNote = "Not connected."; return; }
        try
        {
            var d = await api.NodeAsync(_journeyProfile, node.Node.Id, CancellationToken.None).ConfigureAwait(true);
            if (id != _journeyDetailId) return;
            if (!d.Ok || string.IsNullOrWhiteSpace(d.Content)) { JourneyDetailNote = "Couldn't read this one."; return; }
            JourneyDetailNote = "";
            JourneyDetailText = d.Content;
        }
        catch (RestException ex) { if (id == _journeyDetailId) JourneyDetailNote = ex.Failure == RestFailure.NotFound ? "This one no longer exists." : "Couldn't read this one: " + ex.Message; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        {
            if (id == _journeyDetailId) JourneyDetailNote = "Couldn't reach the gateway: " + ex.Message;
        }
    }

    private void CloseJourneyNode()
    {
        _journeyOpen = null;
        _journeyDetailId++;                          // a read still in flight is dropped
        JourneyDetailText = ""; JourneyDetailNote = "";
        Raise(nameof(JourneyDetailVisibility)); Raise(nameof(JourneyListVisibility));
        Raise(nameof(JourneyDetailTitle)); Raise(nameof(JourneyDetailSubtitle));
    }
}
