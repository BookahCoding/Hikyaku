using System.Collections.Specialized;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>A prompt held in this app until the running reply finishes (an app-side queue).</summary>
/// <param name="AfterStop">Queued behind a stopped reply that would not settle: it still runs once that reply ends.</param>
public sealed record QueuedPrompt(MessageVM Bubble, string Text, bool AfterStop = false);

/// <summary>
/// Sending while a reply runs, and side questions (BookahHermes ChatSession.steer / askAside / runInBackground):
/// <b>Steer</b> (<c>session.steer</c>, folded into the running turn), <b>Queue</b> (held in this app, then sent as an ordinary
/// turn after a clean finish — never resent automatically after a stop, error or chat switch), <b>Stop &amp; send</b>, and
/// the <c>/btw</c> and <c>/bg</c> side tasks whose answers arrive as events. The queue is app-side, not the gateway's: queued
/// text does not survive closing the app, which the UI states.
/// </summary>
public sealed partial class MainViewModel
{
    public static readonly TimeSpan DefaultStopSettle = TimeSpan.FromSeconds(10);
    private const int MaxSideBuffer = 16;

    /// <summary>What the running turn currently streams into; a steer starts a new reply bubble below the steer text.</summary>
    private sealed class TurnContext(MessageVM reply)
    {
        public MessageVM Reply { get; private set; } = reply;
        public Action? BeforeSwap { get; set; }
        public void Swap(MessageVM next)
        {
            BeforeSwap?.Invoke();
            Reply.IsLive = false;          // its tool steps still finish when their events arrive (found by id)
            next.IsLive = true;
            Reply = next;
        }
    }

    private TurnContext? _turn;
    private TaskCompletionSource? _turnDone;
    private Dispatcher? _uiDispatcher;
    private IGatewayEventSource? _sideSource;
    private readonly Dictionary<string, MessageVM> _sideCards = new();
    private readonly Dictionary<string, SideTaskAnswer> _sideBuffer = new();

    public TimeSpan StopSettleTimeout { get; set; } = DefaultStopSettle;

    public ICommand? QueueCommand { get; private set; }
    public ICommand? StopAndSendCommand { get; private set; }
    public ICommand? UnqueueCommand { get; private set; }

    public string SendLabel => _busy ? "Steer" : "Send";
    public Visibility RunningActionsVisibility => _busy ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Queue and Stop &amp; send. Advanced only; Simple keeps the send arrow and Stop.</summary>
    public Visibility MidTurnExtrasVisibility =>
        _busy && Shows(ExperienceFeature.MidTurnActions) ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>The send arrow. Simple never says "steer" or mentions slash commands.</summary>
    public string SendToolTip => !_busy
        ? (_advanced ? "Send (Enter). Type / for commands." : "Send (Enter).")
        : _advanced
            ? "Add this to the reply that is already running."
            : "Send this with the reply that is already going.";
    public string RunningHint => "A reply is running. Steer slips your message into it, Queue sends it after, Stop & send replaces the reply. Queued messages are kept in this app only.";

    private void InitMidTurnCommands()
    {
        _uiDispatcher = Dispatcher.CurrentDispatcher;
        QueueCommand = new RelayCommand(_ => _ = QueueNowAsync(), _ => CanMidTurn(allowAttachments: false));
        StopAndSendCommand = new RelayCommand(_ => _ = StopAndSendAsync(), _ => CanMidTurn(allowAttachments: true));
        UnqueueCommand = new RelayCommand(p => { if (p is MessageVM m) Unqueue(m); }, p => p is MessageVM { IsQueued: true });
    }

    private bool CanMidTurn(bool allowAttachments) =>
        Shows(ExperienceFeature.MidTurnActions)
        && _busy && !IsEditing && SelectedConversation is not null
        && ((_input.Trim().Length > 0 && (allowAttachments || _attachQueue.Items.Count == 0)) || (allowAttachments && _attachQueue.Items.Count > 0));

    private void RefreshMidTurnCommands()
    {
        Raise(nameof(SendLabel));
        Raise(nameof(RunningActionsVisibility));
        Raise(nameof(MidTurnExtrasVisibility));
        Raise(nameof(SendToolTip));
        (QueueCommand as RelayCommand)?.Refresh();
        (StopAndSendCommand as RelayCommand)?.Refresh();
    }

