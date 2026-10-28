using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Hikyaku.Core;

/// <summary>
/// Source-backed Hermes dashboard ("remote gateway") transport.
/// Evidence pin: sibling BookahHermes @ 5f00986b2a582e480592fd680131d501d57038ef
/// (AuthApi, GatewayConnection, JsonRpcClient, ChatSession, ProfilesApi; hermes-protocol-research).
/// Live authenticated chat is NOT claimed until a user-entered credential test succeeds.
/// </summary>
public sealed class HermesDashboardTransport : IGatewayTransport, IBranchingTransport, IMidTurnTransport, IAsyncDisposable
{
    public const string ProtocolEvidenceRevision = "5f00986b2a582e480592fd680131d501d57038ef";
    public const string ClientSource = "desktop";
    public const int TerminalColumns = 80;

    private readonly DashboardAuth _auth;
    private readonly string _gatewayId;
    private readonly Func<string, CancellationToken, Task<IRpcTransport>> _openSocket;
    private readonly Func<CancellationToken, Task>? _ensureSignedIn;
    private readonly bool _ownsAuth;
    private readonly GatewayConnection _connection;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ProfileKey> _sessionOwners = new(StringComparer.Ordinal);
    private Action<ConnectionPhase>? _phaseHandler;

    public HermesDashboardTransport(DashboardAuth auth, string gatewayId,
        Func<string, CancellationToken, Task<IRpcTransport>>? openSocket = null, bool ownsAuth = true,
        Func<CancellationToken, Task>? ensureSignedIn = null,
        Func<TimeSpan, CancellationToken, Task>? reconnectDelay = null,
        TimeSpan? readyTimeout = null)
    {
        _auth = auth;
        _gatewayId = gatewayId;
        _ownsAuth = ownsAuth;
        _openSocket = openSocket ?? OpenClientWebSocketAsync;
        _ensureSignedIn = ensureSignedIn;
        _connection = new GatewayConnection(
            async ct =>
            {
                if (_ensureSignedIn is not null && !_auth.HasSession)
                    await _ensureSignedIn(ct).ConfigureAwait(false);
                return await _auth.MintWsTicketAsync(ct).ConfigureAwait(false);
            },
            _openSocket,
            delay: reconnectDelay,
            readyTimeout: readyTimeout);
        _phaseHandler = p => PhaseChanged?.Invoke(p);
        _connection.PhaseChanged += _phaseHandler;
    }

    public DashboardAuth Auth => _auth;
    public string GatewayId => _gatewayId;
    public JsonRpcClient? Client => _connection.Client;
    public ConnectionPhase ConnectionPhase => _connection.Phase;
    public GatewayConnection Connection => _connection;
    public event Action<ConnectionPhase>? PhaseChanged;

    public Task SignInAsync(string provider, string username, string password, CancellationToken ct)
        => _auth.SignInAsync(provider, username, password, ct);

    public Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(CancellationToken ct)
        => _auth.ListProfilesAsync(_gatewayId, ct);

    public SessionsApi CreateSessionsApi() => new(_auth);
    public MediaApi CreateMediaApi() => new(_auth);

