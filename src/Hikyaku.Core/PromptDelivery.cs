namespace Hikyaku.Core;

/// <summary>
/// Delivery uncertainty for a submitted prompt (BookahHermes SendCheck).
/// Auto-resend is never allowed — only an explicit user action after NotReceived.
/// </summary>
public enum PromptDeliveryState
{
    /// <summary>prompt.submit has not completed successfully yet.</summary>
    Pending,
    /// <summary>Submit acknowledged; waiting for / receiving tokens.</summary>
    Acknowledged,
    /// <summary>At least one token arrived; stream may still be open.</summary>
    Partial,
    /// <summary>Submit left the wire (or may have) but outcome is unclear after drop.</summary>
    Uncertain,
    /// <summary>Transcript check shows the user turn never arrived — safe for user resend.</summary>
    NotReceived,
    /// <summary>Turn finished (complete or explicit error).</summary>
    Settled
}

public static class PromptResendPolicy
{
    /// <summary>Never auto-resend a user prompt, including after reconnect.</summary>
    public static bool MayAutoResend(PromptDeliveryState state) => false;

    /// <summary>User-initiated resend is only offered when the turn is known missing.</summary>
    public static bool UserMayResend(PromptDeliveryState state) => state == PromptDeliveryState.NotReceived;

    public static PromptDeliveryState AfterSubmitAck() => PromptDeliveryState.Acknowledged;
    public static PromptDeliveryState AfterFirstToken(PromptDeliveryState current)
        => current is PromptDeliveryState.Settled ? current : PromptDeliveryState.Partial;

    public static PromptDeliveryState AfterDisconnect(PromptDeliveryState current) => current switch
    {
        PromptDeliveryState.Pending => PromptDeliveryState.Uncertain,
        PromptDeliveryState.Acknowledged => PromptDeliveryState.Uncertain,
        PromptDeliveryState.Partial => PromptDeliveryState.Partial, // keep partial; recover via resume/refetch
        _ => current
    };
}

/// <summary>
/// Honest session recovery after reconnect: prefer REST transcript refetch when event replay is unavailable.
/// Does not resubmit prompts.
/// </summary>
public static class SessionRecovery
{
    public static async Task<IReadOnlyList<ChatMessage>> RefetchTranscriptAsync(
        SessionsApi api, ProfileKey profile, string storedSessionId, CancellationToken ct)
    {
        var page = await api.MessagesAsync(storedSessionId, profile.ProfileId, limit: 200, ct: ct)
            .ConfigureAwait(false);
        return page.Messages
            .Select(m => new ChatMessage(m.Role, m.Content))
            .ToList();
    }
}
