using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;
using Microsoft.Win32;

namespace Hikyaku.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action<object?> run, Func<object?, bool>? can = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? p) => can?.Invoke(p) ?? true;
    public void Execute(object? p) => run(p);
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed partial class MessageVM(string role, string text = "") : Observable
{
    private string _text = text;
    private string _activity = "";
    private bool _useMarkdown = true;
    public string Role { get; } = role;
    public string Text { get => _text; private set { Set(ref _text, value); Raise(nameof(Markdown)); Raise(nameof(ReadAloudVisibility)); Raise(nameof(ActivityLineVisibility)); } }
    public string Markdown => _text;
    public bool UseMarkdown { get => _useMarkdown; set => Set(ref _useMarkdown, value); }
    public string Activity { get => _activity; set { Set(ref _activity, value); Raise(nameof(ActivityVisibility)); } }
    private string _toolSummary = "";
    /// <summary>The raw "tool: preview" line of the step the agent is running; shown in Advanced only. Status and errors use <see cref="Activity"/>.</summary>
    public string ToolSummary { get => _toolSummary; set => Set(ref _toolSummary, value); }
    public ObservableCollection<AttachmentCardVM> Attachments { get; } = new();
    /// <summary>The tool calls of a stored reply (arguments and results made readable); shown as collapsed disclosures in Advanced.</summary>
    public ObservableCollection<ToolStepVM> Tools { get; } = new();
    /// <summary>Every stored row this bubble stands for (a merged reply covers several); used to avoid showing a row twice when paging.</summary>
    public List<long> CoveredRowIds { get; } = new();
    private long? _rowId;
    /// <summary>The gateway's stored row of this user prompt; required to edit or regenerate it.</summary>
    public long? RowId { get => _rowId; set { Set(ref _rowId, value); Raise(nameof(IsRewindable)); Raise(nameof(RewindVisibility)); Raise(nameof(ActionsVisibility)); } }
    /// <summary>What actually went out when it differs from what is shown (e.g. a skill's full body).</summary>
    public string? SentText { get; set; }
    public bool IsRewindable => Role == "You" && _rowId is not null;
    /// <summary>Any stored row can be branched from; only user prompts can be edited or regenerated.</summary>
    public Visibility ActionsVisibility => _rowId is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RewindVisibility => IsRewindable ? Visibility.Visible : Visibility.Collapsed;
    private bool _findMatch, _findCurrent;
    /// <summary>Marked by Find in chat: contains the query / is the match currently selected.</summary>
    public bool IsFindMatch { get => _findMatch; set => Set(ref _findMatch, value); }
    public bool IsFindCurrent { get => _findCurrent; set => Set(ref _findCurrent, value); }
    private bool _queued, _sideCard, _failed;
    /// <summary>A prompt held in this app until the running reply finishes (never sent automatically after a stop or error).</summary>
    public bool IsQueued { get => _queued; set { Set(ref _queued, value); Raise(nameof(UnqueueVisibility)); } }
    public Visibility UnqueueVisibility => _queued ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>A /btw or /bg answer card: local to this app, never part of the prompt history sent to the gateway.</summary>
    public bool IsSideCard { get => _sideCard; set { Set(ref _sideCard, value); Raise(nameof(ReadAloudVisibility)); } }
    public bool IsFailed { get => _failed; set => Set(ref _failed, value); }

    private TurnFailureCard? _failure;
    /// <summary>Set when this reply failed: a plain-words card (what happened, what is known about delivery, whether the prompt may be put back).</summary>
    public TurnFailureCard? Failure
    {
        get => _failure;
        set
        {
            Set(ref _failure, value);
            Raise(nameof(FailureVisibility));
            Raise(nameof(ActivityVisibility));
            Raise(nameof(RestoreVisibility));
            Raise(nameof(ReadAloudVisibility));
        }
    }
    /// <summary>The text of the message that failed, kept for "Put my message back in the box".</summary>
    public string FailedPromptText { get => _failedPrompt; set { _failedPrompt = value; Raise(nameof(RestoreVisibility)); } }
    private string _failedPrompt = "";
    public Visibility FailureVisibility => _failure is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RestoreVisibility => _failure is { CanRestorePrompt: true } && FailedPromptText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>The raw activity line is replaced by the card when there is one.</summary>
    public Visibility ActivityVisibility => _failure is null && Activity.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Read aloud is offered on assistant replies that have text (never on side cards or failures).</summary>
    public Visibility ReadAloudVisibility => Role == "Assistant" && !_sideCard && _failure is null && _text.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    private bool _reading;
    /// <summary>This reply is being read aloud (or its audio is being fetched).</summary>
    public bool IsReading { get => _reading; set { Set(ref _reading, value); Raise(nameof(ReadAloudLabel)); Raise(nameof(ReadAloudGlyph)); } }
    public string ReadAloudLabel => _reading ? "Stop reading" : "Read aloud";
    /// <summary>Speaker icon, or a stop square while this reply is being read.</summary>
    public string ReadAloudGlyph => _reading ? "\uE71A" : "\uE767";
    /// <summary>Your own messages show as a bubble on the right (Claude-style); replies are plain text across the column.</summary>
    public bool IsMine => Role == "You";
    /// <summary>Your messages are plain text in a bubble that fits them; replies render as formatted text (markdown, maths).</summary>
    public Visibility MarkdownVisibility => IsMine ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PlainTextVisibility => IsMine ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>The speaker's name is shown only for other speakers (side tasks, bots); "You" and "Assistant" are clear from the layout.</summary>
    public Visibility RoleLabelVisibility => Role is "You" or "Assistant" ? Visibility.Collapsed : Visibility.Visible;
    public void Append(string s) => Text = _text + s;
    public void ReplaceText(string s) => Text = s;
}

/// <summary>One tool call of a reply: collapsed by default, input and result selectable, a failure marked in words.</summary>
public sealed class ToolStepVM(string name, string? input, string? output, bool failed, string? id = null, bool running = false) : Observable
{
    private bool _expanded, _failed = failed, _running = running;
    private string? _input = input, _output = output, _meta, _risk;
    /// <summary>The gateway's id for a live call (<c>tool_id</c>), matching its start to its end.</summary>
    public string? Id { get; } = id;
    public string Name { get; } = name;
    public string? Input { get => _input; set { Set(ref _input, value); Raise(nameof(HasInput)); } }
    public string? Output { get => _output; set { Set(ref _output, value); Raise(nameof(HasOutput)); } }
    public bool Failed { get => _failed; set { Set(ref _failed, value); Raise(nameof(Header)); Raise(nameof(StatusGlyph)); } }
    /// <summary>Still running (a live step until its <c>tool.complete</c>, or until the turn ends).</summary>
    public bool Running { get => _running; set { Set(ref _running, value); Raise(nameof(Header)); Raise(nameof(StatusGlyph)); } }
    public bool HasInput => !string.IsNullOrEmpty(Input);
    public bool HasOutput => !string.IsNullOrEmpty(Output);
    public string Header => Failed ? Name + " (reported an error)" : Running ? Name + " (running…)" : Name;
    /// <summary>✓, ✗ or a clock while it runs (Segoe Fluent glyphs).</summary>
    public string StatusGlyph => Running ? "\uE916" : Failed ? "\uE711" : "\uE73E";
    /// <summary>What it was doing and how long it took ("README.md · 0.4 s").</summary>
    public string? Meta { get => _meta; set { Set(ref _meta, value); Raise(nameof(MetaVisibility)); } }
    public Visibility MetaVisibility => string.IsNullOrEmpty(_meta) ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>The gateway flagged this tool's output as high risk (shown in words on the step).</summary>
    public string? Risk { get => _risk; set { Set(ref _risk, value); Raise(nameof(RiskVisibility)); } }
    public Visibility RiskVisibility => string.IsNullOrEmpty(_risk) ? Visibility.Collapsed : Visibility.Visible;
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
}

public sealed class AttachmentCardVM(string name, AttachmentKind kind, string? gatewayPath = null, byte[]? localPreview = null) : Observable
{
    public string Name { get; } = name;
    public AttachmentKind Kind { get; } = kind;
    public string? GatewayPath { get; } = gatewayPath;
    public byte[]? LocalPreview { get; } = localPreview;
    public string Label => Kind + " · " + Name;
    public bool HasPath => !string.IsNullOrEmpty(GatewayPath);
}

public sealed class ConversationVM(ProfileKey profile, string title) : Observable
{
    private string _title = title;
    private string _modelLabel = "Model: default";
    private string _reasoningLabel = "Reasoning";
    private string _fastLabel = "Normal";
    private PromptDeliveryState _delivery = PromptDeliveryState.Settled;
    public ProfileKey Profile { get; } = profile;
    public string Title { get => _title; set => Set(ref _title, value); }
    /// <summary>Runtime session id for prompt.submit.</summary>
    public string? SessionId { get; set; }
    /// <summary>Stored session id from REST / session.create.</summary>
    public string? StoredSessionId { get; set; }
    /// <summary>True when this conversation is the profile's permanent Bot Chat (title identity).</summary>
    public bool IsBotChat { get; set; }
    public SubagentMonitor? Subagents { get; set; }
    public bool OfflineStale { get; set; }
    public List<MessageVM> History { get; } = new();
    /// <summary>Stored rows read so far (newest first), including steps that are not shown; the offset of the next older page.</summary>
    public int RawRowsLoaded { get; set; }
    /// <summary>Older rows exist on the gateway than are loaded.</summary>
    public bool HasEarlier { get; set; }
    public bool LoadingEarlier { get; set; }
    /// <summary>Prompts queued while a reply runs (app-side), in send order.</summary>
    public List<QueuedPrompt> Queue { get; } = new();
    /// <summary>Queued text that could not be sent because the reply ended while the user was in another chat.</summary>
    public List<string> UnsentFromQueue { get; } = new();
    /// <summary>Pending picks before create / after catalog load. Live RPC uses <see cref="BindModelControls"/>.</summary>
    public string? PendingModel { get; set; }
    public string? PendingProvider { get; set; }
    public string? PendingReasoning { get; set; }
    public bool? PendingFast { get; set; }
    /// <summary>This chat's YOLO (approvals skipped) as last confirmed by the gateway; null until set from this app. Per chat, never shared.</summary>
    public bool? YoloEnabled { get; set; }
    public SessionModelControls? LiveModelControls { get; set; }
    public string ModelLabel { get => _modelLabel; set => Set(ref _modelLabel, value); }
    public string ReasoningLabel { get => _reasoningLabel; set => Set(ref _reasoningLabel, value); }
    public string FastLabel { get => _fastLabel; set => Set(ref _fastLabel, value); }
    public PromptDeliveryState Delivery
    {
        get => _delivery;
        set { Set(ref _delivery, value); Raise(nameof(ShowResendHint)); }
    }
    public bool ShowResendHint => PromptResendPolicy.UserMayResend(Delivery);
}