    // ---- routing from Send ---------------------------------------------------------------------

    private static readonly Regex SideTaskPattern = new(@"^/(btw|bg)(?:\s+(.*))?$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static bool TryParseSideTask(string text, out SideTaskKind kind, out string arg)
    {
        kind = SideTaskKind.Btw;
        arg = "";
        var m = SideTaskPattern.Match(text);
        if (!m.Success) return false;
        kind = m.Groups[1].Value.Equals("bg", StringComparison.OrdinalIgnoreCase) ? SideTaskKind.Background : SideTaskKind.Btw;
        arg = m.Groups[2].Value.Trim();
        return true;
    }

    // ---- queue ---------------------------------------------------------------------------------

    private async Task QueueNowAsync()
    {
        if (!Shows(ExperienceFeature.MidTurnActions)) return;
        var convo = SelectedConversation;
        var text = _input.Trim();
        if (convo is null || text.Length == 0 || !_busy) return;
        InputText = "";
        Enqueue(convo, text);
        await PersistDraftAsync();
    }

    private void Enqueue(ConversationVM convo, string text, bool afterStop = false)
    {
        var bubble = new MessageVM("You", text) { IsQueued = true, Activity = "Queued — sends when the reply finishes (kept in this app only)." };
        convo.Queue.Add(new QueuedPrompt(bubble, text, afterStop));
        RelayoutQueueBubbles(convo);
    }

    /// <summary>Queued bubbles always sit last, in queue order, below the running reply.</summary>
    private void RelayoutQueueBubbles(ConversationVM convo)
    {
        foreach (var q in convo.Queue) { convo.History.Remove(q.Bubble); if (SelectedConversation == convo) Messages.Remove(q.Bubble); }
        foreach (var q in convo.Queue) { convo.History.Add(q.Bubble); if (SelectedConversation == convo) Messages.Add(q.Bubble); }
    }

    private void RemoveQueueBubbles(ConversationVM convo)
    {
        foreach (var q in convo.Queue) { convo.History.Remove(q.Bubble); if (SelectedConversation == convo) Messages.Remove(q.Bubble); }
    }

    private void Unqueue(MessageVM bubble)
    {
        var convo = SelectedConversation;
        var item = convo?.Queue.FirstOrDefault(q => q.Bubble == bubble);
        if (convo is null || item is null) return;
        convo.Queue.Remove(item);
        convo.History.Remove(bubble);
        Messages.Remove(bubble);
        GiveBackToComposer(new[] { item.Text });
        SetRewindNotice("Removed from the queue and put back in the composer. It was not sent.");
    }

    private void GiveBackToComposer(IEnumerable<string> texts)
    {
        var back = string.Join("\n\n", texts);
        if (back.Length == 0) return;
        InputText = _input.Trim().Length == 0 ? back : _input.TrimEnd() + "\n\n" + back;
    }

    /// <summary>
    /// Runs when a turn has ended. A clean finish releases the next queued prompt (only if its chat is still open);
    /// after a stop, error or disconnect nothing is sent automatically — the text goes back to the user — except a prompt
    /// the user explicitly asked to send after a stop that would not settle. Never into a chat that is not open.
    /// </summary>
    private async Task AfterTurnAsync(ConversationVM convo, bool clean)
    {
        var done = _turnDone;
        _turnDone = null;
        done?.TrySetResult();
        if (convo.Queue.Count == 0) return;

        if (SelectedConversation != convo)
        {
            convo.UnsentFromQueue.AddRange(convo.Queue.Select(q => q.Text));   // restored to the composer on return
            RemoveQueueBubbles(convo);
            convo.Queue.Clear();
            return;
        }

        if (!clean)
        {
            var back = convo.Queue.Where(q => !q.AfterStop).ToList();
            if (back.Count > 0)
            {
                foreach (var q in back) { convo.Queue.Remove(q); convo.History.Remove(q.Bubble); Messages.Remove(q.Bubble); }
                GiveBackToComposer(back.Select(q => q.Text));
                SetRewindNotice(back.Count == 1
                    ? "The reply stopped, so your queued message was put back in the composer. Nothing was sent."
                    : $"The reply stopped, so your {back.Count} queued messages were put back in the composer. Nothing was sent.");
            }
        }

        if (convo.Queue.Count > 0 && (clean || convo.Queue[0].AfterStop))
        {
            var next = convo.Queue[0];
            convo.Queue.RemoveAt(0);
            convo.History.Remove(next.Bubble);          // the sent prompt gets a normal bubble; the rest are re-laid below it
            Messages.Remove(next.Bubble);
            await SendCoreAsync(null, next.Text, textOnly: true).ConfigureAwait(true);
        }
    }

    /// <summary>Called when a chat is opened: text that could not be sent while the user was elsewhere comes back.</summary>
    private void RestoreUnsentQueue(ConversationVM convo)
    {
        if (convo.UnsentFromQueue.Count == 0) return;
        var texts = convo.UnsentFromQueue.ToList();
        convo.UnsentFromQueue.Clear();
        GiveBackToComposer(texts);
        SetRewindNotice($"{texts.Count} queued message(s) were not sent because the reply ended while you were in another chat. They are back in the composer.");
    }

    // ---- steer ---------------------------------------------------------------------------------

    private async Task SteerNowAsync(string text)
    {
        var convo = SelectedConversation;
        var turn = _turn;
        if (convo is null || text.Length == 0) return;
        if (!_busy || turn is null)
        {
            // The reply ended between the keypress and now: an ordinary send.
            await SendCoreAsync(null, text).ConfigureAwait(true);
            return;
        }
        if (_transport() is not IMidTurnTransport mid || string.IsNullOrEmpty(convo.SessionId))
        {
            InputText = "";
            Enqueue(convo, text);
            SetRewindNotice("This connection can't steer a running reply, so your message was queued to send after it.");
            return;
        }

        InputText = "";
        var bubble = new MessageVM("You", text);
        convo.History.Add(bubble);
        Messages.Add(bubble);
        RelayoutQueueBubbles(convo);

        SteerOutcome outcome;
        try
        {
            outcome = await mid.SteerAsync(convo.Profile, convo.SessionId!, text, CancellationToken.None).ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            RemoveBubble(convo, bubble);
            InputText = text;                        // the user's words are not lost
            SetRewindNotice("Couldn't steer the reply: " + ex.Message);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No answer: the agent may have read it, so it is not resent and not handed back to go twice.
            bubble.Activity = "Not confirmed: the connection dropped before the gateway answered. It was NOT resent.";
            return;
        }

        switch (outcome)
        {
            case SteerOutcome.Accepted:
                if (_turn == turn)
                {
                    // What streamed so far stays above; the rest of the turn continues below the steer text.
                    var next = new MessageVM("Assistant");
                    convo.History.Add(next);
                    Messages.Add(next);
                    RelayoutQueueBubbles(convo);
                    turn.Swap(next);
                }
                else
                {
                    bubble.Activity = "The reply finished before reading this. The gateway may run it next; reopen the chat to see it. It was NOT resent.";
                }
                break;
            default:
                RemoveBubble(convo, bubble);
                var why = outcome == SteerOutcome.Unsupported
                    ? "This gateway can't steer a running reply, so your message was queued to send after it."
                    : "The reply couldn't take that now, so your message was queued to send after it.";
                if (_busy) { Enqueue(convo, text); SetRewindNotice(why); }
                else if (SelectedConversation != convo) convo.UnsentFromQueue.Add(text);   // the person moved to another chat meanwhile: never send there
                else await SendCoreAsync(null, text).ConfigureAwait(true);   // it ended meanwhile: just send
                break;
        }
    }

    private void RemoveBubble(ConversationVM convo, MessageVM bubble)
    {
        convo.History.Remove(bubble);
        if (SelectedConversation == convo) Messages.Remove(bubble);
    }

    // ---- stop & send ---------------------------------------------------------------------------

    private async Task StopAndSendAsync()
    {
        if (!Shows(ExperienceFeature.MidTurnActions)) return;
        var convo = SelectedConversation;
        var text = _input.Trim();
        if (convo is null || !_busy) return;
        InputText = "";                                  // the new text is held here; queued texts come back to the composer on stop
        var done = _turnDone ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts?.Cancel();                                  // session.interrupt goes out through the turn's own cancellation path
        var settled = await Task.WhenAny(done.Task, Task.Delay(StopSettleTimeout)).ConfigureAwait(true) == done.Task;
        if (text.Length == 0 && _attachQueue.Items.Count == 0) return;
        if (settled && SelectedConversation == convo) { await SendCoreAsync(null, text).ConfigureAwait(true); return; }   // never into a chat the person moved to meanwhile
        // The stopped turn will not settle: send behind it rather than fold into the turn it was meant to replace.
        if (settled) { convo.UnsentFromQueue.Add(text); return; }             // the turn is over and the person is elsewhere: held for the chat it was written in
        Enqueue(convo, text, afterStop: true);
        SetRewindNotice("The reply is taking a while to stop; your message was queued to send after it.");
    }

    // ---- /btw and /bg side tasks ---------------------------------------------------------------

    private async Task RunSideTaskAsync(SideTaskKind kind, string arg)
    {
        var command = kind == SideTaskKind.Btw ? "/btw" : "/bg";
        var convo = SelectedConversation;
        SetRewindNotice("");
        if (convo is null || !_gate.Allow(Surface.Conversation, convo.Profile)) return;
        if (arg.Length == 0)
        {
            SetRewindNotice(kind == SideTaskKind.Btw
                ? "Usage: /btw <question>. It's answered from a snapshot of this chat, without interrupting it."
                : "Usage: /bg <prompt>. A separate agent runs it in the background and its answer shows up here.");
            return;
        }
        if (_transport() is not IMidTurnTransport mid) { SetRewindNotice($"This connection can't run {command}."); return; }

        var card = new MessageVM(command, arg) { IsSideCard = true, Activity = "Working… the answer arrives when the side task finishes." };
        convo.History.Add(card);
        Messages.Add(card);
        RelayoutQueueBubbles(convo);
        try
        {
            if (string.IsNullOrEmpty(convo.SessionId) && !string.IsNullOrEmpty(convo.StoredSessionId) && _transport() is HermesDashboardTransport hermes)
                convo.SessionId = (await hermes.ResumeSessionAsync(convo.Profile, convo.StoredSessionId!).ConfigureAwait(true)).RuntimeSessionId;
            if (string.IsNullOrEmpty(convo.SessionId))
                throw new InvalidOperationException("Send a message in this chat first.");
            EnsureSideTaskSource();
            var taskId = await mid.StartSideTaskAsync(convo.Profile, convo.SessionId!, kind, arg, CancellationToken.None).ConfigureAwait(true);
            if (taskId is null)
            {
                card.Activity = "The gateway started it but gave no task id, so its answer can't be shown here.";
                return;
            }
            if (_sideBuffer.Remove(taskId, out var early)) { ApplySideAnswer(card, early); return; }
            _sideCards[taskId] = card;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            card.Activity = "Couldn't start it: " + ex.Message;
            card.IsFailed = true;
        }
    }

    private void EnsureSideTaskSource()
    {
        var source = _rpc() as IGatewayEventSource;
        if (ReferenceEquals(source, _sideSource)) return;
        if (_sideSource is not null) _sideSource.EventReceived -= OnSideTaskEvent;
        _sideSource = source;
        if (source is not null) source.EventReceived += OnSideTaskEvent;
    }

    private void OnSideTaskEvent(GatewayEvent ev)
    {
        if (!SideTaskEvents.TryParse(ev, out var answer)) return;
        // Events arrive on the RPC thread; cards live on the UI thread.
        if (_uiDispatcher is { } d && !d.CheckAccess()) d.BeginInvoke(() => HandleSideAnswer(ev.SessionId, answer));
        else HandleSideAnswer(ev.SessionId, answer);
    }

    private void HandleSideAnswer(string? sessionId, SideTaskAnswer answer)
    {
        // Only tasks started from here are shown; another client's side task is not ours to display.
        if (answer.TaskId is null) return;
        if (_sideCards.Remove(answer.TaskId, out var card)) { ApplySideAnswer(card, answer); return; }
        _sideBuffer[answer.TaskId] = answer;             // may arrive before the start call returns its task id
        while (_sideBuffer.Count > MaxSideBuffer) _sideBuffer.Remove(_sideBuffer.Keys.First());
    }

    private static void ApplySideAnswer(MessageVM card, SideTaskAnswer answer)
    {
        card.Activity = "";
        card.IsFailed = answer.Failed;
        card.ReplaceText((card.Role == "/btw" ? "Q: " + card.Text + "\n\nA: " : "") + (answer.Failed ? answer.Text[SideTaskEvents.ErrorPrefix.Length..] : answer.Text));
    }
}
