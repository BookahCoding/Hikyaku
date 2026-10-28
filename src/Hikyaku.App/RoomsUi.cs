using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>A hosted room in the list.</summary>
public sealed class HostedRoomRowVM(HostedRoom room) : Observable
{
    public HostedRoom Room { get; private set; } = room;
    public string RoomId => Room.RoomId;
    public string Name => Room.Name;
    public string Summary => Room.Members.Count + (Room.Members.Count == 1 ? " member" : " members")
        + (Room.Members.Count > 0 ? " · " + string.Join(", ", Room.Members.Take(3).Select(m => m.Label)) + (Room.Members.Count > 3 ? "…" : "") : "");
    public void Update(HostedRoom room) { Room = room; Raise(nameof(Name)); Raise(nameof(Summary)); }
}

/// <summary>One line of a room's transcript, as drawn.</summary>
public sealed class RoomLineVM(RoomLine line)
{
    public RoomLine Line { get; } = line;
    public int Seq => Line.Seq;
    public bool IsSystem => Line is RoomLine.System;
    public bool FromUser => Line is RoomLine.Message { FromUser: true };
    public string Speaker => Line switch { RoomLine.Message { FromUser: true } => "You", RoomLine.Message m => m.Speaker ?? "A member", _ => "" };
    public string Text => Line switch { RoomLine.Message m => m.Text, RoomLine.System s => s.Text, _ => "" };
    public Visibility SpeakerVisibility => IsSystem ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>Something the room waits on the user for: an approval (allow once / deny only) or a failed turn to retry.</summary>
public sealed class RoomPendingVM : Observable
{
    private bool _busy;
    public RoomPendingVM(RoomPendingAction action, string who)
    {
        Action = action;
        Title = action.Kind == "approval"
            ? who + " is asking to run a command" + (string.IsNullOrWhiteSpace(action.ApprovalSummary) ? "." : ":")
            : who + "'s turn failed.";
        Detail = action.Kind == "approval" ? action.ApprovalSummary ?? "" : "";
    }
    public RoomPendingAction Action { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool IsApproval => Action.Kind == "approval";
    public bool IsRetry => Action.Kind == "retry";
    public Visibility ApprovalVisibility => IsApproval ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RetryVisibility => IsRetry ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailVisibility => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
}

/// <summary>
/// Hosted rooms (Rooms page): group chats the gateway itself runs, so they keep working with no client attached. Reads are
/// polled with a cursor while the page is open (rooms send no events), writes are idempotent by an id kept across a retry of
/// the same text, and a room appears only when every member's profile is one chosen in Hikyaku. Android (BookahHermes
/// <c>RoomsViewModel</c>) is the wire contract; Hikyaku never invents a call. Not here yet: creating rooms and files.
/// </summary>
public sealed partial class MainViewModel
{
    private const int RoomLogPageSize = 50;
    private const int RoomLogMaxPages = 40;

    private Func<HostedRoomsApi?> _roomsApi = () => null;
    private string _roomsGatewayId = "";
    private string _roomsStatus = "";
    private string _roomStatus = "";
    private string _roomComposer = "";
    private string _roomWaiting = "";
    private bool _roomsLoading, _roomSending;
    private int _roomsRefreshId, _roomOpenId;
    private HostedRoomRowVM? _selectedHostedRoom;
    private IReadOnlyList<RoomEvent> _roomEvents = [];
    private bool _roomsRefreshing;
    /// <summary>How many events of the open room are kept in memory (the newest); older lines scroll out of this app, not off the gateway.</summary>
    internal static int RoomEventsKept { get; set; } = 2000;
    private int _roomCursor;
    private IReadOnlyList<HostedRoomMember> _roomMembers = [];
    private (string RoomId, string Text, string EventId)? _roomSendAttempt;
    private MentionQuery? _roomMention;

    public ObservableCollection<HostedRoomRowVM> HostedRooms { get; } = new();
    public ObservableCollection<RoomLineVM> RoomTranscriptLines { get; } = new();
    public ObservableCollection<RoomPendingVM> RoomPending { get; } = new();
    public ObservableCollection<HostedRoomMember> RoomMentionChoices { get; } = new();

