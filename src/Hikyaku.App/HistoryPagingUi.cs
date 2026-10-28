using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>
/// Earlier history on demand. A chat opens with its newest page; "Load earlier messages" reads the next older page for the
/// chat's own profile and puts it above, keeping the reader on the message they were at. Explicit (no surprise loading),
/// bounded (<see cref="MaxLoadedRows"/>), only between turns, and de-duplicated by stored row id.
/// </summary>
public sealed partial class MainViewModel
{
    public const int EarlierPageSize = 100;

    /// <summary>Stored rows kept loaded per chat; past this the rest stays on the gateway to keep the window responsive.</summary>
    public int MaxLoadedRows { get; set; } = 5000;

    public ICommand? LoadEarlierCommand { get; private set; }

    /// <summary>Raised after earlier messages were added above, with the message that was first before (to keep in view).</summary>
    public event Action<MessageVM>? EarlierLoaded;

    private bool _messagesNearTop = true;

    /// <summary>Reported by the message scroller: the reader is near the oldest loaded message, where "Load earlier" belongs.</summary>
    public bool MessagesNearTop
    {
        get => _messagesNearTop;
        set { if (_messagesNearTop == value) return; _messagesNearTop = value; Raise(); Raise(nameof(LoadEarlierVisibility)); }
    }

    public Visibility LoadEarlierVisibility =>
        SelectedConversation is { HasEarlier: true } && _messagesNearTop ? Visibility.Visible : Visibility.Collapsed;

    public string LoadEarlierLabel => SelectedConversation switch
    {
        { LoadingEarlier: true } => "Loading earlier messages…",
        { HasEarlier: true } c => $"Load earlier messages ({c.RawRowsLoaded} loaded)",
        _ => ""
    };

    private void InitHistoryPaging() =>
        LoadEarlierCommand = new RelayCommand(_ => _ = LoadEarlierAsync(), _ => CanLoadEarlier());

    private bool CanLoadEarlier() =>
        !_busy && SelectedConversation is { HasEarlier: true, LoadingEarlier: false, OfflineStale: false, StoredSessionId: { Length: > 0 } }
        && _sessions() is not null;

    private void RaiseEarlierState()
    {
        Raise(nameof(LoadEarlierVisibility));
        Raise(nameof(LoadEarlierLabel));
        (LoadEarlierCommand as RelayCommand)?.Refresh();
    }

    private async Task LoadEarlierAsync()
    {
        var convo = SelectedConversation;
        if (convo is null || !CanLoadEarlier() || _sessions() is not { } catalog) return;
        if (!_gate.Allow(Surface.Conversation, convo.Profile)) return;
        SetRewindNotice("");

        var offset = convo.RawRowsLoaded;
        convo.LoadingEarlier = true;
        RaiseEarlierState();
        SessionMessagesPage page;
        try
        {
            page = await catalog.LoadMessagesPageAsync(convo.Profile, convo.StoredSessionId!, offset, EarlierPageSize).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            convo.LoadingEarlier = false;
            RaiseEarlierState();
            if (SelectedConversation == convo) SetRewindNotice("Couldn't load earlier messages: " + ex.Message);
            return;
        }
        convo.LoadingEarlier = false;

        // The chat was reloaded or paged meanwhile: this answer is for rows that are no longer the next ones.
        if (convo.RawRowsLoaded != offset) { RaiseEarlierState(); return; }

        var known = convo.History.SelectMany(m => m.CoveredRowIds.Concat(m.RowId is { } r ? [r] : [])).ToHashSet();
        var fresh = page.Messages.Where(r => HistoryMapping.ParseRowId(r.Id) is not { } id || !known.Contains(id));
        var mapped = HistoryMapping.ToMessages(fresh);
        convo.RawRowsLoaded += page.Messages.Count;
        convo.HasEarlier = page.Messages.Count >= EarlierPageSize && convo.RawRowsLoaded < MaxLoadedRows;

        var anchor = convo.History.FirstOrDefault();
        convo.History.InsertRange(0, mapped);
        var visible = SelectedConversation == convo;
        if (visible)
        {
            for (var i = 0; i < mapped.Count; i++) Messages.Insert(i, mapped[i]);
            if (!convo.HasEarlier && page.Messages.Count >= EarlierPageSize)
                SetRewindNotice("This chat is very long: the earliest messages are not loaded here, to keep the window responsive.");
        }
        RaiseEarlierState();
        if (visible && anchor is not null && mapped.Count > 0)
        {
            IgnoreTailGrowthBriefly();   // older messages were added above: not new content below
            EarlierLoaded?.Invoke(anchor);
        }
    }
}
