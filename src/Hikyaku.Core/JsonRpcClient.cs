using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Hikyaku.Core;

/// <summary>Something that announces gateway events (the live RPC client); optional for fakes.</summary>
public interface IGatewayEventSource
{
    event Action<GatewayEvent>? EventReceived;
}

public sealed record GatewayEvent(string Type, JsonNode? Payload, string? SessionId, long? Seq, string? RequestId = null);

public sealed class RpcException(int code, string message, string? reason = null) : Exception(message)
{
    public int Code { get; } = code;
    public string? Reason { get; } = reason;
}

public sealed class RpcTimeoutException(string method) : Exception($"The gateway didn't answer {method} in time.")
{
    public string Method { get; } = method;
}

/// <summary>
/// JSON-RPC 2.0 peer over the dashboard WebSocket (BookahHermes JsonRpcClient.kt / tui_gateway/ws.py).
/// One JSON document per frame; newline-batched frames are split defensively.
/// </summary>
public sealed class JsonRpcClient : IJsonRpcPeer, IGatewayEventSource, IAsyncDisposable
{
    private readonly IRpcTransport _transport;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private long _nextId;
    private readonly Channel<GatewayEvent> _events = Channel.CreateUnbounded<GatewayEvent>();
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _pump;
    private readonly TaskCompletionSource<JsonObject> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception?> _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _capabilitiesSent;
    private Exception? _closedWith;

    public JsonRpcClient(IRpcTransport transport)
    {
        _transport = transport;
        _pump = Task.Run(() => PumpAsync(_pumpCts.Token));
    }

    public Task<JsonObject> Ready => _ready.Task;
    /// <summary>Completes when the frame pump ends; result is the close cause (null = clean end).</summary>
    public Task<Exception?> Closed => _closed.Task;
    public ChannelReader<GatewayEvent> Events => _events.Reader;
    /// <summary>Fan-out for observers (Needs-you) that must not compete with stream consumers on <see cref="Events"/>.</summary>
    public event Action<GatewayEvent>? EventReceived;

    /// <summary>JSON-RPC response to a server→client request (Needs-you answers). Never log result contents — may hold secrets.</summary>
    public Task RespondAsync(string requestId, JsonNode? result, CancellationToken ct = default)
    {
        var frame = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = requestId,
            ["result"] = result ?? new JsonObject()
        };
        return _transport.SendAsync(frame.ToJsonString(HermesJson.Options), ct);
    }

    /// <summary>Decline an unknown/unsupported server request (BookahHermes SESSION_NOT_SHOWN when applicable).</summary>
    public Task RespondErrorAsync(string requestId, int code, string message, string? reason = null, CancellationToken ct = default)
    {
        var err = new JsonObject { ["code"] = code, ["message"] = message };
        if (reason is not null) err["data"] = new JsonObject { ["reason"] = reason };
        var frame = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = requestId, ["error"] = err };
        return _transport.SendAsync(frame.ToJsonString(HermesJson.Options), ct);
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (_closedWith is not null) throw _closedWith;
        var id = "c" + Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var frame = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters ?? new JsonObject()
        };
        try
        {
            await _transport.SendAsync(frame.ToJsonString(HermesJson.Options), ct).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            await using (linked.Token.Register(() =>
            {
                if (ct.IsCancellationRequested) tcs.TrySetCanceled(ct);
                else tcs.TrySetException(new RpcTimeoutException(method));
            }))
                return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        Exception? cause = null;
        try
        {
            await foreach (var text in _transport.ReadFramesAsync(ct).ConfigureAwait(false))
            {
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    await HandleLineAsync(line).ConfigureAwait(false);
            }
            cause = new TransportClosedException(null, "stream ended");
            FailPending(cause);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cause = new TransportClosedException(null, "cancelled");
            FailPending(cause);
        }
        catch (Exception ex)
        {
            cause = ex;
            FailPending(ex);
            if (!_ready.Task.IsCompleted) _ready.TrySetException(ex);
            _events.Writer.TryComplete(ex);
            _closed.TrySetResult(ex);
            return;
        }
        _events.Writer.TryComplete();
        _closed.TrySetResult(cause);
    }

    private async Task HandleLineAsync(string line)
    {
        JsonObject? frame;
        try { frame = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return; }
        if (frame is null) return;

        var idNode = frame["id"];
        var id = idNode switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v when v.TryGetValue<long>(out var n) => n.ToString(),
            _ => null
        };
        var method = frame["method"]?.GetValue<string>();

        if (method == "event")
        {
            await HandleEventAsync(frame["params"] as JsonObject).ConfigureAwait(false);
            return;
        }

        if (method is not null && id is not null)
        {
            var sessionId = (frame["params"] as JsonObject)?["session_id"]?.GetValue<string>();
            await PublishAsync(new GatewayEvent("server.request:" + method, frame["params"], sessionId, null, id))
                .ConfigureAwait(false);
            return;
        }

        if (id is null) return;
        if (!_pending.TryRemove(id, out var tcs)) return;
        if (frame["error"] is JsonObject err)
        {
            var code = err["code"]?.GetValue<int>() ?? -32000;
            var message = err["message"]?.GetValue<string>() ?? "RPC error";
            string? reason = null;
            if (err["data"] is JsonObject data && data["reason"] is JsonValue)
                reason = data["reason"]!.GetValue<string>();
            tcs.TrySetException(new RpcException(code, message, reason));
        }
        else
            tcs.TrySetResult(frame["result"]?.DeepClone());
    }

    private async Task HandleEventAsync(JsonObject? parameters)
    {
        if (parameters is null) return;
        var type = parameters["type"]?.GetValue<string>();
        if (type is null) return;
        var payload = parameters["payload"]?.DeepClone();
        var sessionId = parameters["session_id"]?.GetValue<string>();
        long? seq = null;
        if (parameters["seq"] is JsonValue seqVal && seqVal.TryGetValue<long>(out var s)) seq = s;

        if (type == "gateway.ready")
        {
            var readyPayload = payload as JsonObject ?? new JsonObject();
            _ready.TrySetResult(readyPayload);
            if (Interlocked.Exchange(ref _capabilitiesSent, 1) == 0)
            {
                _ = RequestAsync("client.capabilities", new JsonObject { ["server_requests"] = true });
            }
        }

        await PublishAsync(new GatewayEvent(type, payload, sessionId, seq)).ConfigureAwait(false);
    }

    private async Task PublishAsync(GatewayEvent ev)
    {
        try { EventReceived?.Invoke(ev); }
        catch { /* observers must not break the pump */ }
        await _events.Writer.WriteAsync(ev).ConfigureAwait(false);
    }

    private void FailPending(Exception ex)
    {
        _closedWith = ex;
        foreach (var kv in _pending)
            kv.Value.TrySetException(ex);
        _pending.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        _pumpCts.Cancel();
        try { await _transport.CloseAsync().ConfigureAwait(false); } catch { /* ignore */ }
        try { await _pump.ConfigureAwait(false); } catch { /* pump may throw on close */ }
        _closed.TrySetResult(_closedWith ?? new TransportClosedException(null, "disposed"));
        _pumpCts.Dispose();
    }
}

internal static class HermesJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