    public ICommand? ShowRoomsCommand { get; private set; }
    public ICommand? RefreshRoomsCommand { get; private set; }
    public ICommand? SendRoomMessageCommand { get; private set; }
    public ICommand? StopRoomCommand { get; private set; }
    public ICommand? RenameRoomCommand { get; private set; }
    public ICommand? DeleteRoomCommand { get; private set; }
    public ICommand? PickRoomMentionCommand { get; private set; }
    public ICommand? AllowRoomApprovalCommand { get; private set; }
    public ICommand? DenyRoomApprovalCommand { get; private set; }
    public ICommand? RetryRoomTurnCommand { get; private set; }

    /// <summary>Asked before deleting a room (title, message) → true only on an explicit yes. Unset means no.</summary>
    public Func<string, string, bool>? ConfirmRoom { get; set; }

    public void AttachRooms(Func<HostedRoomsApi?> api, string gatewayId) { _roomsApi = api; _roomsGatewayId = gatewayId; }

    public string RoomsStatus { get => _roomsStatus; private set { Set(ref _roomsStatus, value); Raise(nameof(RoomsStatusVisibility)); } }
    public Visibility RoomsStatusVisibility => _roomsStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string RoomStatus { get => _roomStatus; private set { Set(ref _roomStatus, value); Raise(nameof(RoomStatusVisibility)); } }
    public Visibility RoomStatusVisibility => _roomStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string RoomWaitingLine { get => _roomWaiting; private set { Set(ref _roomWaiting, value); Raise(nameof(RoomWaitingVisibility)); } }
    public Visibility RoomWaitingVisibility => _roomWaiting.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool RoomsLoading { get => _roomsLoading; private set { Set(ref _roomsLoading, value); (RefreshRoomsCommand as RelayCommand)?.Refresh(); } }
    public bool RoomSending { get => _roomSending; private set { Set(ref _roomSending, value); RefreshRoomCommands(); } }
    public Visibility RoomsVisibility =>
        ChatShellVisibility == Visibility.Visible && Nav == MainNav.Rooms ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RoomOpenVisibility => _selectedHostedRoom is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RoomPickVisibility => _selectedHostedRoom is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RoomMentionVisibility => RoomMentionChoices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RoomPendingVisibility => RoomPending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string RoomTitle => _selectedHostedRoom?.Name ?? "";
    public string RoomMembersLine => _roomMembers.Count == 0 ? "" : "Members: " + string.Join(", ", _roomMembers.Select(m => m.Label + (m.Handle is null ? "" : " (@" + m.Handle + ")")));

    public HostedRoomRowVM? SelectedHostedRoom
    {
        get => _selectedHostedRoom;
        set { if (ReferenceEquals(_selectedHostedRoom, value) || (value is null && _roomsRefreshing)) return; _ = OpenRoomAsync(value); }   // the list being rebuilt reports "nothing selected": that is not the person closing the room
    }

    /// <summary>The message being written. Typing an @ offers the room's members.</summary>
    public string RoomComposerText
    {
        get => _roomComposer;
        set { Set(ref _roomComposer, value); UpdateRoomMentions(null); RefreshRoomCommands(); }
    }

    /// <summary>The caret position the window reports while typing, so the picker follows the word being typed.</summary>
    public void RoomComposerCaretMoved(int caret) => UpdateRoomMentions(caret);

