namespace Hikyaku.Core;

public enum TurnFailureKind
{
    /// <summary>The connection to the gateway dropped while the reply ran.</summary>
    ConnectionDropped,
    /// <summary>The gateway reported an error for the turn.</summary>
    GatewayError,
    /// <summary>A file could not be attached, so the message was never sent.</summary>
    AttachFailed,
    /// <summary>Something unexpected inside Hikyaku.</summary>
    ClientError
}

/// <summary>What the chat shows when a reply fails: what happened, what is known about delivery, and whether the prompt may be put back.</summary>
public sealed record TurnFailureCard(TurnFailureKind Kind, string Title, string Explanation, string DeliveryNote, bool CanRestorePrompt, string? Detail);

/// <summary>
/// Plain-words failure cards. The rules that matter: nothing is ever resent automatically; the "put my message back in the box"
/// offer (which only fills the composer, the user still presses Send) is made only when the message is known not to have arrived,
/// or the gateway itself reported the turn's failure. When delivery is uncertain the card says to check the chat first instead of
/// inviting a duplicate.
/// </summary>
public static class TurnFailures
{
    public const int MaxDetailChars = 300;

    public static TurnFailureCard Describe(TurnFailureKind kind, string? detail, PromptDeliveryState delivery, bool hadAttachmentsOrContext)
    {
        var (title, explanation) = kind switch
        {
            TurnFailureKind.ConnectionDropped => ("The connection dropped", "Hikyaku lost its connection to the gateway while this reply was running."),
            TurnFailureKind.GatewayError => ("The gateway reported an error", "The gateway could not finish this reply."),
            TurnFailureKind.AttachFailed => ("Couldn't attach a file", "A file could not be attached, so your message was not sent."),
            _ => ("Something went wrong in Hikyaku", "Hikyaku hit an unexpected problem while handling this reply.")
        };

        var effective = kind == TurnFailureKind.AttachFailed ? PromptDeliveryState.Pending : delivery;
        var note = kind == TurnFailureKind.GatewayError
            ? "Your message reached the gateway. Nothing is resent automatically."
            : effective switch
            {
                PromptDeliveryState.Pending => "Your message did not reach the gateway. Nothing was sent.",
                PromptDeliveryState.NotReceived => "The chat history shows your message never arrived, so it is safe to send it again.",
                PromptDeliveryState.Partial => "Your message reached the gateway and part of the reply was kept. Nothing is resent automatically.",
                _ => "The gateway may already have your message. Check the chat history before sending it again; nothing is resent automatically."
            };

        var canRestore = kind == TurnFailureKind.GatewayError
            || effective is PromptDeliveryState.Pending or PromptDeliveryState.NotReceived;
        if (canRestore && hadAttachmentsOrContext)
            note += " Attachments and shared terminal text are not restored.";
        return new TurnFailureCard(kind, title, explanation, note, canRestore, Clean(detail));
    }

    /// <summary>The detail the gateway or exception gave, without control characters, bounded.</summary>
    public static string? Clean(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;
        var s = new string(detail.Where(c => !char.IsControl(c) || c == ' ').ToArray()).Trim();
        if (s.Length == 0) return null;
        return s.Length > MaxDetailChars ? s[..MaxDetailChars] + "…" : s;
    }
}
