using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>
/// "Running" filter for the gateway session list (BookahHermes AttentionFilter.Running + ActiveSessions): the live status
/// of each listed chat from <c>session.active_list</c>, polled only while the filter is on. The answer covers every profile
/// on the gateway, so it is only ever matched against rows already listed for the selected profile. It never pretends: while
/// unknown, unsupported or failing, every chat stays visible and a note says why. Read-only; sends nothing but the poll.
/// </summary>
/// <summary>The gateway session list: everything, only chats with a reply running, or only chats that need you (waiting on an answer, or a reply not yet read).</summary>
public enum AttentionFilter { All, Running, NeedsYou }

public sealed partial class MainViewModel
{
    private AttentionFilter _attention = AttentionFilter.All;
    private int _needsYouListed;
    private LiveSessionsPoller? _live;
    private IGatewayEventSource? _liveSource;
    private Dispatcher? _liveDispatcher;
    private bool _runningFilterOn;
    private string _liveNote = "";
    private int _runningCount;

    /// <summary>The session list as shown: all listed chats, or only the running ones while the filter has an answer.</summary>
    public ObservableCollection<SessionRowVM> VisibleSessionRows { get; } = new();

    public ICommand? ToggleRunningFilterCommand { get; private set; }

    /// <summary>Switches the list filter; the parameter is an <see cref="AttentionFilter"/> or its name.</summary>
    public ICommand? SetAttentionFilterCommand { get; private set; }

    public AttentionFilter AttentionMode => _attention;

    public TimeSpan LivePollInterval { get; set; } = LiveSessionsPoller.DefaultPoll;
    public TimeSpan LiveSettleDelay { get; set; } = LiveSessionsPoller.DefaultSettle;

    public bool RunningFilterOn => _runningFilterOn && _attention == AttentionFilter.Running;
    public string RunningFilterLabel => RunningFilterOn && _live?.Current.HasAnswer == true ? $"Running ({_runningCount})" : "Running";
    public string NeedsYouFilterLabel => _attention == AttentionFilter.NeedsYou ? $"Needs you ({_needsYouListed})" : "Needs you";
    public string LiveNote => _liveNote;
    public Visibility LiveNoteVisibility => _liveNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void InitRunningFilter()
    {
        ToggleRunningFilterCommand = new RelayCommand(_ => SetAttention(RunningFilterOn ? AttentionFilter.All : AttentionFilter.Running));
        SetAttentionFilterCommand = new RelayCommand(p =>
        {
            if (p is AttentionFilter f || Enum.TryParse(p?.ToString(), true, out f)) SetAttention(_attention == f && f != AttentionFilter.All ? AttentionFilter.All : f);
        });
        SessionRows.CollectionChanged += OnSessionRowsChanged;
    }

    private void OnSessionRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ApplyLive();

    private void SetAttention(AttentionFilter next)
    {
        if (next == AttentionFilter.All) { _attention = next; StopRunningFilter(); }
        else
        {
            _attention = next;
            if (!_runningFilterOn) StartRunningFilter(); else ApplyLive();
        }
        Raise(nameof(RunningFilterOn));
        Raise(nameof(AttentionMode));
        Raise(nameof(RunningFilterLabel));
        Raise(nameof(NeedsYouFilterLabel));
    }

    private void StartRunningFilter()
    {
        _runningFilterOn = true;
        _liveDispatcher = Dispatcher.CurrentDispatcher;
        _live ??= CreatePoller();
        ApplyLive();
        _live.Start();
    }

    private void StopRunningFilter()
    {
        _runningFilterOn = false;
        _live?.Stop();
        _live?.Clear();
        DetachLiveSource();
        ApplyLive();
    }

    private LiveSessionsPoller CreatePoller()
    {
        var poller = new LiveSessionsPoller(async ct =>
        {
            var peer = _rpc() ?? throw new InvalidOperationException("Not connected to the gateway.");
            AttachLiveSource(peer as IGatewayEventSource);
            return await peer.RequestAsync("session.active_list", null, null, ct).ConfigureAwait(false);
        }, LivePollInterval, LiveSettleDelay);
        poller.Changed += () => _liveDispatcher?.BeginInvoke(ApplyLive);
        return poller;
    }

    private void AttachLiveSource(IGatewayEventSource? source)
    {
        if (ReferenceEquals(source, _liveSource)) return;
        DetachLiveSource();
        _liveSource = source;
        if (source is not null) source.EventReceived += OnLiveEvent;
    }

    private void DetachLiveSource()
    {
        if (_liveSource is not null) _liveSource.EventReceived -= OnLiveEvent;
        _liveSource = null;
    }

    private void OnLiveEvent(GatewayEvent ev)
    {
        if (ev.Type is "sessions.changed" or "message.start" or "message.complete") _live?.Nudge();
    }

    /// <summary>Our own turns start and end here, which also changes what the list should say.</summary>
    private void NudgeLive() => _live?.Nudge();

    private void ApplyLive()
    {
        var snap = _runningFilterOn && _live is not null ? _live.Current : LiveSnapshot.Empty;
        var usable = _runningFilterOn && snap.HasAnswer && !snap.Unsupported;
        var needsMode = _runningFilterOn && _attention == AttentionFilter.NeedsYou;

        var running = 0;
        var needs = 0;
        foreach (var row in SessionRows)
        {
            var status = usable && snap.Rows.TryGetValue(row.Summary.Id, out var s) ? s.Status : LiveStatus.Idle;
            var label = status switch
            {
                LiveStatus.Working => "Running",
                LiveStatus.Waiting => "Waiting for you",
                _ => ""
            };
            // In the Needs-you view a chat with a reply not yet read says so in words (not only by being listed).
            if (needsMode && label.Length == 0 && row.IsUnread) label = "New reply";
            row.LiveLabel = label;
            if (status.IsRunning()) running++;
            if (status == LiveStatus.Waiting || row.IsUnread) needs++;
        }
        _runningCount = running;
        _needsYouListed = needs;

        var wantRunning = _attention == AttentionFilter.Running;
        _liveNote = !_runningFilterOn ? ""
            : snap.Unsupported ? (wantRunning ? "This gateway doesn't report running chats. Showing all chats."
                                              : "This gateway doesn't report which chats are waiting. Showing chats with an unread reply only.")
            : !snap.HasAnswer && snap.Error is not null ? (wantRunning ? $"Couldn't read running chats ({snap.Error}). Showing all chats."
                                                                       : $"Couldn't check which chats are waiting ({snap.Error}). Showing chats with an unread reply only.")
            : !snap.HasAnswer ? "Checking the gateway…"
            : snap.Error is not null ? "Showing the last known status (couldn't refresh)."
            : wantRunning && running == 0 ? "No listed chat is running."
            : needsMode && needs == 0 ? "Nothing needs you."
            : "";

        VisibleSessionRows.Clear();
        foreach (var row in SessionRows)
        {
            bool show;
            if (!_runningFilterOn) show = true;
            else if (wantRunning) show = !usable || row.LiveLabel.Length > 0;
            else show = row.LiveLabel == "Waiting for you" || row.IsUnread;   // Needs you (a running chat is not waiting); without a live answer, unread only
            if (show) VisibleSessionRows.Add(row);
        }

        Raise(nameof(RunningFilterLabel));
        Raise(nameof(NeedsYouFilterLabel));
        Raise(nameof(LiveNote));
        Raise(nameof(LiveNoteVisibility));
    }
}