    private void InitRooms()
    {
        ShowRoomsCommand = new RelayCommand(_ => { Nav = MainNav.Rooms; _ = RefreshRoomsAsync(); });
        RefreshRoomsCommand = new RelayCommand(_ => _ = RefreshRoomsAsync(), _ => !_roomsLoading);
        SendRoomMessageCommand = new RelayCommand(_ => _ = SendRoomMessageAsync(), _ => CanSendRoomMessage());
        StopRoomCommand = new RelayCommand(_ => _ = StopRoomAsync(), _ => _selectedHostedRoom is not null);
        RenameRoomCommand = new RelayCommand(_ => _ = RenameRoomAsync(), _ => _selectedHostedRoom is not null);
        DeleteRoomCommand = new RelayCommand(_ => _ = DeleteRoomAsync(), _ => _selectedHostedRoom is not null);
        PickRoomMentionCommand = new RelayCommand(p => PickRoomMention(p as HostedRoomMember), p => p is HostedRoomMember);
        AllowRoomApprovalCommand = new RelayCommand(p => { if (p is RoomPendingVM v) _ = AnswerRoomApprovalAsync(v, "once"); }, p => p is RoomPendingVM { Busy: false });
        DenyRoomApprovalCommand = new RelayCommand(p => { if (p is RoomPendingVM v) _ = AnswerRoomApprovalAsync(v, "deny"); }, p => p is RoomPendingVM { Busy: false });
        RetryRoomTurnCommand = new RelayCommand(p => { if (p is RoomPendingVM v) _ = RetryRoomTurnAsync(v); }, p => p is RoomPendingVM { Busy: false });
    }

    private void RefreshRoomCommands()
    {
        (SendRoomMessageCommand as RelayCommand)?.Refresh();
        (StopRoomCommand as RelayCommand)?.Refresh();
        (RenameRoomCommand as RelayCommand)?.Refresh();
        (DeleteRoomCommand as RelayCommand)?.Refresh();
    }

    private bool CanSendRoomMessage() =>
        _selectedHostedRoom is not null && !_roomSending && _roomComposer.Trim().Length > 0;

    /// <summary>A room is shown only when every member names a profile that is chosen here: a member we cannot place is not guessed at.</summary>
    private bool RoomAllowed(HostedRoom room) =>
        room.Members.Count > 0 && room.Members.All(m => !string.IsNullOrWhiteSpace(m.Profile)
            && _gate.Allow(Surface.Rooms, new ProfileKey(_roomsGatewayId, m.Profile!)));

    private static string RoomMessage(RoomException ex) => ex.Kind switch
    {
        RoomFailureKind.NotConnected => "Connect to a gateway to use rooms.",
        RoomFailureKind.Unsupported => "This gateway does not host rooms.",
        _ => ex.Message
    };

    public async Task RefreshRoomsAsync()
    {
        if (_roomsApi() is not { } api) { RoomsStatus = "Connect to a gateway to use its rooms."; return; }
        var id = ++_roomsRefreshId;
        RoomsLoading = true;
        try
        {
            var caps = await api.CapabilitiesAsync().ConfigureAwait(true);
            if (id != _roomsRefreshId) return;
            if (!caps.Driver) { HostedRooms.Clear(); RoomsStatus = "This gateway does not run rooms."; return; }
            var all = await api.ListAsync().ConfigureAwait(true);
            if (id != _roomsRefreshId) return;
            var shown = all.Where(RoomAllowed).ToList();
            var hidden = all.Count - shown.Count;
            var keep = _selectedHostedRoom?.RoomId;
            _roomsRefreshing = true;
            try
            {
                HostedRooms.Clear();
                foreach (var r in shown) HostedRooms.Add(new HostedRoomRowVM(r));
            }
            finally { _roomsRefreshing = false; }
            RoomsStatus = shown.Count == 0
                ? (hidden > 0 ? $"{hidden} room(s) include profiles not chosen in Hikyaku and are hidden." : "No rooms yet. Create one from Hermes Desktop or the Android app.")
                : (hidden > 0 ? $"{hidden} room(s) include profiles not chosen in Hikyaku and are hidden." : "");
            if (keep is not null && HostedRooms.FirstOrDefault(r => r.RoomId == keep) is { } same) { _selectedHostedRoom = same; Raise(nameof(SelectedHostedRoom)); }
            else if (keep is not null) await OpenRoomAsync(null).ConfigureAwait(true);   // the open room is gone or no longer allowed
        }
        catch (RoomException ex) { if (id == _roomsRefreshId) RoomsStatus = RoomMessage(ex); }
        finally { if (id == _roomsRefreshId) RoomsLoading = false; }
    }