public sealed class SessionRowVM : Observable
{
    /// <summary>The profile this row was listed for: an action on it is never sent under another profile.</summary>
    public ProfileKey? LoadedFor { get; set; }
    public SessionRowVM(SessionSummary summary, bool archivedView = false)
    {
        Summary = summary;
        _unread = summary.Unread;
        _pinned = summary.Pinned;
        ArchivedView = archivedView;
    }
    public SessionSummary Summary { get; }
    private bool _unread;
    private bool _pinned;
    /// <summary>The row is in the Archived view, so its archive action is "Unarchive".</summary>
    public bool ArchivedView { get; }
    /// <summary>The gateway's shared read state, updated here when this app marks the chat read or unread.</summary>
    public bool IsUnread { get => _unread; set { Set(ref _unread, value); Raise(nameof(PinMark)); } }
    public bool IsPinned { get => _pinned; set { Set(ref _pinned, value); Raise(nameof(Title)); Raise(nameof(PinMark)); } }
    public string PinMark => _pinned ? "Pinned" : "";
    private string _liveLabel = "";
    /// <summary>"Running" / "Waiting for you" from the gateway's live status; empty when unknown or idle. Text, not only colour.</summary>
    public string LiveLabel { get => _liveLabel; set { Set(ref _liveLabel, value); Raise(nameof(LiveVisibility)); } }
    public Visibility LiveVisibility => _liveLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Title { get => _title ?? (string.IsNullOrWhiteSpace(Summary.Title) ? Summary.Id : Summary.Title!); set { _title = value; Raise(); } }
    private string? _title;
    public string Preview => Summary.Snippet ?? Summary.Preview ?? "";
}

public enum MainNav { Chats, Bots, Inbox, NeedsYou, Projects, Scheduled, Capabilities, Insights, Rooms, Artifacts, Journey }

public sealed class BotRowVM : Observable
{
    public BotRowVM(Bot bot, string? preview, bool unread, string activity)
    {
        Bot = bot;
        Preview = preview ?? "";
        Unread = unread;
        Activity = activity;
    }
    public Bot Bot { get; }
    public string Name => Bot.Label;
    public string Handle => "@" + Bot.Handle;
    public string Preview { get; }
    public bool Unread { get; }
    public string Activity { get; }
    public string Meta => Activity + (Unread ? " · unread" : "");
    public bool IsBotChat => Bot.CanonicalSession?.Title == BotsApi.BotChatTitle
        || string.Equals(Bot.CanonicalSession?.Title, BotsApi.BotChatTitle, StringComparison.Ordinal);
}

public sealed class RoomRowVM : Observable
{
    public RoomRowVM(DesktopRoom room)
    {
        Room = room;
        Key = room.Key;
        DisplayName = room.Name;
        IsRestricted = false;
        Explanation = "";
        Preview = room.LinesList.LastOrDefault()?.Text ?? (room.MembersList.Count + " members · Continue on Desktop");
    }
    public RoomRowVM(RestrictedRoomView restricted)
    {
        Room = null;
        Key = restricted.Key;
        DisplayName = restricted.Name;
        IsRestricted = true;
        Explanation = restricted.Explanation;
        Preview = restricted.Explanation;
    }
    public DesktopRoom? Room { get; }
    public string Key { get; }
    public string DisplayName { get; }
    public string Name => DisplayName;
    public bool IsRestricted { get; }
    public string Explanation { get; }
    public string Preview { get; }
    public bool NeedsYou => Room?.NeedsYou == true;
    public string Badge => NeedsYou ? "Needs you" : (IsRestricted ? "Restricted" : "");
}

public sealed class SubagentRowVM : Observable
{
    public SubagentRowVM(SubagentRow row) => Row = row;
    public SubagentRow Row { get; }
    public string Goal => Row.Goal;
    public string Status => Row.StatusLabel;
    public string Elapsed => Row.ElapsedLabel;
    public string Detail => Row.Thinking ?? Row.Summary ?? string.Join(" · ", Row.ActivityList.TakeLast(2));
    public bool CanStop => Row.StoppableId is not null;
}

public sealed class InboxRunVM : Observable
{
    public InboxRunVM(InboxRun run, bool unread)
    {
        Run = run;
        Unread = unread;
    }
    public InboxRun Run { get; }
    public bool Unread { get; private set; }
    public string Title => Run.Title;
    public string Meta => $"{Run.Bot} · {Run.Job} · {Run.Status}" + (Unread ? " · unread" : "");
    public string Preview => Run.Text.Length <= 280 ? Run.Text : Run.Text[..280] + "…";
    public bool Failed => Run.Failed;
    public void MarkReadLocal() { Unread = false; Raise(nameof(Unread)); Raise(nameof(Meta)); }
}

public sealed class NeedsYouItemVM : Observable
{
    public NeedsYouItemVM(NeedsYouRequest request) => Request = request;
    public NeedsYouRequest Request { get; }
    public string Title => Request switch
    {
        ApprovalRequest a => "Approval" + (string.IsNullOrEmpty(a.ToolName) ? "" : $" · {a.ToolName}"),
        ClarifyRequest => "Question",
        SecretRequest s => s.Kind,
        VaultSaveLoginRequest v => v.Site.Length == 0 ? "Save login?" : $"Save {v.Site} login?",
        _ => "Needs you"
    };
    public string Body => Request switch
    {
        ApprovalRequest a => string.IsNullOrEmpty(a.Description) ? a.Command : a.Description + "\n" + a.Command,
        ClarifyRequest c => string.Join("\n", c.Questions.Select(q => q.Question)),
        SecretRequest s => s.Prompt + (s.Profile is null ? "\n(pending profile attribution)" : ""),
        VaultSaveLoginRequest v => v.Origin,
        _ => ""
    };
    public bool IsApproval => Request is ApprovalRequest;
    public bool IsClarify => Request is ClarifyRequest;
    public bool IsSecret => Request is SecretRequest;
}

public sealed partial class MainViewModel : Observable
{
    private readonly ProfileSelectionService _selection;
    private readonly NotificationGate _gate;
    private readonly Func<IGatewayTransport?> _transport;
    private readonly Func<InboxService?> _inbox;
    private readonly Func<NeedsYouService?> _needsYou;
    private readonly Func<SessionCatalog?> _sessions;
    private readonly Func<DraftStore?> _drafts;
    private readonly Func<OfflineChatCache?> _offline;
    private readonly Func<AttachmentUploader?> _uploader;
    private readonly Func<MediaApi?> _media;
    private readonly Func<InboxGroupsStore?> _groupsStore;
    private readonly Func<BotRosterService?> _bots;
    private readonly Func<IJsonRpcPeer?> _rpc;
    private readonly AttachmentQueue _attachQueue = new();
    private readonly List<ConversationVM> _all = new();
    private CancellationTokenSource? _cts;
    private ConversationVM? _selected;
    private string _input = "";
    private bool _busy;
    private MainNav _nav = MainNav.Chats;
    private string _botsStatus = "";
    private string _steerHint = "";
    private string _subagentSteerText = "";
    private bool _botsLoading;
    private bool _failuresOnly;
    private bool _inboxLoading;
    private string _inboxStatus = "";
    private string _clarifyAnswer = "";
    private string _secretValue = "";
    private string _sessionSearch = "";
    private string? _inboxJobFilter;
    private string? _inboxGroupFilter;
    private InboxGroups _groups = InboxGroups.Empty;
    private string _followJobId = "";
    private FollowMode _followMode = FollowMode.Quiet;
    private bool _followFailures;
    private bool _offlineBanner;
    private readonly Func<string> _identityStrip;

