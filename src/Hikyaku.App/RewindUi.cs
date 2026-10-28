using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>What the user is asked to confirm before the gateway truncates the stored transcript.</summary>
public sealed record RewindConfirmation(bool IsEdit, int MessagesRemoved);

/// <summary>
/// Edit / Regenerate (BookahHermes ChatSession.rewind): the chosen prompt and everything after it are cut here and on
/// the gateway, and the prompt (edited, or its own text again) is sent in its place in one <c>prompt.submit</c>.
/// Between turns only, for a prompt with a stored row, and only after an explicit confirmation. A refusal restores the
/// transcript exactly and nothing is ever resent automatically.
/// </summary>
public sealed partial class MainViewModel
{
    private sealed record RewindPlan(
        MessageVM Target, long RowId, string Display, string Send, bool IsEdit, int Index, IReadOnlyList<MessageVM> Removed);

    private MessageVM? _editTarget;
    private string _draftBeforeEdit = "";
    private string _rewindNotice = "";

    /// <summary>Asked before any cut. Null (or false) means nothing is changed.</summary>
    public Func<RewindConfirmation, bool>? ConfirmRewind { get; set; }

    public ICommand? EditMessageCommand { get; private set; }
    public ICommand? RegenerateMessageCommand { get; private set; }
    public ICommand? CancelEditCommand { get; private set; }

    public bool IsEditing => _editTarget is not null;
    public Visibility EditBannerVisibility => IsEditing ? Visibility.Visible : Visibility.Collapsed;
    public string EditBannerText => "Editing a message. Send replaces it and everything after it.";
    public string RewindNotice => _rewindNotice;
    public Visibility RewindNoticeVisibility => _rewindNotice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void InitRewindCommands()
    {
        EditMessageCommand = new RelayCommand(p => { if (p is MessageVM m) BeginEdit(m); }, CanRewind);
        RegenerateMessageCommand = new RelayCommand(p => { if (p is MessageVM m) _ = RegenerateAsync(m); }, CanRewind);
        CancelEditCommand = new RelayCommand(_ => ExitEditMode(restoreDraft: true), _ => IsEditing);
    }

    private bool CanRewind(object? p) =>
        p is MessageVM m && !_busy && m.IsRewindable && SelectedConversation?.History.Contains(m) == true;

    private void RefreshBusyCommands()
    {
        ((RelayCommand)CancelCommand).Refresh();
        ((RelayCommand)SendCommand).Refresh();
        ((RelayCommand)AttachCommand).Refresh();
        (EditMessageCommand as RelayCommand)?.Refresh();
        (RegenerateMessageCommand as RelayCommand)?.Refresh();
        (ReadAloudMessageCommand as RelayCommand)?.Refresh();
        RefreshBranchCommands();
        RefreshMidTurnCommands();
        (LoadEarlierCommand as RelayCommand)?.Refresh();
        NudgeLive();   // a turn starting or ending changes what the Running filter should show
        RaiseComposerState();
    }

    private void BeginEdit(MessageVM target)
    {
        if (!CanRewind(target)) return;
        SetRewindNotice("");
        if (!IsEditing) _draftBeforeEdit = _input;
        _editTarget = target;
        InputText = target.Text;
        RaiseEditState();
    }

    private void ExitEditMode(bool restoreDraft)
    {
        if (_editTarget is null) return;
        _editTarget = null;
        if (restoreDraft) InputText = _draftBeforeEdit;
        _draftBeforeEdit = "";
        RaiseEditState();
    }

    private void RaiseEditState()
    {
        Raise(nameof(IsEditing));
        Raise(nameof(EditBannerVisibility));
        (CancelEditCommand as RelayCommand)?.Refresh();
    }

    private void SetRewindNotice(string text)
    {
        _rewindNotice = text;
        Raise(nameof(RewindNotice));
        Raise(nameof(RewindNoticeVisibility));
    }

    private Task RegenerateAsync(MessageVM target) =>
        RewindAsync(target, isEdit: false, target.Text, target.SentText ?? target.Text);

    /// <summary>Validates, confirms, cuts the visible transcript, then sends through the normal turn path.</summary>
    private async Task RewindAsync(MessageVM target, bool isEdit, string display, string send)
    {
        SetRewindNotice("");
        var convo = SelectedConversation;
        if (convo is null || !_gate.Allow(Surface.Conversation, convo.Profile)) return;
        if (_busy) { SetRewindNotice("Wait for the reply to finish, then try again."); return; }
        var index = convo.History.IndexOf(target);
        if (index < 0 || target.RowId is not { } row || string.IsNullOrWhiteSpace(send)) return;

        var transport = _transport();
        if (transport is null) { SetRewindNotice("Not connected. Reconnect, then try again."); return; }
        if (string.IsNullOrEmpty(convo.SessionId) && !string.IsNullOrEmpty(convo.StoredSessionId)
            && transport is HermesDashboardTransport hermes)
        {
            try { convo.SessionId = (await hermes.ResumeSessionAsync(convo.Profile, convo.StoredSessionId!).ConfigureAwait(true)).RuntimeSessionId; }
            catch (Exception ex) { SetRewindNotice("Could not reattach to this chat: " + ex.Message); return; }
            if (SelectedConversation != convo) return;
        }
        if (string.IsNullOrEmpty(convo.SessionId)) { SetRewindNotice("This chat is not attached to the gateway. Reconnect, then try again."); return; }

        var removed = convo.History.Skip(index).ToList();
        if (ConfirmRewind is not { } confirm)
        {
            SetRewindNotice("Confirmation is unavailable, so nothing was changed.");
            return;
        }
        if (!confirm(new RewindConfirmation(isEdit, removed.Count))) return;

        // Cut what is shown at once; put back exactly as it was if the gateway turns it down.
        convo.History.RemoveRange(index, removed.Count);
        foreach (var m in removed) Messages.Remove(m);
        if (isEdit) { _editTarget = null; _draftBeforeEdit = ""; InputText = ""; RaiseEditState(); }
        await SendCoreAsync(new RewindPlan(target, row, display, send, isEdit, index, removed)).ConfigureAwait(true);
    }

    /// <summary>A refusal means nothing was cut on the gateway: show the original transcript again and keep the edit.</summary>
    private void RestoreAfterRewindRefused(ConversationVM convo, RewindPlan plan, MessageVM user, MessageVM reply, RewindRefused refused)
    {
        convo.History.Remove(user);
        convo.History.Remove(reply);
        convo.History.InsertRange(Math.Min(plan.Index, convo.History.Count), plan.Removed);
        var visible = SelectedConversation == convo;
        if (visible)
        {
            Messages.Remove(user);
            Messages.Remove(reply);
            for (var i = 0; i < plan.Removed.Count; i++)
                Messages.Insert(Math.Min(plan.Index + i, Messages.Count), plan.Removed[i]);
        }
        convo.Delivery = PromptDeliveryState.Settled;
        if (!visible) return;
        SetRewindNotice(refused.Reason switch
        {
            RewindRefusal.Busy => "Wait for the reply to finish, then try again.",
            RewindRefusal.Stale => "This message can't be changed any more. Reopen the chat to refresh it.",
            _ => refused.Message
        });
        if (plan.IsEdit)
        {
            _editTarget = plan.Target;
            InputText = plan.Display;
            RaiseEditState();
        }
    }
}