    /// <summary>The open room, but only while every member's profile is still chosen: deselecting a profile closes the room instead of leaving it read and writable.</summary>
    private HostedRoomRowVM? OpenAllowedRoom()
    {
        if (!Shows(ExperienceFeature.Rooms) || _selectedHostedRoom is not { } row) return null;   // Simple view never reaches rooms, whoever asks
        if (RoomAllowed(row.Room)) return row;
        HostedRooms.Remove(row);
        _ = OpenRoomAsync(null);
        RoomsStatus = "That room was closed because one of its profiles is no longer chosen.";
        return null;
    }

    /// <summary>Called when the chosen profiles change.</summary>
    internal void CheckOpenRoomStillAllowed() => _ = OpenAllowedRoom();

    private static IReadOnlyList<RoomEvent> Cap(IReadOnlyList<RoomEvent> events) =>
        events.Count <= RoomEventsKept ? events : events.Skip(events.Count - RoomEventsKept).ToList();

    private void ClearOpenRoom()
    {
        _roomEvents = []; _roomCursor = 0; _roomMembers = [];
        RoomTranscriptLines.Clear(); RoomPending.Clear(); RoomMentionChoices.Clear();
        RoomWaitingLine = ""; RoomStatus = ""; _roomMention = null;
        Raise(nameof(RoomMentionVisibility)); Raise(nameof(RoomPendingVisibility)); Raise(nameof(RoomMembersLine));
    }

    /// <summary>Opens a room (or none): a switch discards the other room's lines and any half-typed @ choices.</summary>
    public async Task OpenRoomAsync(HostedRoomRowVM? row)
    {
        var id = ++_roomOpenId;
        _selectedHostedRoom = row;
        ClearOpenRoom();
        Raise(nameof(SelectedHostedRoom)); Raise(nameof(RoomTitle));
        Raise(nameof(RoomOpenVisibility)); Raise(nameof(RoomPickVisibility));
        RefreshRoomCommands();
        if (row is null || _roomsApi() is not { } api) return;
        _roomMembers = row.Room.Members;
        Raise(nameof(RoomMembersLine));
        RoomStatus = "Loading…";
        try
        {
            IReadOnlyList<RoomEvent> events = [];
            var cursor = 0;
            for (var page = 0; page < RoomLogMaxPages; page++)
            {
                var p = await api.LogAsync(row.RoomId, cursor, RoomLogPageSize).ConfigureAwait(true);
                if (id != _roomOpenId) return;
                events = RoomTranscript.Merge(events, p.Events);
                if (p.Cursor <= cursor && p.Events.Count == 0) break;
                cursor = Math.Max(cursor, p.Cursor);
                if (!p.HasMore) break;
            }
            _roomEvents = Cap(events); _roomCursor = cursor;
            await RefreshRoomStateAsync(api, row.RoomId, id).ConfigureAwait(true);
            if (id != _roomOpenId) return;
            RoomStatus = "";
            PaintRoom();
        }
        catch (RoomException ex) { if (id == _roomOpenId) RoomStatus = RoomMessage(ex); }
    }

    /// <summary>Reads what is new in the open room; the window calls this every few seconds while the page is shown. Never throws.</summary>
    public async Task PollRoomAsync()
    {
        if (OpenAllowedRoom() is not { } row || _roomsApi() is not { } api) return;
        var id = _roomOpenId;
        try
        {
            var p = await api.LogAsync(row.RoomId, _roomCursor, RoomLogPageSize).ConfigureAwait(true);
            if (id != _roomOpenId) return;                                  // another room was opened meanwhile
            _roomEvents = Cap(RoomTranscript.Merge(_roomEvents, p.Events));
            _roomCursor = Math.Max(_roomCursor, p.Cursor);
            await RefreshRoomStateAsync(api, row.RoomId, id).ConfigureAwait(true);
            if (id != _roomOpenId) return;
            if (RoomStatus.StartsWith("Couldn't refresh the room", StringComparison.Ordinal)) RoomStatus = "";   // only its own message, never an action's
            PaintRoom();
        }
        catch (RoomException ex) { if (id == _roomOpenId) RoomStatus = "Couldn't refresh the room: " + RoomMessage(ex); }
    }

