using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>
/// Branch (BookahHermes ChatSession.branch / branchFrom): copy this chat, or everything up to and including a message,
/// into a new stored chat and open it. Non-destructive (the original is untouched), so no confirmation. Counting uses every
/// stored row, not the rows currently shown, and an incomplete read refuses rather than guessing.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _branching;

    public ICommand? BranchFromHereCommand { get; private set; }
    public ICommand? BranchChatCommand { get; private set; }

    private void InitBranchCommands()
    {
        BranchFromHereCommand = new RelayCommand(p => { if (p is MessageVM m) _ = BranchAsync(m); }, CanBranchFrom);
        BranchChatCommand = new RelayCommand(_ => _ = BranchAsync(null), _ => CanBranchChat());
    }

    private bool CanBranchChat() =>
        !_busy && !_branching && SelectedConversation is { StoredSessionId: { Length: > 0 } };

    private bool CanBranchFrom(object? p) =>
        p is MessageVM { RowId: not null } m && CanBranchChat() && SelectedConversation!.History.Contains(m);

    private void RefreshBranchCommands()
    {
        (BranchFromHereCommand as RelayCommand)?.Refresh();
        (BranchChatCommand as RelayCommand)?.Refresh();
    }

    private async Task BranchAsync(MessageVM? from)
    {
        SetRewindNotice("");
        var convo = SelectedConversation;
        if (convo is null || !_gate.Allow(Surface.Conversation, convo.Profile)) return;
        if (_busy) { SetRewindNotice("Wait for the reply to finish, then try again."); return; }
        if (_branching) return;
        if (string.IsNullOrEmpty(convo.StoredSessionId)) { SetRewindNotice("Nothing to branch yet. Send a message first."); return; }
        if (_transport() is not IBranchingTransport branching) { SetRewindNotice("This connection cannot branch chats."); return; }

        _branching = true;
        RefreshBranchCommands();
        try
        {
            if (string.IsNullOrEmpty(convo.SessionId) && _transport() is HermesDashboardTransport hermes)
            {
                try { convo.SessionId = (await hermes.ResumeSessionAsync(convo.Profile, convo.StoredSessionId!).ConfigureAwait(true)).RuntimeSessionId; }
                catch (Exception ex) { SetRewindNotice("Could not reattach to this chat: " + ex.Message); return; }
            }
            if (string.IsNullOrEmpty(convo.SessionId)) { SetRewindNotice("This chat is not attached to the gateway. Reconnect, then try again."); return; }

            int? count = null;
            if (from is not null)
            {
                if (from.RowId is not { } row || _sessions() is not { } catalog) return;
                StoredRows stored;
                try { stored = await catalog.LoadAllMessagesAsync(convo.StoredSessionId!).ConfigureAwait(true); }
                catch (Exception ex) { SetRewindNotice("Couldn't read the whole conversation: " + ex.Message); return; }
                if (!stored.Complete) { SetRewindNotice("This chat is too long to branch from a message. Branch the whole chat instead."); return; }
                count = BranchPlanner.CountThrough(stored.Rows, row);
                if (count is null) { SetRewindNotice("This message can't be branched from any more. Reopen the chat to refresh it."); return; }
            }

            BranchResult result;
            try { result = await branching.BranchSessionAsync(convo.Profile, convo.SessionId!, count, CancellationToken.None).ConfigureAwait(true); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetRewindNotice(ex is RpcException { Code: 4009 } ? "Wait for the reply to finish, then try again." : "Couldn't branch the chat: " + ex.Message);
                return;
            }

            var title = result.Title ?? convo.Title + " (branch)";
            // Don't steal focus if the user has moved on while the gateway was copying.
            var stillHere = SelectedConversation == convo;
            await OpenStoredAsync(result.StoredSessionId, title, convo.Profile, select: stillHere).ConfigureAwait(true);
            if (stillHere) SetRewindNotice($"Branched into “{title}”.");
        }
        finally
        {
            _branching = false;
            RefreshBranchCommands();
        }
    }
}