    public MainViewModel(ProfileSelectionService selection, NotificationGate gate, Func<IGatewayTransport?> transport,
        Func<string> statusBanner, Func<InboxService?> inbox, Func<NeedsYouService?> needsYou,
        Func<SessionCatalog?> sessions, Func<DraftStore?> drafts, Func<OfflineChatCache?> offline,
        Func<AttachmentUploader?> uploader, Func<MediaApi?> media, Func<InboxGroupsStore?> groupsStore,
        Func<string>? identityStrip = null,
        Func<BotRosterService?>? bots = null,
        Func<IJsonRpcPeer?>? rpc = null)
    {
        _selection = selection;
        _gate = gate;
        _transport = transport;
        _inbox = inbox;
        _needsYou = needsYou;
        _sessions = sessions;
        _drafts = drafts;
        _offline = offline;
        _uploader = uploader;
        _media = media;
        _groupsStore = groupsStore;
        _bots = bots ?? (() => null);
        _rpc = rpc ?? (() => null);
        _status = statusBanner;
        _identityStrip = identityStrip ?? (() => "");
        SendCommand = new RelayCommand(_ => _ = SendAsync(), _ => CanSendNow());
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => _busy);
        InitRewindCommands();
        InitBranchCommands();
        InitFindCommands();
        InitRunningFilter();
        InitMidTurnCommands();
        InitHistoryPaging();
        InitCron();
        InitCapabilities();
        InitRooms();
        InitArtifacts();
        InitJourney();
        InitSkillsHub();
        InitConfigEditor();
        InitTypedSettings();
        InitApprovalsRisk();
        InitInsights();
        InitSessionActions();
        InitTail();
        InitFailureCards();
        InitPalette();
        RefreshInboxCommand = new RelayCommand(_ => _ = RefreshJobInboxAsync());          // Stage 42: the Inbox reads scheduled-job results
        ShowChatsCommand = new RelayCommand(_ => Nav = MainNav.Chats);
        ShowBotsCommand = new RelayCommand(_ => { Nav = MainNav.Bots; _ = RefreshBotsAsync(); });
        ShowInboxCommand = new RelayCommand(_ => { Nav = MainNav.Inbox; _ = RefreshJobInboxAsync(); });
        ShowNeedsYouCommand = new RelayCommand(_ => { Nav = MainNav.NeedsYou; RefreshNeedsYou(); });
        ToggleFailuresCommand = new RelayCommand(_ => { FailuresOnly = !FailuresOnly; RebuildInboxList(); });
        AnswerDenyCommand = new RelayCommand(p => _ = AnswerApprovalAsync(p, ApprovalChoice.Deny));
        AnswerOnceCommand = new RelayCommand(p => _ = AnswerApprovalAsync(p, ApprovalChoice.Once));
        AnswerClarifyCommand = new RelayCommand(p => _ = AnswerClarifyAsync(p));
        AnswerSecretCommand = new RelayCommand(p => _ = AnswerSecretAsync(p));
        DeclineSecretCommand = new RelayCommand(p => _ = DeclineSecretAsync(p));
        SearchSessionsCommand = new RelayCommand(_ => _ = SearchSessionsAsync());
        RefreshSessionsCommand = new RelayCommand(_ => _ = RefreshSessionsAsync());
        OpenSessionCommand = new RelayCommand(p => _ = OpenSessionAsync(p));
        AttachCommand = new RelayCommand(_ => PickAttachments(), _ => !_busy && SelectedConversation is not null);
        RemoveAttachCommand = new RelayCommand(p => { if (p is OutgoingAttachment a) { _attachQueue.Remove(a.Id); RefreshPendingAttachments(); } });
        OpenInboxRunCommand = new RelayCommand(p => _ = OpenInboxRunAsync(p));
        FilterInboxJobCommand = new RelayCommand(p => { InboxJobFilter = p as string; RebuildInboxList(); });
        FilterInboxGroupCommand = new RelayCommand(p => { InboxGroupFilter = p as string; RebuildInboxList(); });
        SaveFollowCommand = new RelayCommand(_ => _ = SaveFollowAsync(), _ => !_inboxLoading);
        DownloadAttachmentCommand = new RelayCommand(p => _ = DownloadAttachmentAsync(p));
        CycleModelCommand = new RelayCommand(_ => _ = CycleModelAsync());
        CycleReasoningCommand = new RelayCommand(_ => CycleReasoning());
        ToggleFastCommand = new RelayCommand(_ => _ = ToggleFastAsync());
        ToggleYoloCommand = new RelayCommand(_ => _ = ToggleYoloAsync(), _ => SelectedConversation is not null);
        RefreshBotsCommand = new RelayCommand(_ => _ = RefreshBotsAsync(), _ => !_botsLoading);
        OpenBotCommand = new RelayCommand(p => _ = OpenBotAsync(p));
        OpenRoomCommand = new RelayCommand(p => OpenRoom(p));
        StopSubagentCommand = new RelayCommand(p => _ = StopSubagentAsync(p), _ => SelectedConversation?.Subagents is not null);
        SteerSubagentCommand = new RelayCommand(_ => _ = SteerSubagentAsync(), _ => SelectedConversation?.Subagents is not null && _subagentSteerText.Trim().Length > 0);
        selection.Changed += () => Application.Current?.Dispatcher.Invoke(() =>
        {
            RebuildProfileChoices();
            RefreshVisibility();
            RebuildInboxList();
            RefreshNeedsYou();
            if (SelectedConversation is { } c) _attachQueue.EnsureOwner(c.Profile, _gate);
            else _attachQueue.Clear();
            RefreshPendingAttachments();
            _ = RefreshSessionsAsync();
            if (Nav == MainNav.Bots) _ = RefreshBotsAsync();
            OnProjectsProfileChanged();
            RefreshSubagentRows();
            CheckOpenRoomStillAllowed();
        });
        RebuildProfileChoices();
    }

    private readonly Func<string> _status;
    public string StatusBanner
    {
        get
        {
            var status = _status();
            if (!OfflineBanner) return status;
            // Simple view says it in a sentence; Advanced keeps the developer wording.
            return _advanced ? status + " · Showing offline copy (stale until connected)."
                             : (status.Length > 0 ? status + " " : "") + PlainStatus.OfflineCopyNote;
        }
    }

    /// <summary>The status line is empty when there is nothing to say (Simple view, connected).</summary>
    public Visibility StatusBannerVisibility => StatusBanner.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Supplied by the window: whether the connection state is one where the person must sign in (again).</summary>
    public Func<bool> SignInOffered { get; set; } = () => false;
    public Visibility SignInPromptVisibility => !_advanced && SignInOffered() ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Called when the connection state changes, so the status line and the sign-in prompt follow it.</summary>
    public void RaiseStatus()
    {
        Raise(nameof(StatusBanner)); Raise(nameof(StatusBannerVisibility)); Raise(nameof(SignInPromptVisibility));
    }
    public ObservableCollection<ConversationVM> Conversations { get; } = new();
    public ObservableCollection<SessionRowVM> SessionRows { get; } = new();
    public ObservableCollection<MessageVM> Messages { get; } = new();
    public ObservableCollection<InboxRunVM> InboxRuns { get; } = new();
    public ObservableCollection<NeedsYouItemVM> NeedsYouItems { get; } = new();
    public ObservableCollection<OutgoingAttachment> PendingAttachments { get; } = new();
    public ObservableCollection<string> InboxJobChips { get; } = new();
    public ObservableCollection<string> InboxGroupChips { get; } = new();
    public ObservableCollection<BotRowVM> BotRows { get; } = new();
    public ObservableCollection<RoomRowVM> RoomRows { get; } = new();
    public ObservableCollection<SubagentRowVM> SubagentRows { get; } = new();
    public ICommand SendCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RefreshInboxCommand { get; }
    public ICommand ShowChatsCommand { get; }
    public ICommand ShowBotsCommand { get; }
    public ICommand ShowInboxCommand { get; }
    public ICommand ShowNeedsYouCommand { get; }
    public ICommand RefreshBotsCommand { get; }
    public ICommand OpenBotCommand { get; }
    public ICommand OpenRoomCommand { get; }
    public ICommand StopSubagentCommand { get; }
    public ICommand SteerSubagentCommand { get; }
    public ICommand ToggleFailuresCommand { get; }
    public ICommand AnswerDenyCommand { get; }
    public ICommand AnswerOnceCommand { get; }
    public ICommand AnswerClarifyCommand { get; }
    public ICommand AnswerSecretCommand { get; }
    public ICommand DeclineSecretCommand { get; }
    public ICommand SearchSessionsCommand { get; }
    public ICommand RefreshSessionsCommand { get; }
    public ICommand OpenSessionCommand { get; }
    public ICommand AttachCommand { get; }
    public ICommand RemoveAttachCommand { get; }
    public ICommand OpenInboxRunCommand { get; }
    public ICommand FilterInboxJobCommand { get; }
    public ICommand FilterInboxGroupCommand { get; }
    public ICommand SaveFollowCommand { get; }
    public ICommand DownloadAttachmentCommand { get; }
    public ICommand CycleModelCommand { get; }
    public ICommand CycleReasoningCommand { get; }
    public ICommand ToggleFastCommand { get; }
    public ICommand ToggleYoloCommand { get; }

    public MainNav Nav
    {
        get => _nav;
        set
        {
            // The one gate: a page that Simple hides can never become the current page, whoever asks (palette, a command, a link).
            if (!Shows(FeatureOf(value))) value = MainNav.Chats;
            if (_nav == value) return;
            _nav = value;
            Raise();
            Raise(nameof(ChatsVisibility));
            Raise(nameof(BotsVisibility));
            Raise(nameof(InboxVisibility));
            Raise(nameof(NeedsYouVisibility));
            Raise(nameof(ProjectsVisibility));
            Raise(nameof(ScheduledVisibility));
            Raise(nameof(CapabilitiesVisibility));
            Raise(nameof(RoomsVisibility));
            Raise(nameof(ArtifactsVisibility));
            Raise(nameof(JourneyVisibility));
            Raise(nameof(InsightsVisibility));
            Raise(nameof(ComposerVisibility));
            Raise(nameof(EmptyChatVisibility));
        }
    }

    public Visibility OnboardingVisibility => _selection.IsLoaded && _selection.NeedsOnboarding ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ChatShellVisibility => _selection.IsLoaded && !_selection.NeedsOnboarding ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ChatsVisibility => ChatShellVisibility == Visibility.Visible && Nav == MainNav.Chats ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BotsVisibility => ChatShellVisibility == Visibility.Visible && Nav == MainNav.Bots ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InboxVisibility => ChatShellVisibility == Visibility.Visible && Nav == MainNav.Inbox ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NeedsYouVisibility => ChatShellVisibility == Visibility.Visible && Nav == MainNav.NeedsYou ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ComposerVisibility => ChatsVisibility;
    public Visibility SubagentPanelVisibility =>
        ChatsVisibility == Visibility.Visible && SubagentRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool FailuresOnly
    {
        get => _failuresOnly;
        set { Set(ref _failuresOnly, value); Raise(nameof(FailuresFilterLabel)); }
    }
    public string FailuresFilterLabel => FailuresOnly ? "Showing failures" : "Show failures only";
    public string InboxStatus { get => _inboxStatus; private set => Set(ref _inboxStatus, value); }
    public bool InboxLoading { get => _inboxLoading; private set { Set(ref _inboxLoading, value); ((RelayCommand)RefreshInboxCommand).Refresh(); ((RelayCommand)SaveFollowCommand).Refresh(); } }
    public string ClarifyAnswer { get => _clarifyAnswer; set => Set(ref _clarifyAnswer, value); }
    public string SecretValue { get => _secretValue; set => Set(ref _secretValue, value); }

    private string _needsYouStatus = "";
    /// <summary>Why the last answer on the Needs-you page did not reach the agent (empty when it did).</summary>
    public string NeedsYouStatus
    {
        get => _needsYouStatus;
        private set { Set(ref _needsYouStatus, value); Raise(nameof(NeedsYouStatusVisibility)); }
    }
    public Visibility NeedsYouStatusVisibility => _needsYouStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public int NeedsYouCount => NeedsYouItems.Count;
    public string NeedsYouNavLabel => NeedsYouCount == 0 ? "Needs you" : $"Needs you ({NeedsYouCount})";
    public string SessionSearch { get => _sessionSearch; set => Set(ref _sessionSearch, value); }
    public string? InboxJobFilter { get => _inboxJobFilter; set { Set(ref _inboxJobFilter, value); Raise(nameof(InboxFilterLabel)); } }
    public string? InboxGroupFilter { get => _inboxGroupFilter; set { Set(ref _inboxGroupFilter, value); Raise(nameof(InboxFilterLabel)); } }
    public string InboxFilterLabel =>
        "Filters: " + (InboxJobFilter is null ? "all jobs" : "job " + InboxJobFilter) +
        " · " + (InboxGroupFilter is null ? "all groups" : "group " + InboxGroupFilter);
    public string FollowJobId { get => _followJobId; set => Set(ref _followJobId, value); }
    public string FollowModeLabel
    {
        get => _followMode switch { FollowMode.Notify => "notify", FollowMode.Off => "off", _ => "quiet" };
        set
        {
            _followMode = value?.Trim().ToLowerInvariant() switch
            {
                "notify" => FollowMode.Notify,
                "off" => FollowMode.Off,
                _ => FollowMode.Quiet
            };
            Raise();
        }
    }
    public bool FollowFailures { get => _followFailures; set => Set(ref _followFailures, value); }
    public bool OfflineBanner { get => _offlineBanner; private set { Set(ref _offlineBanner, value); RaiseStatus(); } }
    public string SelectedDeliveryHint => SelectedConversation switch
    {
        { Delivery: PromptDeliveryState.Uncertain } => "Delivery uncertain — not auto-resent. Check history before resending.",
        { Delivery: PromptDeliveryState.NotReceived } => "Turn missing from transcript — safe to resend manually.",
        { Delivery: PromptDeliveryState.Partial } => "Partial reply kept; reconnect refreshes transcript only.",
        _ => ""
    };
    public string SelectedModelLabel => SelectedConversation?.ModelLabel ?? "Model: default";
    public string SelectedReasoningLabel => SelectedConversation?.ReasoningLabel ?? "Reasoning";
    public string SelectedFastLabel => SelectedConversation?.FastLabel ?? "Normal";
    public string SelectedYoloLabel => SelectedConversation?.YoloEnabled == true ? "YOLO on" : "YOLO off";
    public string YoloTooltip => SessionYoloControls.ScopeDistinction;
    public string IdentityStrip => _identityStrip();
    public string BotsStatus { get => _botsStatus; private set => Set(ref _botsStatus, value); }
    public string GateDisclaimer => BotRosterService.GateDisclaimer;
    public string SteerHint { get => _steerHint; private set => Set(ref _steerHint, value); }
    public string SubagentSteerText
    {
        get => _subagentSteerText;
        set
        {
            Set(ref _subagentSteerText, value);
            ((RelayCommand)SteerSubagentCommand).Refresh();
        }
    }
    public string BotsNavLabel
    {
        get
        {
            var needs = RoomRows.Count(r => r.NeedsYou);
            return needs == 0 ? "Bots" : $"Bots ({needs})";
        }
    }
    public string RoomNotice { get; private set; } = "";

    public void RaiseIdentity() { Raise(nameof(IdentityStrip)); RaiseRiskNotice(); }

    public string InputText
    {
        get => _input;
        set
        {
            Set(ref _input, value);
            UpdateSlashSuggestions();
            RaiseComposerState();
            ((RelayCommand)SendCommand).Refresh();
            RefreshMidTurnCommands();
            _ = PersistDraftAsync();
        }
    }

    public ConversationVM? SelectedConversation
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _ = PersistDraftAsync();
            ExitEditMode(restoreDraft: false);
            SetRewindNotice("");
            ResetFind();
            _selected = value;
            Messages.Clear();
            if (value is not null)
            {
                foreach (var m in value.History) Messages.Add(m);
                OfflineBanner = value.OfflineStale;
                _attachQueue.Bind(value.Profile, value.SessionId);
                _ = LoadDraftAsync(value.Profile);
                RestoreUnsentQueue(value);
            }
            else OfflineBanner = false;
            ResetTail();
            // Conversation/session change invalidates staged terminal context + share previews.
            TerminalShareGate.DiscardAll();
            ClearStagedTerminalContext();
            TerminalSelection = "";
            RefreshPendingAttachments();
            EnsureSubagentMonitor(value);
            RefreshSubagentRows();
            SyncVoiceScope();
            Raise();
            Raise(nameof(SelectedDeliveryHint));
            Raise(nameof(SelectedModelLabel));
            Raise(nameof(SelectedReasoningLabel));
            Raise(nameof(SelectedFastLabel));
            Raise(nameof(SelectedYoloLabel)); Raise(nameof(YoloVisibility)); Raise(nameof(YoloPillVisibility));
            RaiseComposerState();
            Raise(nameof(SubagentPanelVisibility));
            RefreshBusyCommands();
            (OpenFindCommand as RelayCommand)?.Refresh();
            RaiseEarlierState();
            ((RelayCommand)ToggleYoloCommand).Refresh();
            ((RelayCommand)StopSubagentCommand).Refresh();
            ((RelayCommand)SteerSubagentCommand).Refresh();
        }
    }

    private string _mathCacheProfileSig = "";

    public void RefreshVisibility()
    {
        Raise(nameof(EmptyChatVisibility));
        var sig = string.Join('\u001f', _selection.Selected.Select(k => k.ToString()).OrderBy(s => s, StringComparer.Ordinal));
        if (!string.Equals(sig, _mathCacheProfileSig, StringComparison.Ordinal))
        {
            _mathCacheProfileSig = sig;
            MathTypesetter.ClearCache();
        }
        Raise(nameof(OnboardingVisibility));
        Raise(nameof(ChatShellVisibility));
        Raise(nameof(ChatsVisibility));
        Raise(nameof(BotsVisibility));
        Raise(nameof(InboxVisibility));
        Raise(nameof(NeedsYouVisibility));
        Raise(nameof(ProjectsVisibility));
        Raise(nameof(ScheduledVisibility));
        Raise(nameof(CapabilitiesVisibility));
        Raise(nameof(RoomsVisibility));
        Raise(nameof(ArtifactsVisibility));
        Raise(nameof(JourneyVisibility));
        Raise(nameof(InsightsVisibility));
        Raise(nameof(ComposerVisibility));
        RaiseStatus();
        SyncTerminalProfileBinding();
        var visible = _selection.Filter(_all, c => c.Profile);
        Conversations.Clear();
        foreach (var c in visible) Conversations.Add(c);
        if (_selected is not null && !visible.Contains(_selected)) SelectedConversation = null;
    }

    public void NewChat()
    {
        Nav = MainNav.Chats;
        var profile = ActiveProfileKey();
        if (profile is not { } p || !_gate.Allow(Surface.Navigation, p)) return;
        var c = new ConversationVM(p, $"New chat ({p.ProfileId})");
        _all.Insert(0, c);
        RefreshVisibility();
        SelectedConversation = c;
        _sessions()?.SetActiveProfile(p);
    }

    public void AttachNeedsYou(NeedsYouService? service)
    {
        if (service is null) return;
        service.Changed += () => Application.Current?.Dispatcher.Invoke(RefreshNeedsYou);
        RefreshNeedsYou();
    }

    public void RefreshNeedsYou()
    {
        NeedsYouItems.Clear();
        var svc = _needsYou();
        if (svc is null) { Raise(nameof(NeedsYouCount)); return; }
        foreach (var r in svc.Pending)
            NeedsYouItems.Add(new NeedsYouItemVM(r));
        Raise(nameof(NeedsYouCount));
        Raise(nameof(NeedsYouNavLabel));
    }

    public async Task RefreshSessionsAsync()
    {
        var catalog = _sessions();
        var profile = ActiveProfileKey();
        if (catalog is null || profile is not { } p || !_gate.Allow(Surface.Navigation, p))
        {
            SessionRows.Clear();
            return;
        }
        catalog.SetActiveProfile(p);
        try
        {
            IReadOnlyList<SessionSummary> rows;
            if (!string.IsNullOrWhiteSpace(SessionSearch))
                rows = await catalog.SearchAsync(SessionSearch).ConfigureAwait(true);
            else
                rows = await catalog.LoadRecentAsync().ConfigureAwait(true);
            SessionRows.Clear();
            foreach (var s in rows) SessionRows.Add(new SessionRowVM(s, catalog.ShowArchived) { LoadedFor = p });
            OfflineBanner = catalog.IsOfflineStale;
        }
        catch (Exception ex)
        {
            InboxStatus = "Sessions: " + ex.Message; // reuse status area lightly
            for (var i = SessionRows.Count - 1; i >= 0; i--)                       // rows listed for another profile must not stay clickable
                if (SessionRows[i].LoadedFor is { } was && was != p) SessionRows.RemoveAt(i);
        }
    }

    public Task SearchSessionsAsync() => RefreshSessionsAsync();

    public async Task OpenSessionAsync(object? param)
    {
        if (param is not SessionRowVM row) return;
        await OpenStoredAsync(row.Summary.Id, string.IsNullOrWhiteSpace(row.Summary.Title) ? row.Summary.Id : row.Summary.Title!, null, select: true).ConfigureAwait(true);
        _ = MarkReadOnOpenAsync(row);                  // best effort; never delays or blocks opening
    }

    /// <summary>Opens a stored chat (resume + history, offline cache fallback). <paramref name="select"/> false adds it to the list only.</summary>
    private async Task OpenStoredAsync(string storedId, string title, ProfileKey? profileOverride, bool select)
    {
        var profile = profileOverride
            ?? ActiveProfileKey();
        if (profile is not { } p || !_gate.Allow(Surface.Conversation, p)) return;
        if (select) Nav = MainNav.Chats;
        var existing = _all.FirstOrDefault(c => c.StoredSessionId == storedId || c.SessionId == storedId);
        if (existing is not null)
        {
            if (select) SelectedConversation = existing;
            return;
        }

        var convo = new ConversationVM(p, title)
        {
            StoredSessionId = storedId
        };

        var transport = _transport() as HermesDashboardTransport;
        var catalog = _sessions();
        // Reading the history and attaching to the live session are separate: a chat the gateway will not resume (a scheduled
        // run's chat, for one: a live finding) is still readable, and a reply tries to resume it again when it is sent.
        if (transport is not null)
        {
            try
            {
                var resumed = await transport.ResumeSessionAsync(p, storedId).ConfigureAwait(true);
                convo.SessionId = resumed.RuntimeSessionId;
            }
            catch (Exception) { /* read-only for now; the first reply resumes */ }
        }
        string? problem = null;
        try
        {
            if (catalog is not null)
            {
                var page = await catalog.LoadMessagesAsync(storedId, p).ConfigureAwait(true);
                convo.History.AddRange(HistoryMapping.ToMessages(page.Messages));
                convo.RawRowsLoaded = page.Messages.Count;
                convo.HasEarlier = page.Messages.Count >= SessionsApi.HistoryPage;
                convo.OfflineStale = false;
            }
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            var offline = _offline();
            var cached = offline is null ? null : await offline.LoadTranscriptAsync(p, storedId).ConfigureAwait(true);
            if (cached is not null)
            {
                foreach (var m in cached.Messages)
                {
                    var role = m.Role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "You" : "Assistant";
                    convo.History.Add(new MessageVM(role, m.Content));
                }
                convo.OfflineStale = true;
            }
            else
            {
                convo.History.Add(new MessageVM("Assistant", "Couldn't open this chat"
                    + (IsAdvanced && problem is { Length: > 0 } ? " (" + problem + ")." : ". Check the connection and try again.")));
            }
        }

        _all.Insert(0, convo);
        RefreshVisibility();
        if (select) SelectedConversation = convo;
    }

    public async Task RefreshInboxAsync()
    {
        var svc = _inbox();
        if (svc is null)
        {
            InboxStatus = "Inbox unavailable until a dashboard connection is configured.";
            InboxRuns.Clear();
            return;
        }
        InboxLoading = true;
        InboxStatus = "Loading…";
        try
        {
            var groupsStore = _groupsStore();
            if (groupsStore is not null)
                _groups = await groupsStore.LoadAsync(svc.GatewayUrl).ConfigureAwait(true);

            await svc.RefreshAsync().ConfigureAwait(true);
            InboxStatus = svc.State switch
            {
                InboxConnectionState.Ready => $"Ready · {svc.Snapshot.Unread} unread",
                InboxConnectionState.SetupRequired => "Setup required: " + (svc.StatusDetail ?? "herald-push"),
                InboxConnectionState.Disconnected => "Disconnected — " + (svc.StatusDetail ?? "connect in Settings") + " (offline cache retained)",
                _ => "Error — " + (svc.StatusDetail ?? "unknown")
            };
            FollowFailures = svc.Following.Failures;
            RebuildJobGroupChips(svc);
            RebuildInboxList();
        }
        finally { InboxLoading = false; }
    }

    private void RebuildJobGroupChips(InboxService svc)
    {
        InboxJobChips.Clear();
        InboxJobChips.Add("(all jobs)");
        foreach (var j in svc.JobIds()) InboxJobChips.Add(j);
        InboxGroupChips.Clear();
        InboxGroupChips.Add("(all groups)");
        foreach (var g in _groups.Names) InboxGroupChips.Add(g);
    }

    /// <summary>Raised after follow settings were saved (the notification poller re-evaluates).</summary>
    public event Action? InboxFollowSaved;

    /// <summary>The background notification poll refreshed the Inbox store: show it if the list is on screen.</summary>
    public void NotifyInboxStoreChanged()
    {
        if (Nav == MainNav.Inbox) RebuildInboxList();
        else Raise(nameof(InboxStatus));
    }

    private void RebuildInboxList()
    {
        InboxRuns.Clear();
        var svc = _inbox();
        if (svc is null) return;
        var job = InboxJobFilter is null or "(all jobs)" ? null : InboxJobFilter;
        var group = InboxGroupFilter is null or "(all groups)" ? null : InboxGroupFilter;
        foreach (var run in svc.Filter(FailuresOnly, job, group, _groups))
            InboxRuns.Add(new InboxRunVM(run, svc.Snapshot.IsUnread(run)));
        Raise(nameof(InboxFilterLabel));
    }

    private async Task OpenInboxRunAsync(object? param)
    {
        if (param is not InboxRunVM vm) return;
        var svc = _inbox();
        if (svc is null) return;
        var opened = await svc.OpenRunAsync(vm.Run).ConfigureAwait(true);
        vm.MarkReadLocal();
        if (opened is not null)
            MessageBox.Show(opened.Text.Length > 4000 ? opened.Text[..4000] + "…" : opened.Text, opened.Title);
        RebuildInboxList();
    }

    private async Task SaveFollowAsync()
    {
        if (!Shows(ExperienceFeature.InboxSetup)) return;                                  // Simple view never reaches it, whoever asks
        var svc = _inbox();
        if (svc is null) { InboxStatus = "Not connected."; return; }
        if (string.IsNullOrWhiteSpace(FollowJobId)) { InboxStatus = "Enter a job id to follow."; return; }
        InboxLoading = true;
        try
        {
            var next = svc.Following.With(FollowJobId.Trim(), _followMode) with { Failures = FollowFailures };
            await svc.SetFollowingAsync(next).ConfigureAwait(true);
            InboxStatus = "Follow settings saved on gateway.";
            InboxFollowSaved?.Invoke();   // what is followed changed: the notification poll should look again
            if (_groupsStore is not null && InboxGroupFilter is { Length: > 0 } g && g != "(all groups)")
            {
                _groups = _groups.WithJob(FollowJobId.Trim(), g);
                await _groupsStore()!.SaveAsync(svc.GatewayUrl, _groups).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            InboxStatus = "Follow save failed: " + ex.Message;
        }
        finally { InboxLoading = false; }
    }

    private void PickAttachments()
    {
        var convo = SelectedConversation;
        if (convo is null || !_gate.Allow(Surface.Conversation, convo.Profile)) return;
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Attach files (explicit selection only)",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return;
        foreach (var path in dlg.FileNames)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                if (info.Length > OutgoingAttachment.MaxBytes)
                {
                    MessageBox.Show($"Skipping {info.Name}: exceeds {OutgoingAttachment.MaxBytes} bytes.");
                    continue;
                }
                var bytes = File.ReadAllBytes(path);
                var mime = GuessMime(info.Name);
                var att = new OutgoingAttachment(Guid.NewGuid().ToString("N"), info.Name, mime, bytes);
                var err = _attachQueue.TryAdd(convo.Profile, _gate, att);
                if (err is not null) MessageBox.Show(err);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not read file: " + ex.Message);
            }
        }
        RefreshPendingAttachments();
        ((RelayCommand)SendCommand).Refresh();
    }

    private static string GuessMime(string name) =>
        name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
        : name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg"
        : name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? "image/gif"
        : name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp"
        : name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf"
        : "application/octet-stream";

    private void RefreshPendingAttachments()
    {
        PendingAttachments.Clear();
        foreach (var a in _attachQueue.Items) PendingAttachments.Add(a);
    }

    private async Task DownloadAttachmentAsync(object? param)
    {
        if (param is not AttachmentCardVM card || string.IsNullOrEmpty(card.GatewayPath)) return;
        var convo = SelectedConversation;
        if (convo is null || !_gate.Allow(Surface.Conversation, convo.Profile)) return;
        var media = _media();
        if (media is null) { MessageBox.Show("Not connected."); return; }
        var dlg = new SaveFileDialog
        {
            FileName = GatewayFile.SafeDownloadFileName(card.GatewayPath),
            Title = "Save attachment"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var bytes = await media.DownloadAsync(card.GatewayPath, convo.Profile.ProfileId).ConfigureAwait(true);
            await File.WriteAllBytesAsync(dlg.FileName, bytes).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Download failed: " + ex.Message);
        }
    }

    private async Task PersistDraftAsync()
    {
        var convo = _selected;
        var drafts = _drafts();
        if (convo is null || drafts is null) return;
        try { await drafts.SetAsync(convo.Profile, _input).ConfigureAwait(false); }
        catch { /* ignore draft IO */ }
    }

    private async Task LoadDraftAsync(ProfileKey profile)
    {
        var drafts = _drafts();
        if (drafts is null) return;
        try
        {
            var d = await drafts.GetAsync(profile).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(d) && string.IsNullOrEmpty(_input))
                InputText = d;
        }
        catch { /* ignore */ }
    }

    private async Task AnswerApprovalAsync(object? param, ApprovalChoice choice)
    {
        if (param is not NeedsYouItemVM vm) return;
        var svc = _needsYou();
        if (svc is null) return;
        await SendNeedsYouAnswerAsync(() => svc.AnswerApprovalAsync(vm.Request.Id, choice));
        RefreshNeedsYou();
    }

    private const string NeedsYouNotSent = "Couldn't send your answer, so Hermes is still waiting. Check the connection and try again.";

    /// <summary>
    /// Sends one Needs-you answer. The commands run it without awaiting, so a failure (a dropped connection, a request the gateway
    /// no longer knows) would otherwise vanish: the click would seem to do nothing. It is said on the page instead, and the request stays listed.
    /// </summary>
    private async Task<bool> SendNeedsYouAnswerAsync(Func<Task> send)
    {
        try
        {
            await send();
            NeedsYouStatus = "";
            return true;
        }
        catch (Exception)
        {
            NeedsYouStatus = NeedsYouNotSent;
            return false;
        }
    }

    private async Task AnswerClarifyAsync(object? param)
    {
        if (param is not NeedsYouItemVM vm) return;
        var svc = _needsYou();
        if (svc is null) return;
        var answer = ClarifyAnswer;
        ClarifyAnswer = "";
        if (!await SendNeedsYouAnswerAsync(() => svc.AnswerClarifyAsync(vm.Request.Id, answer)) && ClarifyAnswer.Length == 0)
            ClarifyAnswer = answer;                                   // not lost: the person can press Send again
        RefreshNeedsYou();
    }

    private async Task AnswerSecretAsync(object? param)
    {
        if (param is not NeedsYouItemVM vm) return;
        var svc = _needsYou();
        if (svc is null) return;
        var value = SecretValue;
        SecretValue = "";                                             // a secret is never kept, even when sending fails
        await SendNeedsYouAnswerAsync(() => svc.AnswerSecretAsync(vm.Request.Id, value));
        RefreshNeedsYou();
    }

    private async Task DeclineSecretAsync(object? param)
    {
        if (param is not NeedsYouItemVM vm) return;
        var svc = _needsYou();
        if (svc is null) return;
        SecretValue = "";
        await SendNeedsYouAnswerAsync(() => svc.DeclineSecretAsync(vm.Request.Id));
        RefreshNeedsYou();
    }

    private bool CanSendNow() =>
        SelectedConversation is not null
        && (_busy
            // While a reply runs, Send steers it (text only) or starts a /btw or /bg side task.
            ? _input.Trim().Length > 0 && !IsEditing && (_attachQueue.Items.Count == 0 || TryParseSideTask(_input.Trim(), out _, out _))
            : _input.Trim().Length > 0 || _attachQueue.Items.Count > 0 || _terminalContext.HasPending);

    private async Task SendAsync()
    {
        // In edit mode Send means "replace that message": confirmed, rewound, never a plain append.
        if (_editTarget is { } editing)
        {
            var edited = _input.Trim();
            await RewindAsync(editing, isEdit: true, edited, edited).ConfigureAwait(true);
            return;
        }
        var raw = _input.Trim();
        if (await TryRunSlashCommandAsync(raw).ConfigureAwait(true)) return;   // /model, /reasoning, /fast, /yolo, /new, /stop, /branch
        if (TryParseSideTask(raw, out var kind, out var arg))
        {
            InputText = "";
            await RunSideTaskAsync(kind, arg).ConfigureAwait(true);
            return;
        }
        if (_busy)
        {
            await SteerNowAsync(raw).ConfigureAwait(true);
            return;
        }
        await SendCoreAsync(null).ConfigureAwait(true);
    }

    /// <param name="textOverride">Send this text instead of the composer's (queue release, stop-and-send).</param>
    /// <param name="textOnly">Text only: no staged attachments or terminal context ride along (queue release, rewind).</param>
    private async Task SendCoreAsync(RewindPlan? plan, string? textOverride = null, bool textOnly = false)
    {
        var convo = SelectedConversation;
        var transport = _transport();
        if (convo is null || !_gate.Allow(Surface.Conversation, convo.Profile)) return;

        var text = plan?.Display ?? textOverride ?? _input.Trim();
        var onlyText = plan is not null || textOnly;
        // A rewind sends text only; staged attachments / terminal context stay queued for a normal send.
        var pending = !onlyText ? _attachQueue.Items.ToList() : _attachQueue.Items.Take(0).ToList();
        var dest = new TerminalShareDestination(convo.Profile, convo.SessionId);
        string promptWithTerminal;
        bool hadTerminalContext;
        if (!onlyText && _terminalContext.HasPending)
        {
            promptWithTerminal = _terminalContext.BuildNextPrompt(dest, text);
            hadTerminalContext = !string.Equals(promptWithTerminal, text, StringComparison.Ordinal);
            _terminalContext.Consume(); // one-shot; mismatch drops without transmitting
        }
        else
        {
            promptWithTerminal = plan?.Send ?? text;
            hadTerminalContext = false;
        }
        Raise(nameof(StagedTerminalContextLabel));
        Raise(nameof(StagedTerminalContextVisibility));
        ((RelayCommand?)ClearStagedTerminalContextCommand)?.Refresh();
        if (plan is null && textOverride is null)
        {
            InputText = "";
            await PersistDraftAsync();
        }

        var user = new MessageVM("You", hadTerminalContext ? promptWithTerminal : text);
        foreach (var a in pending)
            user.Attachments.Add(new AttachmentCardVM(a.Name, a.Kind, localPreview: a.Kind == AttachmentKind.Image ? a.Bytes : null));
        var reply = new MessageVM("Assistant");
        convo.History.Add(user); convo.History.Add(reply);
        Messages.Add(user); Messages.Add(reply);
        RelayoutQueueBubbles(convo);

        if (transport is null)
        {
            reply.Append("Not connected: configure a gateway in Settings. No reply was generated.");
            return;
        }

        _busy = true; RefreshBusyCommands();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _turnDone ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn = new TurnContext(reply);
        reply.IsLive = true;                                                            // its activity line shows until the turn ends
        _turn = turn;
        var coalescer = new TokenCoalescer(s => turn.Reply.Append(s), TimeSpan.FromMilliseconds(50));
        // Reasoning used to repaint on every token. The same 50 ms batch as the answer keeps a long "thinking" stream responsive.
        var reasoning = new TokenCoalescer(s => turn.Reply.AppendReasoning(s, DateTimeOffset.UtcNow), TimeSpan.FromMilliseconds(50));
        turn.BeforeSwap = () => { coalescer.Flush(); reasoning.Flush(); };
        var clean = false;       // a StreamCompleted with no error: only then may a queued prompt follow
        var sawError = false;
        try
        {
            var promptText = promptWithTerminal;
            var uploader = _uploader();
            if (transport is HermesDashboardTransport hermes)
            {
                if (string.IsNullOrEmpty(convo.SessionId) && !string.IsNullOrEmpty(convo.StoredSessionId))
                {
                    var resumed = await hermes.ResumeSessionAsync(convo.Profile, convo.StoredSessionId, ct).ConfigureAwait(true);
                    convo.SessionId = resumed.RuntimeSessionId;
                }
                if (string.IsNullOrEmpty(convo.SessionId) && pending.Count > 0)
                {
                    var created = await hermes.CreateSessionAsync(convo.Profile, ct).ConfigureAwait(true);
                    convo.SessionId = created.SessionId;
                    if (!string.IsNullOrEmpty(created.StoredSessionId))
                        convo.StoredSessionId = created.StoredSessionId;
                }
            }

            if (pending.Count > 0)
            {
                if (uploader is null || string.IsNullOrEmpty(convo.SessionId))
                {
                    turn.Reply.Activity = "Cannot attach: session or uploader unavailable.";
                    ShowTurnFailure(turn.Reply, convo, TurnFailureKind.AttachFailed, "The session or uploader is unavailable.", text, true);
                    return;
                }
                foreach (var file in pending)
                {
                    var result = await uploader.UploadAsync(convo.SessionId!, file, ct).ConfigureAwait(true);
                    if (!result.Ok)
                    {
                        turn.Reply.Activity = "Attach failed: " + (result.Error ?? "unknown");
                        if (!string.IsNullOrEmpty(result.Path))
                            await uploader.DetachBestEffortAsync(convo.SessionId!, result.Path!, CancellationToken.None).ConfigureAwait(true);
                        ShowTurnFailure(turn.Reply, convo, TurnFailureKind.AttachFailed, result.Error, text, true);
                        return;
                    }
                    if (!string.IsNullOrEmpty(result.RefText))
                        promptText = result.RefText + "\n" + promptText;
                    user.Attachments.Add(new AttachmentCardVM(file.Name, file.Kind, result.Path));
                }
                _attachQueue.Clear();
                RefreshPendingAttachments();
            }

            if (string.IsNullOrWhiteSpace(promptText) && pending.Count > 0)
                promptText = "What do you see in this image?";

            var request = new ChatRequest(convo.Profile,
                convo.History.Where(m => m != reply && !m.IsQueued && !m.IsSideCard).Select(m => new ChatMessage(m.Role == "You" ? "user" : "assistant",
                    m == user ? promptText : m.Text)).ToList(),
                convo.SessionId,
                convo.PendingModel, convo.PendingProvider, convo.PendingReasoning, convo.PendingFast,
                RewindBeforeRowId: plan?.RowId);

            // Never wrap StreamChatAsync in ReconnectPolicy — that would auto-resubmit prompt.submit.
            convo.Delivery = PromptDeliveryState.Pending;
            Raise(nameof(SelectedDeliveryHint));
            try
            {
                await foreach (var ev in transport.StreamChatAsync(request, ct))
                {
                    switch (ev)
                    {
                        case SessionBound s:
                            convo.SessionId = s.SessionId;
                            if (!string.IsNullOrEmpty(s.StoredSessionId))
                                convo.StoredSessionId = s.StoredSessionId;
                            convo.Delivery = PromptResendPolicy.AfterSubmitAck();
                            break;
                        case TokenDelta d:
                            convo.Delivery = PromptResendPolicy.AfterFirstToken(convo.Delivery);
                            reasoning.Flush();                                              // the answer started: show the thinking, then close it
                            turn.Reply.EndReasoning(DateTimeOffset.UtcNow);
                            coalescer.Append(d.Text);
                            break;
                        case ToolActivity t: turn.Reply.ToolSummary = t.Summary; break;
                        // Stage 44: what the agent is doing, as it happens (BookahHermes ChatReducer).
                        case ReasoningDelta rd: reasoning.Append(rd.Text); break;
                        case FinalReasoning fr:
                            reasoning.Flush();
                            if (!turn.Reply.HasReasoning) turn.Reply.SetStoredReasoning(fr.Text);
                            break;
                        case ActivityLabel al: turn.Reply.SetActivityLabel(al.Text); break;
                        case StatusLine sl: turn.Reply.SetStatus(sl.Text); break;
                        case ToolStarted ts:
                            reasoning.Flush();                                              // a tool means thinking stopped; don't leave the last words buffered
                            turn.Reply.StartTool(ts, DateTimeOffset.UtcNow);
                            break;
                        case ToolFinished tf:
                            // A steer may have moved the reply on; the step finishes wherever it started.
                            (convo.History.LastOrDefault(m => m.Tools.Any(x => x.Id == tf.Id)) ?? turn.Reply).FinishTool(tf);
                            break;
                        case ToolRiskFlagged rf:
                            (convo.History.LastOrDefault(m => m.Tools.Any(x => x.Id == rf.Id)) ?? turn.Reply).FlagRisk(rf);
                            break;
                        case PlanUpdated pu: turn.Reply.SetPlan(pu.Plan); break;
                        case AgentNotice an: turn.Reply.AddNotice(an.Text); break;
                        case UserRowBound b: user.RowId = b.RowId; if (plan is not null && plan.Send != text) user.SentText = plan.Send; break;
                        case RewindRefused rr when plan is not null:
                            RestoreAfterRewindRefused(convo, plan, user, reply, rr);
                            sawError = true;
                            break;
                        case ProtocolError pe:
                            turn.Reply.Activity = "Protocol error: " + pe.Message;
                            sawError = true;
                            convo.Delivery = PromptDeliveryState.Settled;
                            ShowTurnFailure(turn.Reply, convo, TurnFailureKind.GatewayError, pe.Message, text, pending.Count > 0 || hadTerminalContext);
                            break;
                        case SessionTitled st:
                            convo.Title = st.Title;                     // the gateway named the chat (live finding: was shown as "unknown event")
                            break;
                        case UnknownGatewayEvent:
                            break;                                      // an event this client does not know is never shown in a reply
                        case StreamCompleted:
                            convo.Delivery = PromptDeliveryState.Settled;
                            clean = !sawError;
                            break;
                    }
                    Raise(nameof(SelectedDeliveryHint));
                }
            }
            catch (IOException) when (!ct.IsCancellationRequested)
            {
                convo.Delivery = PromptResendPolicy.AfterDisconnect(convo.Delivery);
                Raise(nameof(SelectedDeliveryHint));
                if (transport is HermesDashboardTransport hermesRecover
                    && !string.IsNullOrEmpty(convo.StoredSessionId))
                {
                    turn.Reply.Activity = "Connection dropped — recovering transcript (no auto-resend)…";
                    hermesRecover.RequestReconnect();
                    try
                    {
                        await hermesRecover.EnsureConnectedAsync(ct).ConfigureAwait(true);
                        var resumed = await hermesRecover.ResumeSessionAsync(convo.Profile, convo.StoredSessionId!, ct)
                            .ConfigureAwait(true);
                        convo.SessionId = resumed.RuntimeSessionId;
                        var api = hermesRecover.CreateSessionsApi();
                        var msgs = await SessionRecovery.RefetchTranscriptAsync(api, convo.Profile, convo.StoredSessionId!, ct)
                            .ConfigureAwait(true);
                        ApplyRecoveredTranscript(convo, msgs, turn.Reply);
                        turn.Reply.Activity = convo.ShowResendHint
                            ? "Delivery uncertain. Resend only if the turn is missing (not automatic)."
                            : "Transcript refreshed after reconnect.";
                        // A card only when the turn did not simply recover: delivery still unclear, or known missing.
                        if (convo.Delivery is PromptDeliveryState.Uncertain or PromptDeliveryState.NotReceived or PromptDeliveryState.Pending)
                            ShowTurnFailure(turn.Reply, convo, TurnFailureKind.ConnectionDropped, null, text, pending.Count > 0 || hadTerminalContext);
                    }
                    catch (Exception rex)
                    {
                        turn.Reply.Activity = "Reconnect/refetch failed: " + rex.Message
                            + " (prompt was NOT auto-resent).";
                        ShowTurnFailure(turn.Reply, convo, TurnFailureKind.ConnectionDropped, rex.Message, text, pending.Count > 0 || hadTerminalContext);
                    }
                }
                else
                {
                    turn.Reply.Activity = "Connection dropped before a clear delivery result (prompt NOT auto-resent).";
                    ShowTurnFailure(turn.Reply, convo, TurnFailureKind.ConnectionDropped, null, text, pending.Count > 0 || hadTerminalContext);
                }
            }

            convo.OfflineStale = false;
            OfflineBanner = false;
            var offline = _offline();
            if (offline is not null && !string.IsNullOrEmpty(convo.StoredSessionId ?? convo.SessionId))
            {
                await offline.SaveTranscriptAsync(new OfflineTranscript(
                    convo.StoredSessionId ?? convo.SessionId!,
                    convo.Profile,
                    convo.History.Where(m => !m.IsQueued && !m.IsSideCard).Select(m => new OfflineTranscriptMessage(
                        m.Role == "You" ? "user" : "assistant", m.Text)).ToList(),
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) { turn.Reply.Activity = "Stopped."; }
        catch (Exception ex)
        {
            turn.Reply.Activity = "Error: " + ex.GetType().Name + (ex.Message.Length > 0 ? " — " + ex.Message : "");
            ShowTurnFailure(turn.Reply, convo, TurnFailureKind.ClientError, ex.GetType().Name + (ex.Message.Length > 0 ? ": " + ex.Message : ""), text, pending.Count > 0 || hadTerminalContext);
        }
        finally
        {
            coalescer.Flush();
            reasoning.Flush();
            reply.FinishLive(DateTimeOffset.UtcNow);
            turn.Reply.FinishLive(DateTimeOffset.UtcNow);
            _busy = false; _cts.Dispose(); _cts = null;
            RefreshBusyCommands();
            Raise(nameof(SelectedDeliveryHint));
            _turn = null;
            // Releases the next queued prompt after a clean finish; otherwise hands queued text back, never sending it.
            await AfterTurnAsync(convo, clean && !ct.IsCancellationRequested).ConfigureAwait(true);
        }
    }

    private static void ApplyRecoveredTranscript(ConversationVM convo, IReadOnlyList<ChatMessage> msgs, MessageVM streamingReply)
    {
        // Replace assistant bubble text from last assistant message if present; keep UI roles.
        var lastAssistant = msgs.LastOrDefault(m =>
            string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        if (lastAssistant is not null)
            streamingReply.ReplaceText(lastAssistant.Content);
        convo.OfflineStale = false;
    }

    private SessionModelControls BindModelControls(ConversationVM convo)
    {
        if (convo.LiveModelControls is { } existing) return existing;
        var controls = new SessionModelControls(
            () => (_transport() as HermesDashboardTransport)?.Client,
            async () =>
            {
                if (!string.IsNullOrEmpty(convo.SessionId)) return convo.SessionId!;
                if (_transport() is not HermesDashboardTransport hermes)
                    throw new InvalidOperationException("Not connected");
                if (!string.IsNullOrEmpty(convo.StoredSessionId))
                {
                    var resumed = await hermes.ResumeSessionAsync(convo.Profile, convo.StoredSessionId!).ConfigureAwait(false);
                    convo.SessionId = resumed.RuntimeSessionId;
                    return convo.SessionId!;
                }
                var created = await hermes.CreateSessionAsync(convo.Profile).ConfigureAwait(false);
                convo.SessionId = created.SessionId;
                if (!string.IsNullOrEmpty(created.StoredSessionId))
                    convo.StoredSessionId = created.StoredSessionId;
                return convo.SessionId!;
            });
        if (!string.IsNullOrEmpty(convo.PendingModel) && !string.IsNullOrEmpty(convo.PendingProvider))
            controls.RememberPending(convo.PendingModel!, convo.PendingProvider!, convo.PendingReasoning, convo.PendingFast);
        convo.LiveModelControls = controls;
        return controls;
    }

    private async Task CycleModelAsync()
    {
        var convo = SelectedConversation;
        if (convo is null || !_gate.Allow(Surface.Conversation, convo.Profile)) return;
        try
        {
            if (_transport() is not HermesDashboardTransport hermes)
            {
                // Pending new-chat selection without live catalog.
                convo.PendingProvider ??= "default";
                convo.PendingModel = convo.PendingModel is null ? "pending-pick" : null;
                convo.ModelLabel = convo.PendingModel is null ? "Model: default" : "Model: " + convo.PendingModel + " (pending)";
                Raise(nameof(SelectedModelLabel));
                return;
            }
            await hermes.EnsureConnectedAsync(default).ConfigureAwait(true);
            var controls = BindModelControls(convo);
            var catalog = await controls.OptionsAsync(convo.SessionId, convo.Profile.ProfileId).ConfigureAwait(true);
            var flat = catalog.Providers.SelectMany(p => p.Models.Select(m => (p.Slug, m))).ToList();
            if (flat.Count == 0)
            {
                convo.ModelLabel = "Model: (catalog empty — not entitlement)";
                Raise(nameof(SelectedModelLabel));
                return;
            }
            var idx = flat.FindIndex(x => x.m.Id == convo.PendingModel && x.Slug == convo.PendingProvider);
            var next = flat[(idx + 1) % flat.Count];
            // No automatic paid switch: usable() already filtered; still never confirm_expensive without UI confirm.
            if (string.IsNullOrEmpty(convo.SessionId) && string.IsNullOrEmpty(convo.StoredSessionId))
            {
                controls.RememberPending(next.m.Id, next.Slug);
                convo.PendingModel = next.m.Id;
                convo.PendingProvider = next.Slug;
                convo.ModelLabel = $"Model: {next.m.Id} ({next.Slug}, pending new chat)";
            }
            else
            {
                var result = await controls.SetModelAsync(next.m.Id, next.Slug).ConfigureAwait(true);
                if (result is ModelSwitchNeedsConfirmation conf)
                {
                    convo.ModelLabel = "Confirm required: " + conf.Message + " (not auto-switched)";
                }
                else if (result is ModelSwitchDone)
                {
                    convo.PendingModel = next.m.Id;
                    convo.PendingProvider = next.Slug;
                    convo.ModelLabel = $"Model: {next.m.Id} ({next.Slug})";
                }
                else if (result is ModelSwitchFailed fail)
                    convo.ModelLabel = "Model switch failed: " + (fail.Error ?? "");
            }
            Raise(nameof(SelectedModelLabel));
        }
        catch (Exception ex)
        {
            convo.ModelLabel = "Model: error — " + ex.Message;
            Raise(nameof(SelectedModelLabel));
        }
    }

    private void CycleReasoning()
    {
        var levels = SlashCommands.ReasoningLevels;
        var cur = SelectedConversation?.PendingReasoning ?? "medium";
        SetReasoning(levels[(levels.ToList().IndexOf(cur) + 1) % levels.Count]);
    }

    /// <summary>Reasoning effort for this chat (/reasoning): from the next message for a new chat, at once for a live one.</summary>
    private void SetReasoning(string next)
    {
        var convo = SelectedConversation;
        if (convo is null) return;
        convo.PendingReasoning = next;
        convo.ReasoningLabel = "Reasoning: " + next;
        if (!string.IsNullOrEmpty(convo.SessionId))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var controls = BindModelControls(convo);
                    await controls.SetReasoningEffortAsync(next).ConfigureAwait(false);
                }
                catch { /* label already updated; live set best-effort */ }
            });
        }
        Raise(nameof(SelectedReasoningLabel));
        SetRewindNotice("Reasoning for this chat: " + next + ".");
    }

    private Task ToggleFastAsync() => SetFastAsync(!(SelectedConversation?.PendingFast ?? false));

    /// <summary>Fast mode for this chat (/fast on|off).</summary>
    private async Task SetFastAsync(bool next)
    {
        var convo = SelectedConversation;
        if (convo is null) return;
        convo.PendingFast = next;
        convo.FastLabel = next ? "Fast" : "Normal";
        Raise(nameof(SelectedFastLabel));
        if (string.IsNullOrEmpty(convo.SessionId)) return;
        try
        {
            var controls = BindModelControls(convo);
            await controls.SetFastAsync(next).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            convo.FastLabel = "Fast error: " + ex.Message;
            Raise(nameof(SelectedFastLabel));
        }
    }

    private async Task ToggleYoloAsync()
    {
        var convo = SelectedConversation;
        if (convo is null || !_gate.Allow(Surface.Conversation, convo.Profile)) return;
        var enable = convo.YoloEnabled != true;
        if (enable && SessionYoloControls.RequiresConfirmation(true))
        {
            var ok = MessageBox.Show(
                SessionYoloControls.EnableConfirmMessage + "\n\n" + SessionYoloControls.ScopeDistinction,
                "Enable YOLO for this chat?",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) == MessageBoxResult.OK;
            if (!ok) return;
        }
        try
        {
            if (_transport() is not HermesDashboardTransport)
            {
                // Demo / offline: flip local label only — backend YOLO is on/off, not three session modes.
                convo.YoloEnabled = enable;
                Raise(nameof(SelectedYoloLabel)); Raise(nameof(YoloVisibility)); Raise(nameof(YoloPillVisibility));
                return;
            }
            var yolo = new SessionYoloControls(
                () => (_transport() as HermesDashboardTransport)?.Client,
                async () =>
                {
                    if (!string.IsNullOrEmpty(convo.SessionId)) return convo.SessionId!;
                    if (_transport() is not HermesDashboardTransport hermes)
                        throw new InvalidOperationException("Not connected");
                    if (!string.IsNullOrEmpty(convo.StoredSessionId))
                    {
                        var resumed = await hermes.ResumeSessionAsync(convo.Profile, convo.StoredSessionId!).ConfigureAwait(false);
                        convo.SessionId = resumed.RuntimeSessionId;
                        return convo.SessionId!;
                    }
                    var created = await hermes.CreateSessionAsync(convo.Profile).ConfigureAwait(false);
                    convo.SessionId = created.SessionId;
                    if (!string.IsNullOrEmpty(created.StoredSessionId))
                        convo.StoredSessionId = created.StoredSessionId;
                    return convo.SessionId!;
                });
            // The answer belongs to the chat it was sent for, even if another chat is open by now.
            convo.YoloEnabled = await yolo.SetAsync(enable, default).ConfigureAwait(true);
            Raise(nameof(SelectedYoloLabel)); Raise(nameof(YoloVisibility)); Raise(nameof(YoloPillVisibility));
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not change YOLO: " + ex.Message, "YOLO", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public async Task RefreshBotsAsync()
    {
        var svc = _bots();
        if (svc is null)
        {
            BotRows.Clear();
            RoomRows.Clear();
            BotsStatus = "Bots / rooms blocked: no Bot Mode source wired (connect gateway or use demo fixtures).";
            Raise(nameof(BotsNavLabel));
            return;
        }
        _botsLoading = true;
        ((RelayCommand)RefreshBotsCommand).Refresh();
        BotsStatus = "Loading roster…";
        try
        {
            var roster = await svc.LoadVisibleRosterAsync().ConfigureAwait(true);
            BotRows.Clear();
            foreach (var bot in roster.Bots)
            {
                var preview = BotRosterParser.PreviewLine(bot);
                var unread = bot.CanonicalSession is { MessageCount: > 0 };
                var activity = bot.CanonicalSession is not null ? "Bot Chat" : "no Bot Chat yet";
                BotRows.Add(new BotRowVM(bot, preview, unread, activity));
            }
            var rooms = await svc.LoadRoomsAsync().ConfigureAwait(true);
            RoomRows.Clear();
            foreach (var r in rooms.Visible) RoomRows.Add(new RoomRowVM(r));
            foreach (var r in rooms.Restricted) RoomRows.Add(new RoomRowVM(r));
            BotsStatus = $"{roster.Bots.Count} bot(s) · {rooms.Visible.Count} room(s)"
                + (rooms.Restricted.Count > 0 ? $" · {rooms.Restricted.Count} restricted" : "")
                + (roster.TeammateProtocol ? " · teammate protocol" : "")
                + " · " + roster.GateDisclaimer;
            Raise(nameof(BotsNavLabel));
        }
        catch (Exception ex)
        {
            BotsStatus = "Bots unavailable: " + ex.Message;
        }
        finally
        {
            _botsLoading = false;
            ((RelayCommand)RefreshBotsCommand).Refresh();
        }
    }

    public async Task OpenBotAsync(object? param)
    {
        if (param is not BotRowVM row) return;
        var profile = new ProfileKey(
            _selection.Selected.Select(k => k.GatewayId).FirstOrDefault() ?? "demo",
            row.Bot.Name);
        if (!_gate.Allow(Surface.Conversation, profile))
        {
            BotsStatus = $"Profile “{row.Bot.Name}” is not selected — preference filtering, not server authorization.";
            return;
        }

        var svc = _bots();
        string? openId = row.Bot.CanonicalSession?.OpenId;
        try
        {
            if (svc is not null)
            {
                // Prefer canonical open via BotChats when a live/fixture backend is available through BotsApi.
                var api = new BotsApi(_rpc);
                var chats = new BotChats(api);
                await chats.NoteAsync(new[] { row.Bot }).ConfigureAwait(true);
                // Demo/fixture path: openId already known; live path uses BotChats.
                if (openId is null && _rpc() is not null)
                    openId = await chats.OpenAsync(row.Bot).ConfigureAwait(true);
                else if (openId is null)
                    openId = await new BotChats(new FixtureBotBackend(row.Bot)).OpenAsync(row.Bot).ConfigureAwait(true);
            }
        }
        catch (BotChatUnavailableException ex)
        {
            BotsStatus = ex.Message;
            return;
        }
        catch (Exception ex)
        {
            BotsStatus = "Could not open Bot Chat: " + ex.Message;
            return;
        }

        Nav = MainNav.Chats;
        var existing = _all.FirstOrDefault(c => c.IsBotChat && c.Profile == profile);
        if (existing is not null)
        {
            SelectedConversation = existing;
            return;
        }
        var convo = new ConversationVM(profile, row.Bot.Label + " · Bot Chat")
        {
            IsBotChat = true,
            StoredSessionId = openId,
            SessionId = openId
        };
        EnsureSubagentMonitor(convo);
        _all.Insert(0, convo);
        RefreshVisibility();
        SelectedConversation = convo;
        // Seed synthetic subagent progress in demo so the frame is visible without fabricating live events.
        if (_rpc() is null && convo.Subagents is not null)
        {
            convo.Subagents.ApplyEvent("subagent.start",
                System.Text.Json.Nodes.JsonNode.Parse("""{"subagent_id":"demo-s1","goal":"Synthetic subagent (fixture)","status":"running"}""")!.AsObject());
            RefreshSubagentRows();
        }
    }

    private void OpenRoom(object? param)
    {
        if (param is not RoomRowVM row) return;
        if (row.IsRestricted)
        {
            RoomNotice = row.Explanation;
            Raise(nameof(RoomNotice));
            return;
        }
        RoomNotice = $"“{row.Name}” is a Desktop room (read-only mirror). Continue on Desktop — Hikyaku does not write group rounds.";
        Raise(nameof(RoomNotice));
        if (row.NeedsYou)
            ShowNeedsYouCommand.Execute(null);
    }

    private void EnsureSubagentMonitor(ConversationVM? convo)
    {
        if (convo is null) return;
        if (string.IsNullOrEmpty(convo.SessionId)) return;
        if (convo.Subagents is null || !convo.Subagents.Accepts(convo.Profile, convo.SessionId))
            convo.Subagents = new SubagentMonitor(convo.Profile, convo.SessionId!);
    }

    public void RefreshSubagentRows()
    {
        SubagentRows.Clear();
        var mon = SelectedConversation?.Subagents;
        if (mon is not null)
            foreach (var r in mon.Rows)
                SubagentRows.Add(new SubagentRowVM(r));
        Raise(nameof(SubagentPanelVisibility));
    }

    public void ApplySubagentEvent(ProfileKey profile, string? sessionId, string type, System.Text.Json.Nodes.JsonObject? payload)
    {
        var convo = SelectedConversation;
        if (convo?.Subagents is null || !convo.Subagents.Accepts(profile, sessionId)) return;
        convo.Subagents.ApplyEvent(type, payload);
        RefreshSubagentRows();
    }

    private async Task StopSubagentAsync(object? param)
    {
        if (param is not SubagentRowVM row || row.Row.StoppableId is null) return;
        var mon = SelectedConversation?.Subagents;
        var client = _rpc();
        if (mon is null || client is null)
        {
            SteerHint = "Stop blocked: not connected (fixture-only cannot interrupt a live child).";
            return;
        }
        await mon.StopAsync(client, row.Row.StoppableId).ConfigureAwait(true);
        RefreshSubagentRows();
        SteerHint = mon.LastError ?? "Stop requested — waiting for subagent.complete.";
    }

    private async Task SteerSubagentAsync()
    {
        var mon = SelectedConversation?.Subagents;
        var client = _rpc();
        var text = SubagentSteerText.Trim();
        if (mon is null || text.Length == 0) return;
        if (client is null)
        {
            // Demo honesty: show queued-ack label without claiming child read.
            var demo = SubagentSteerOutcome.FromWireStatus(SubagentSteerOutcome.WireAccepted);
            SteerHint = demo.HonestyLabel;
            SubagentSteerText = "";
            return;
        }
        var outcome = await mon.SteerAsync(client, text).ConfigureAwait(true);
        SteerHint = outcome.HonestyLabel;
        SubagentSteerText = "";
    }

    private sealed class FixtureBotBackend(Bot bot) : IBotChatBackend
    {
        public Task<BotSession?> BotChatAsync(string profile, CancellationToken ct = default)
            => Task.FromResult(profile == bot.Name ? bot.CanonicalSession : null);
        public Task<string> StartBotChatAsync(string profile, CancellationToken ct = default)
            => throw new BotChatUnavailableException("Live bot creation is disabled in fixtures.");
    }
}