    private async Task RefreshRoomStateAsync(HostedRoomsApi api, string roomId, int openId)
    {
        try
        {
            var state = await api.StateAsync(roomId).ConfigureAwait(true);
            if (openId != _roomOpenId) return;
            _roomMembers = state.Room.Members;
            _selectedHostedRoom?.Update(state.Room);
            RoomPending.Clear();
            foreach (var a in state.Driver?.PendingActions ?? [])
                if (a.Kind is "approval" or "retry")
                    RoomPending.Add(new RoomPendingVM(a, state.Room.Member(a.MemberId)?.Label ?? "A member"));
        }
        catch (RoomException)
        {
            // The log is the truth; a failed status read only hides the approval buttons until the next poll.
        }
    }

    private void PaintRoom()
    {
        var lines = RoomTranscript.Lines(_roomEvents, _roomMembers);
        var have = RoomTranscriptLines.Count;
        if (lines.Count >= have && RoomTranscriptLines.Select(l => l.Seq).SequenceEqual(lines.Take(have).Select(l => l.Seq)))
            foreach (var l in lines.Skip(have)) RoomTranscriptLines.Add(new RoomLineVM(l));
        else
        {
            RoomTranscriptLines.Clear();
            foreach (var l in lines) RoomTranscriptLines.Add(new RoomLineVM(l));
        }
        RoomWaitingLine = RoomTranscript.WaitingLine(RoomTranscript.WaitingFor(_roomEvents, _roomMembers)) ?? "";
        Raise(nameof(RoomTitle)); Raise(nameof(RoomMembersLine)); Raise(nameof(RoomPendingVisibility));
    }

    private void UpdateRoomMentions(int? caret)
    {
        var q = RoomMentions.QueryAt(_roomComposer, caret ?? _roomComposer.Length);
        _roomMention = q;
        RoomMentionChoices.Clear();
        if (q is not null)
        {
            foreach (var m in RoomMentions.Mentionable(_roomMembers, q.Query)) RoomMentionChoices.Add(m);
            if (RoomMentions.MentionsEveryone(q.Query))
                RoomMentionChoices.Add(new HostedRoomMember(null, null, "all", "Everyone in the room"));
        }
        Raise(nameof(RoomMentionVisibility));
    }

    private void PickRoomMention(HostedRoomMember? member)
    {
        if (member?.Handle is not { } handle || _roomMention is not { } q) return;
        var (text, _) = RoomMentions.Apply(_roomComposer, q, handle);
        _roomComposer = text;
        Raise(nameof(RoomComposerText));
        RoomMentionChoices.Clear();
        _roomMention = null;
        Raise(nameof(RoomMentionVisibility));
        RefreshRoomCommands();
    }

    /// <summary>
    /// Sends the written message. The same text to the same room reuses one idempotency id, so pressing Send again after a lost
    /// answer cannot post it twice; changed text is a new message. A failure keeps the text and sends nothing on its own.
    /// </summary>
    public async Task SendRoomMessageAsync()
    {
        if (!CanSendRoomMessage() || _roomsApi() is not { } api || OpenAllowedRoom() is not { } row) return;
        var text = _roomComposer.Trim();
        var attempt = _roomSendAttempt is { } a && a.RoomId == row.RoomId && a.Text == text
            ? a : (RoomId: row.RoomId, Text: text, EventId: "hikyaku-" + Guid.NewGuid().ToString("N"));
        _roomSendAttempt = attempt;
        var openId = _roomOpenId;
        RoomSending = true;
        RoomStatus = "";
        try
        {
            var ev = await api.SendAsync(row.RoomId, text, attempt.EventId).ConfigureAwait(true);
            _roomSendAttempt = null;
            if (openId == _roomOpenId)
            {
                _roomEvents = Cap(RoomTranscript.Merge(_roomEvents, [ev]));
                _roomComposer = ""; Raise(nameof(RoomComposerText));
                RoomMentionChoices.Clear(); Raise(nameof(RoomMentionVisibility));
                PaintRoom();
            }
        }
        catch (RoomException ex)
        {
            if (openId == _roomOpenId)
                RoomStatus = "Not sent: " + RoomMessage(ex) + " Your message is still in the box; pressing Send again will not post it twice.";
        }
        finally { RoomSending = false; }
    }