    /// <summary>Create a runtime session without submitting a prompt (needed before attachment upload).</summary>
    public async Task<SessionBound> CreateSessionAsync(ProfileKey profile, CancellationToken ct = default,
        SessionModelControls? modelControls = null)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var client = Client ?? throw new InvalidOperationException("Not connected");
        var createParams = new JsonObject
        {
            ["profile"] = profile.ProfileId,
            ["source"] = ClientSource,
            ["cols"] = TerminalColumns
        };
        modelControls?.ApplyToCreateParams(createParams);
        var created = await client.RequestAsync("session.create", createParams, ct: ct).ConfigureAwait(false) as JsonObject
            ?? throw new RpcException(0, "Empty session.create reply");
        var sessionId = created["session_id"]?.GetValue<string>()
            ?? throw new RpcException(0, "session.create returned no session_id");
        _sessionOwners[sessionId] = profile;
        var stored = created["stored_session_id"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(stored)) _sessionOwners[stored] = profile;
        return new SessionBound(sessionId, stored);
    }

    /// <summary>
    /// Resume a stored session (BookahHermes ChatSession.attach / session.resume).
    /// Returns the runtime session_id for prompt.submit. omit_messages=true — history is REST.
    /// </summary>
    public async Task<SessionResumeResult> ResumeSessionAsync(ProfileKey profile, string storedSessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(storedSessionId))
            throw new ArgumentException("stored session id required", nameof(storedSessionId));
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var client = Client ?? throw new InvalidOperationException("Not connected");
        var resumed = await client.RequestAsync("session.resume", new JsonObject
        {
            ["session_id"] = storedSessionId,
            ["profile"] = profile.ProfileId,
            ["source"] = ClientSource,
            ["cols"] = TerminalColumns,
            ["omit_messages"] = true
        }, ct: ct).ConfigureAwait(false) as JsonObject
            ?? throw new RpcException(0, "Empty session.resume reply");
        var runtimeId = resumed["session_id"]?.GetValue<string>()
            ?? throw new RpcException(0, "session.resume returned no session_id");
        if (_sessionOwners.TryGetValue(runtimeId, out var owner) && owner != profile)
            throw new InvalidOperationException("Session belongs to a different profile; refusing cross-profile resume.");
        _sessionOwners[runtimeId] = profile;
        _sessionOwners[storedSessionId] = profile;
        return new SessionResumeResult(runtimeId, resumed["running"]?.GetValue<bool>() == true);
    }

    /// <summary>Drop ownership map entries for a profile (profile deselect / gateway switch).</summary>
    public void ClearSessionOwnersFor(ProfileKey profile)
    {
        foreach (var kv in _sessionOwners.ToArray())
        {
            if (kv.Value == profile)
                _sessionOwners.TryRemove(kv.Key, out _);
        }
    }

    public void ClearAllSessionOwners() => _sessionOwners.Clear();

    /// <summary>
    /// Starts the reconnect loop if needed and waits until Connected.
    /// Fresh ticket every attempt; dead sockets are replaced (BookahHermes GatewayConnection).
    /// </summary>
    public async Task EnsureConnectedAsync(CancellationToken ct)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection.Phase is ConnectionConnected) return;
            _connection.Start();
            await _connection.WaitConnectedAsync(ct).ConfigureAwait(false);
        }
        finally { _connectLock.Release(); }
    }

    /// <summary>Force a reconnect with a fresh ticket (e.g. after transport death observed by chat).</summary>
    public void RequestReconnect() => _connection.RetryNow();

    public async IAsyncEnumerable<ChatEvent> StreamChatAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // A rewind cuts an existing transcript; without an attached session there is nothing to cut.
        if (request.RewindBeforeRowId is not null && string.IsNullOrEmpty(request.SessionId))
        {
            yield return new RewindRefused(RewindRefusal.NoSession, "This chat is not attached to the gateway, so the message cannot be changed.");
            yield break;
        }

        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var client = Client ?? throw new InvalidOperationException("Not connected");

        if (!string.IsNullOrEmpty(request.SessionId))
        {
            if (_sessionOwners.TryGetValue(request.SessionId, out var owner) && owner != request.Profile)
                throw new InvalidOperationException(
                    "Session belongs to a different profile; refusing cross-profile stream reuse.");
            if (!_sessionOwners.ContainsKey(request.SessionId))
                _sessionOwners[request.SessionId] = request.Profile;
        }

        string sessionId;
        if (!string.IsNullOrEmpty(request.SessionId))
            sessionId = request.SessionId;
        else
        {
            var createParams = new JsonObject
            {
                ["profile"] = request.Profile.ProfileId,
                ["source"] = ClientSource,
                ["cols"] = TerminalColumns
            };
            if (!string.IsNullOrWhiteSpace(request.Model)) createParams["model"] = request.Model;
            if (!string.IsNullOrWhiteSpace(request.Provider)) createParams["provider"] = request.Provider;
            if (!string.IsNullOrWhiteSpace(request.ReasoningEffort)) createParams["reasoning_effort"] = request.ReasoningEffort;
            if (request.Fast is { } fast) createParams["fast"] = fast;
            var created = await client.RequestAsync("session.create", createParams, ct: ct).ConfigureAwait(false) as JsonObject
                ?? throw new RpcException(0, "Empty session.create reply");
            sessionId = created["session_id"]?.GetValue<string>()
                ?? throw new RpcException(0, "session.create returned no session_id");
            _sessionOwners[sessionId] = request.Profile;
            var stored = created["stored_session_id"]?.GetValue<string>();
            yield return new SessionBound(sessionId, stored);
        }

        var userText = request.Messages.LastOrDefault(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? "";

        // Event pump uses its own CTS. User cancel must send session.interrupt explicitly
        // (BookahHermes ChatSession.interrupt) — WithCancellation alone throws before the loop body.
        using var eventCts = new CancellationTokenSource();
        var eventEnum = ConsumeSessionEventsAsync(client, sessionId, eventCts.Token).GetAsyncEnumerator(CancellationToken.None);
        var interruptSent = 0;

        async Task InterruptOnceAsync()
        {
            if (Interlocked.Exchange(ref interruptSent, 1) != 0) return;
            try
            {
                await client.RequestAsync("session.interrupt", new JsonObject { ["session_id"] = sessionId },
                    timeout: TimeSpan.FromSeconds(5), ct: CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* best-effort cancel */ }
        }

        JsonObject? submitResult = null;
        Exception? submitError = null;
        var cancelled = false;
        try
        {
            var submit = new JsonObject
            {
                ["session_id"] = sessionId,
                ["text"] = userText
            };
            if (request.RewindBeforeRowId is { } rewindRow)
            {
                // Android ChatSession.rewind: the cut is confirmed by the caller (UI) before this is ever built.
                submit["truncate_before_row_id"] = rewindRow;
                submit["confirm_truncate"] = true;
                submit["confirm_empty_truncate"] = true;
            }
            submitResult = await client.RequestAsync("prompt.submit", submit, ct: ct).ConfigureAwait(false) as JsonObject;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            await InterruptOnceAsync().ConfigureAwait(false);
            eventCts.Cancel();
        }
        catch (Exception ex)
        {
            eventCts.Cancel();
            submitError = ex;
        }

        if (cancelled)
        {
            await eventEnum.DisposeAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            yield break;
        }
        if (submitError is not null)
        {
            await eventEnum.DisposeAsync().ConfigureAwait(false);
            if (request.RewindBeforeRowId is not null && submitError is RpcException refusal
                && ClassifyRewindRefusal(refusal.Code) is { } why)
            {
                yield return new RewindRefused(why, refusal.Message);
                yield break;
            }
            yield return new ProtocolError(submitError.Message,
                submitError is RpcException rpc ? rpc.Reason : null);
            yield break;
        }

        if (ParseRowId(submitResult?["user_row_id"]) is { } userRow)
            yield return new UserRowBound(userRow);

        // Drain events until complete/error, or until ct cancels — interrupt fires before rethrow.
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                await InterruptOnceAsync().ConfigureAwait(false);
                eventCts.Cancel();
                await eventEnum.DisposeAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }

            var moveTask = eventEnum.MoveNextAsync().AsTask();
            var cancelTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => cancelTcs.TrySetResult());
            var winner = await Task.WhenAny(moveTask, cancelTcs.Task).ConfigureAwait(false);
            if (winner != moveTask)
            {
                await InterruptOnceAsync().ConfigureAwait(false);
                eventCts.Cancel();
                try { await moveTask.ConfigureAwait(false); } catch { /* enumerator aborted */ }
                await eventEnum.DisposeAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            var has = await moveTask.ConfigureAwait(false);

            if (!has)
            {
                await eventEnum.DisposeAsync().ConfigureAwait(false);
                yield break;
            }

            var ev = eventEnum.Current;
            yield return ev;
            if (ev is StreamCompleted or ProtocolError)
            {
                eventCts.Cancel();
                await eventEnum.DisposeAsync().ConfigureAwait(false);
                yield break;
            }
        }
    }

    /// <summary>
    /// Copies a chat into a new stored chat (BookahHermes ChatSession.branchKeeping): the whole chat with
    /// <c>session.branch_whole</c> (older gateways only have <c>session.branch</c>, METHOD_NOT_FOUND falls back to it), or
    /// its first <paramref name="count"/> stored rows with <c>session.branch</c>. Non-destructive: the original is untouched.
    /// </summary>
    public async Task<BranchResult> BranchSessionAsync(ProfileKey profile, string runtimeSessionId, int? count, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(runtimeSessionId)) throw new ArgumentException("session id required", nameof(runtimeSessionId));
        if (_sessionOwners.TryGetValue(runtimeSessionId, out var owner) && owner != profile)
            throw new InvalidOperationException("Session belongs to a different profile; refusing cross-profile branch.");
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var client = Client ?? throw new InvalidOperationException("Not connected");

        // A JsonObject can only be sent once, so each request gets its own parameters.
        JsonObject Args()
        {
            var args = new JsonObject { ["session_id"] = runtimeSessionId };
            if (count is { } keep) args["count"] = keep;
            return args;
        }
        JsonObject? result;
        try
        {
            result = await client.RequestAsync(count is null ? "session.branch_whole" : "session.branch", Args(), ct: ct)
                .ConfigureAwait(false) as JsonObject;
        }
        catch (RpcException ex) when (count is null && ex.Code == -32601)
        {
            result = await client.RequestAsync("session.branch", Args(), ct: ct).ConfigureAwait(false) as JsonObject;
        }
        var stored = result?["stored_session_id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(stored)) throw new RpcException(0, "The gateway didn't say where the branch went.");
        var title = result?["title"]?.GetValue<string>();
        return new BranchResult(stored, string.IsNullOrWhiteSpace(title) ? null : title);
    }

    /// <summary>
    /// <c>session.steer</c> (ChatSession.steer): slips text into the running turn. Status "queued" means the turn took it;
    /// any other answer is a rejection (it should run next instead); METHOD_NOT_FOUND / 4010 mean the gateway cannot steer.
    /// </summary>
    public async Task<SteerOutcome> SteerAsync(ProfileKey profile, string runtimeSessionId, string text, CancellationToken ct)
    {
        CheckOwner(profile, runtimeSessionId);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var client = Client ?? throw new InvalidOperationException("Not connected");
        try
        {
            var result = await client.RequestAsync("session.steer",
                new JsonObject { ["session_id"] = runtimeSessionId, ["text"] = text }, ct: ct).ConfigureAwait(false) as JsonObject;
            return result?["status"]?.GetValue<string>() == "queued" ? SteerOutcome.Accepted : SteerOutcome.Rejected;
        }
        catch (RpcException ex) when (ex.Code is -32601 or 4010)
        {
            return SteerOutcome.Unsupported;
        }
    }

    /// <summary><c>prompt.btw</c> / <c>prompt.background</c>: a side agent whose answer comes back as <c>btw.complete</c> / <c>background.complete</c>.</summary>
    public async Task<string?> StartSideTaskAsync(ProfileKey profile, string runtimeSessionId, SideTaskKind kind, string text, CancellationToken ct)
    {
        CheckOwner(profile, runtimeSessionId);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var client = Client ?? throw new InvalidOperationException("Not connected");
        var result = await client.RequestAsync(kind == SideTaskKind.Btw ? "prompt.btw" : "prompt.background",
            new JsonObject { ["session_id"] = runtimeSessionId, ["text"] = text }, ct: ct).ConfigureAwait(false) as JsonObject;
        return result?["task_id"]?.GetValue<string>();
    }

    private void CheckOwner(ProfileKey profile, string runtimeSessionId)
    {
        if (string.IsNullOrWhiteSpace(runtimeSessionId)) throw new ArgumentException("session id required", nameof(runtimeSessionId));
        if (_sessionOwners.TryGetValue(runtimeSessionId, out var owner) && owner != profile)
            throw new InvalidOperationException("Session belongs to a different profile; refusing cross-profile request.");
    }

    /// <summary>The gateway's "busy" (a turn is running, nothing was cut) and the "can't place that row" family.</summary>
    private static RewindRefusal? ClassifyRewindRefusal(int code) => code switch
    {
        4009 => RewindRefusal.Busy,
        4018 or 4030 or 4004 => RewindRefusal.Stale,
        _ => null
    };

    private static long? ParseRowId(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s) && long.TryParse(s, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    private static async IAsyncEnumerable<ChatEvent> ConsumeSessionEventsAsync(
        JsonRpcClient client, string sessionId, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var ev in client.Events.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (ev.Type.StartsWith("server.request:", StringComparison.Ordinal))
                continue; // Needs-you is handled separately; do not surface here.
            if (ev.SessionId is not null && ev.SessionId != sessionId) continue;
            switch (ev.Type)
            {
                case "message.delta":
                    var text = ev.Payload?["text"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(text)) yield return new TokenDelta(text);
                    break;
                case "message.interim":
                    if (ev.Payload?["already_streamed"]?.GetValue<bool>() == true) break;
                    var interim = ev.Payload?["text"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(interim)) yield return new TokenDelta(interim + "\n\n");
                    break;
                case "tool.start":
                    var name = ev.Payload?["name"]?.GetValue<string>() ?? "tool";
                    var preview = ev.Payload?["context"]?.GetValue<string>()
                        ?? ev.Payload?["preview"]?.GetValue<string>();
                    yield return new ToolActivity(preview is null ? name : $"{name}: {preview}");
                    foreach (var live in LiveEvents.From(ev.Type, ev.Payload)) yield return live;   // the step itself (Stage 44)
                    break;
                case "message.complete":
                    if (LiveEvents.Str(ev.Payload?["reasoning"]) is { Length: > 0 } finalReasoning)
                        yield return new FinalReasoning(finalReasoning);
                    yield return new StreamCompleted();
                    yield break;
                case "error":
                    var msg = ev.Payload?["message"]?.GetValue<string>()
                        ?? ev.Payload?["detail"]?.GetValue<string>()
                        ?? "Gateway error event";
                    yield return new ProtocolError(msg);
                    yield break;
                case "session.title":
                    if (ev.Payload?["title"] is JsonValue tv && tv.TryGetValue<string>(out var title) && !string.IsNullOrWhiteSpace(title))
                        yield return new SessionTitled(title.Trim());
                    break;
                case "thinking.delta":
                case "reasoning.delta":
                case "reasoning.available":
                case "tool.generating":
                case "tool.complete":
                case "tool.output_risk":
                case "status.update":
                case "todo.updated":
                case "notice":
                    foreach (var live in LiveEvents.From(ev.Type, ev.Payload)) yield return live;
                    break;
                case "gateway.ready":
                case "message.start":
                case "session.info":
                case "session.usage":
                case "request.cancel":
                case "approval.cancelled":
                    break;
                default:
                    yield return new UnknownGatewayEvent(ev.Type);
                    break;
            }
        }
    }

    private async Task<IRpcTransport> OpenClientWebSocketAsync(string ticket, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("hermes-gateway-v1");
        ws.Options.AddSubProtocol("hermes-gateway-ticket." + ticket);
        // Ticket must not appear in the URL (BookahHermes RpcTransport.kt).
        var uri = DashboardAuth.WebSocketUri(_auth.BaseUri);
        // Cloudflare Access also guards the upgrade; only over wss and only this host's token (never plain ws).
        foreach (var h in _auth.AccessHeadersFor(uri)) ws.Options.SetRequestHeader(h.Key, h.Value);
        await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        return new ClientWebSocketTransport(ws);
    }

    public async ValueTask DisposeAsync()
    {
        if (_phaseHandler is not null)
            _connection.PhaseChanged -= _phaseHandler;
        await _connection.DisposeAsync().ConfigureAwait(false);
        _sessionOwners.Clear();
        if (_ownsAuth) _auth.Dispose();
        _connectLock.Dispose();
    }
}

internal sealed class ClientWebSocketTransport(ClientWebSocket ws) : IRpcTransport
{
    public async IAsyncEnumerable<string> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new TransportClosedException((int?)result.CloseStatus, result.CloseStatusDescription);
                }
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            yield return Encoding.UTF8.GetString(ms.ToArray());
        }
        throw new TransportClosedException(null, "socket not open");
    }

    public async Task SendAsync(string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    public async Task CloseAsync(int code = 1000, string reason = "", CancellationToken ct = default)
    {
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await ws.CloseAsync((WebSocketCloseStatus)code, reason, ct).ConfigureAwait(false);
        ws.Dispose();
    }
}
