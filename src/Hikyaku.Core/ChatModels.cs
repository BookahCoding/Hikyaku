namespace Hikyaku.Core;

public sealed record ChatMessage(string Role, string Content);
public sealed record ChatRequest(
    ProfileKey Profile,
    IReadOnlyList<ChatMessage> Messages,
    string? SessionId = null,
    string? Model = null,
    string? Provider = null,
    string? ReasoningEffort = null,
    bool? Fast = null,
    /// <summary>Edit/regenerate: cut the stored transcript before this user row and send in its place (<c>prompt.submit truncate_before_row_id</c>). Needs an attached SessionId.</summary>
    long? RewindBeforeRowId = null);
public sealed record ProfileInfo(ProfileKey Key, string DisplayName);

public abstract record ChatEvent;
public sealed record TokenDelta(string Text) : ChatEvent;
public sealed record ToolActivity(string Summary) : ChatEvent;
public sealed record StreamCompleted : ChatEvent;
/// <summary>The stored row of the user prompt just sent (<c>prompt.submit user_row_id</c>); lets that message be edited or regenerated later.</summary>
public sealed record UserRowBound(long RowId) : ChatEvent;
public enum RewindRefusal { Busy, Stale, NoSession }
/// <summary>The gateway (or the client) declined an edit/regenerate; nothing was cut and nothing was resent.</summary>
public sealed record RewindRefused(RewindRefusal Reason, string Message) : ChatEvent;
public sealed record SessionBound(string SessionId, string? StoredSessionId = null) : ChatEvent;
public sealed record ProtocolError(string Message, string? Reason = null) : ChatEvent;
/// <summary>The gateway named (or renamed) the chat: <c>session.title</c> (BookahHermes ChatReducer: title = payload.title).</summary>
public sealed record SessionTitled(string Title) : ChatEvent;
/// <summary>An event type this client does not know; kept for diagnostics, never shown in a reply.</summary>
public sealed record UnknownGatewayEvent(string Type) : ChatEvent;
public sealed record SessionResumeResult(string RuntimeSessionId, bool Running);

/// <summary>The only way the app talks to a gateway. Real implementations live behind this boundary.</summary>
public interface IGatewayTransport
{
    /// <summary>Streams a reply. Cancelling <paramref name="ct"/> must abort the underlying request.</summary>
    IAsyncEnumerable<ChatEvent> StreamChatAsync(ChatRequest request, CancellationToken ct);

    /// <summary>Lists profiles for selection. May throw <see cref="NotSupportedException"/> if the protocol has no documented endpoint.</summary>
    Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(CancellationToken ct);
}

/// <summary>Dashboard auth + profile discovery used by the Hermes remote-gateway transport.</summary>
public interface IHermesDashboardAuth
{
    Task SignInAsync(string provider, string username, string password, CancellationToken ct);
    Task<HermesAuthUser> MeAsync(CancellationToken ct);
    Task<string> MintWsTicketAsync(CancellationToken ct);
    Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(string gatewayId, CancellationToken ct);
    bool HasSession { get; }
}

public sealed record HermesAuthUser(string? UserId, string? DisplayName, string? Email, string? Provider)
{
    public string Label => DisplayName ?? Email ?? UserId ?? "Signed in";
}

/// <summary>OS-protected runtime secret storage. Implementations must never write plaintext to disk.</summary>
public interface ISecretStore
{
    string? Get(string name);
    void Set(string name, string value);
    void Remove(string name);
}