    private async Task StopRoomAsync()
    {
        if (OpenAllowedRoom() is not { } row || _roomsApi() is not { } api) return;
        if (ConfirmRoom?.Invoke("Stop room", $"Stop all work in “{row.Name}”?\n\nEvery member currently replying in this room stops, for everyone who uses it.") != true) return;
        try
        {
            await api.StopAsync(row.RoomId, "hikyaku-stop-" + Guid.NewGuid().ToString("N")).ConfigureAwait(true);
            RoomStatus = "Asked the room to stop.";
            await PollRoomAsync().ConfigureAwait(true);
        }
        catch (RoomException ex) { RoomStatus = "Couldn't stop the room: " + RoomMessage(ex); }
    }

    private async Task RenameRoomAsync()
    {
        if (OpenAllowedRoom() is not { } row || _roomsApi() is not { } api) return;
        var next = PromptText?.Invoke("Rename room", row.Name)?.Trim();
        if (string.IsNullOrEmpty(next) || next == row.Name) return;
        try
        {
            var room = await api.RenameAsync(row.RoomId, next, "hikyaku-rename-" + Guid.NewGuid().ToString("N")).ConfigureAwait(true);
            row.Update(room);
            Raise(nameof(RoomTitle));
            RoomStatus = "";
        }
        catch (RoomException ex) { RoomStatus = "Couldn't rename the room: " + RoomMessage(ex); }
    }

    /// <summary>Deleting a room removes it for every client and cannot be undone: confirmed in words, and nothing happens without a yes.</summary>
    private async Task DeleteRoomAsync()
    {
        if (OpenAllowedRoom() is not { } row || _roomsApi() is not { } api) return;
        if (ConfirmRoom?.Invoke("Delete room",
                $"Delete the room “{row.Name}”?\n\nIt is removed for everyone, on every device and client, its running work is stopped, and it cannot be undone.") != true) return;
        try
        {
            await api.DisbandAsync(row.RoomId, "hikyaku-disband-" + Guid.NewGuid().ToString("N")).ConfigureAwait(true);
            HostedRooms.Remove(row);
            await OpenRoomAsync(null).ConfigureAwait(true);
            RoomsStatus = "Room deleted.";
        }
        catch (RoomException ex) { RoomStatus = "Couldn't delete the room: " + RoomMessage(ex); }
    }

    /// <summary>Allow once or deny a member's command. There is no "always" from here, and a stale answer is refused by the gateway.</summary>
    public async Task AnswerRoomApprovalAsync(RoomPendingVM item, string choice)
    {
        if (OpenAllowedRoom() is not { } row || _roomsApi() is not { } api || item.Busy) return;
        item.Busy = true;
        try
        {
            await api.ApproveAsync(row.RoomId, item.Action, choice, "hikyaku-approve-" + Guid.NewGuid().ToString("N")).ConfigureAwait(true);
            RoomStatus = choice == "once" ? "Allowed once." : "Denied.";
            await PollRoomAsync().ConfigureAwait(true);
        }
        catch (RoomException ex) { RoomStatus = "Couldn't answer: " + RoomMessage(ex); await PollRoomAsync().ConfigureAwait(true); }
        finally { item.Busy = false; }
    }

    private async Task RetryRoomTurnAsync(RoomPendingVM item)
    {
        if (OpenAllowedRoom() is not { } row || _roomsApi() is not { } api || item.Action.TaskId is not { } task || item.Busy) return;
        item.Busy = true;
        try
        {
            await api.RetryAsync(row.RoomId, task).ConfigureAwait(true);
            RoomStatus = "Asked for another try.";
            await PollRoomAsync().ConfigureAwait(true);
        }
        catch (RoomException ex) { RoomStatus = "Couldn't retry: " + RoomMessage(ex); }
        finally { item.Busy = false; }
    }
}
