using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>
/// Failed-reply recovery. A failed reply gets a card in plain words (<see cref="TurnFailures"/>); the one action it may offer,
/// "Put my message back in the box", only fills the message box with what the user wrote. It never sends, never overwrites a
/// draft already in the box, and is offered only when the message is known not to have arrived (or the gateway itself reported
/// the failure). Resending stays the user's explicit decision with the Send button; nothing here resends.
/// </summary>
public sealed partial class MainViewModel
{
    public ICommand? RestorePromptCommand { get; private set; }

    private void InitFailureCards() =>
        RestorePromptCommand = new RelayCommand(p => RestorePrompt(p as MessageVM));

    private static void ShowTurnFailure(MessageVM reply, ConversationVM convo, TurnFailureKind kind, string? detail, string promptText, bool extras)
    {
        reply.FailedPromptText = promptText;
        reply.Failure = TurnFailures.Describe(kind, detail, convo.Delivery, extras);
    }

    public void RestorePrompt(MessageVM? reply)
    {
        if (reply?.Failure is not { CanRestorePrompt: true } || reply.FailedPromptText.Length == 0) return;
        if (_input.Trim().Length > 0)
        {
            SetRewindNotice("The message box already has text. Send it or clear it first, then put the failed message back.");
            return;
        }
        InputText = reply.FailedPromptText;
        reply.FailedPromptText = "";
        SetRewindNotice("");
    }
}
